using DockerDiagram.Contracts;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DockerDiagram.Infrastructure
{
    public partial class DockerApiService
    {
        // =========================================================
        // 1. ISystemService 구현
        // =========================================================

        /// <summary>
        /// 현재 도커 엔진이 정상적으로 실행 중이고 응답 가능한 상태인지(Ping) 확인합니다.
        /// </summary>
        public async Task<bool> PingAsync()
        {
            try
            {
                await _client.System.PingAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 연결 실패의 실제 예외가 필요한 연결 진단 화면에서 사용합니다.
        /// </summary>
        public Task VerifyConnectionAsync()
        {
            return _client.System.PingAsync();
        }

        /// <summary>
        /// docker events 스트림을 열고 컨테이너/볼륨/네트워크/이미지 변경 이벤트를 전달합니다.
        /// </summary>
        public async Task MonitorDockerEventsAsync(IProgress<Message> progress, CancellationToken cancellationToken)
        {
            EnterEventMonitor();
            try
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _eventsLifetimeCts.Token);

                var filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["type"] = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["container"] = true,
                        ["volume"] = true,
                        ["network"] = true,
                        ["image"] = true
                    }
                };

                var parameters = new ContainerEventsParameters
                {
                    Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                    Filters = filters
                };

                await _eventsClient.System.MonitorEventsAsync(
                    parameters,
                    progress,
                    linkedCancellation.Token);
            }
            finally
            {
                ExitEventMonitor();
            }
        }

        /// <summary>
        /// Docker Engine's system disk usage summary, equivalent to docker system df.
        /// </summary>
        public async Task<SystemDiskUsage> GetSystemDiskUsageAsync()
        {
            if (_systemDiskUsageCache != null &&
                DateTimeOffset.UtcNow - _systemDiskUsageCacheAt < SystemDiskUsageCacheTtl)
            {
                return _systemDiskUsageCache;
            }

            await _systemDiskUsageCacheLock.WaitAsync();
            try
            {
                if (_systemDiskUsageCache != null &&
                    DateTimeOffset.UtcNow - _systemDiskUsageCacheAt < SystemDiskUsageCacheTtl)
                {
                    return _systemDiskUsageCache;
                }

                var imagesTask = _client.Images.ListImagesAsync(
                    new ImagesListParameters { All = true });
                var containersTask = _client.Containers.ListContainersAsync(
                    new ContainersListParameters { All = true, Size = true });
                var volumesTask = _client.Volumes.ListAsync();

                await Task.WhenAll(imagesTask, containersTask, volumesTask);

                var imageRows = imagesTask.Result.Select(image => new SystemDiskUsageImage
                {
                    Id = image.ID,
                    ParentId = image.ParentID,
                    RepoTags = image.RepoTags?.ToList(),
                    RepoDigests = image.RepoDigests?.ToList(),
                    Created = ToUnixTimeSeconds(image.Created),
                    Size = image.Size,
                    SharedSize = image.SharedSize,
                    VirtualSize = image.VirtualSize,
                    Containers = image.Containers <= 0
                        ? 0
                        : (int)Math.Min(int.MaxValue, image.Containers)
                }).ToList();

                var containerRows = containersTask.Result.Select(container => new SystemDiskUsageContainer
                {
                    Id = container.ID,
                    Names = container.Names?.ToList(),
                    Image = container.Image,
                    State = container.State,
                    SizeRw = container.SizeRw,
                    SizeRootFs = container.SizeRootFs
                }).ToList();

                var volumeRows = (volumesTask.Result.Volumes ?? [])
                    .Select(volume => new SystemDiskUsageVolume
                    {
                        Name = volume.Name,
                        Driver = volume.Driver,
                        Mountpoint = volume.Mountpoint,
                        UsageData = volume.UsageData == null
                            ? null
                            : new SystemDiskUsageUsageData
                            {
                                Size = volume.UsageData.Size,
                                RefCount = volume.UsageData.RefCount
                            }
                    })
                    .ToList();

                _systemDiskUsageCache = new SystemDiskUsage
                {
                    Images = imageRows,
                    Containers = containerRows,
                    Volumes = volumeRows,
                    HasLayersSize = false,
                    HasBuildCacheData = false
                };
                _systemDiskUsageCacheAt = DateTimeOffset.UtcNow;
                return _systemDiskUsageCache;
            }
            finally
            {
                _systemDiskUsageCacheLock.Release();
            }
        }

        private static long ToUnixTimeSeconds(DateTime value)
        {
            if (value == default) return 0;

            DateTime utc = value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
            return new DateTimeOffset(utc).ToUnixTimeSeconds();
        }
        public async Task<DockerPruneResult> PruneAsync(DockerPruneOptions options)
        {
            var path = options.Target switch
            {
                DockerPruneTarget.Container => "containers/prune",
                DockerPruneTarget.Image => options.AllImages
                    ? $"images/prune?filters={Uri.EscapeDataString("{\"dangling\":{\"false\":true}}")}"
                    : "images/prune",
                DockerPruneTarget.Volume => "volumes/prune",
                DockerPruneTarget.Network => "networks/prune",
                DockerPruneTarget.System => BuildSystemPrunePath(options),
                _ => throw new NotSupportedException($"지원하지 않는 prune 대상입니다: {options.Target}")
            };

            return await MakeRawDockerRequestAsync<DockerPruneResult>(HttpMethod.Post, path);
        }

        private static string BuildSystemPrunePath(DockerPruneOptions options)
        {
            var query = new List<string>();
            if (options.AllImages) query.Add("all=1");
            if (options.IncludeVolumes) query.Add("volumes=1");
            return query.Count == 0 ? "system/prune" : $"system/prune?{string.Join("&", query)}";
        }

        private async Task<T> MakeRawDockerRequestAsync<T>(HttpMethod method, string path, object? body = null)
        {
            var responseBody = await MakeRawDockerApiRequestAsync(method, path, body);
            var result = JsonConvert.DeserializeObject<T>(responseBody);
            return result ?? throw new InvalidOperationException($"Docker API 응답을 해석할 수 없습니다: {path}");
        }

        private async Task MakeRawDockerRequestAsync(HttpMethod method, string path, object? body = null)
        {
            await MakeRawDockerApiRequestAsync(method, path, body);
        }

        private async Task<string> MakeRawDockerApiRequestAsync(HttpMethod method, string path, object? body = null)
        {
            var requestMethod = typeof(DockerClient)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                    m.Name == "MakeRequestAsync" &&
                    !m.IsGenericMethodDefinition &&
                    m.GetParameters().Length == 8)
                ?? throw new NotSupportedException("Docker.DotNet raw request API를 찾을 수 없습니다.");

            var errorHandlerType = requestMethod.GetParameters()[0].ParameterType.GetGenericArguments()[0];
            var errorHandlers = Array.CreateInstance(errorHandlerType, 0);

            var task = requestMethod.Invoke(_client, new object?[]
            {
                errorHandlers,
                method,
                path,
                null,
                CreateRawRequestContent(body),
                null,
                TimeSpan.FromSeconds(CurrentProfile.Type == EndpointType.SshRemote ? 20 : 10),
                CancellationToken.None
            }) as Task;

            if (task == null)
            {
                throw new InvalidOperationException($"Docker API 요청을 시작할 수 없습니다: {path}");
            }

            await task.ConfigureAwait(false);

            var response = task.GetType().GetProperty("Result")?.GetValue(task);
            var responseBody = response?.GetType()
                .GetProperty("Body", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(response) as string;

            return responseBody ?? string.Empty;
        }

        private static object? CreateRawRequestContent(object? body)
        {
            if (body == null) return null;

            var contentDefinition = typeof(DockerClient).Assembly.GetType("Docker.DotNet.JsonRequestContent`1")
                ?? throw new NotSupportedException("Docker.DotNet JsonRequestContent API를 찾을 수 없습니다.");
            var contentType = contentDefinition.MakeGenericType(body.GetType());

            foreach (var ctor in contentType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .OrderBy(ctor => ctor.GetParameters().Length))
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[1].ParameterType.FullName == "Docker.DotNet.JsonSerializer")
                {
                    var serializer = Activator.CreateInstance(parameters[1].ParameterType, nonPublic: true)
                        ?? throw new NotSupportedException("Docker.DotNet JsonSerializer를 만들 수 없습니다.");
                    return ctor.Invoke(new object?[]
                    {
                        body,
                        serializer
                    });
                }
            }

            throw new NotSupportedException("Docker.DotNet JsonRequestContent 생성자를 찾을 수 없습니다.");
        }

        // =========================================================
        // 2. IContainerService 구현
        // =========================================================

        /// <summary>
        /// 도커 엔진에 존재하는 모든 컨테이너 목록을 가져오고, 화면에 띄우기 좋은 데이터 모델로 변환하여 반환합니다.
        /// </summary>
        public async Task<List<DockerContainer>> GetContainersAsync()
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }
            );

            var result = new List<DockerContainer>();
            foreach (var c in containers)
            {
                var labels = CopyLabels(c.Labels);
                string portStr = "";
                if (c.Ports != null)
                {
                    var ports = new List<string>();
                    foreach (var p in c.Ports ?? Enumerable.Empty<Docker.DotNet.Models.Port>())
                    {
                        var proto = string.IsNullOrWhiteSpace(p.Type) ? "tcp" : p.Type;

                        if (p.PublicPort > 0)
                        {
                            var ipPrefix = string.IsNullOrWhiteSpace(p.IP) ? "" : $"{p.IP}:";
                            ports.Add($"{ipPrefix}{p.PublicPort}->{p.PrivatePort}/{proto}");
                        }
                        else
                        {
                            ports.Add($"{p.PrivatePort}/{proto}");
                        }
                    }
                    portStr = string.Join(", ", ports);
                }

                string composeProjectName = GetLabel(labels, "com.docker.compose.project");
                string templateProjectName = GetLabel(labels, "com.dockerdiagram.project");
                string projectName = FirstNonEmpty(composeProjectName, templateProjectName);
                string projectSource = string.IsNullOrWhiteSpace(composeProjectName)
                    ? (string.IsNullOrWhiteSpace(templateProjectName) ? string.Empty : "Template")
                    : "Compose";
                string resourceName = FirstNonEmpty(
                    GetLabel(labels, "com.docker.compose.service"),
                    GetLabel(labels, "com.dockerdiagram.resource"),
                    c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID);

                int composeContainerNumber = ParseComposeContainerNumber(
                    GetLabel(labels, "com.docker.compose.container-number"));
                if (composeContainerNumber == 0 && projectSource.Equals("Template", StringComparison.OrdinalIgnoreCase))
                    composeContainerNumber = 1;

                result.Add(new DockerContainer
                {
                    Id = c.ID,
                    Name = c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID,
                    Image = c.Image,
                    State = c.State,
                    StateColor = GetContainerStateColor(c.State),
                    Ports = portStr,
                    Labels = labels,
                    ComposeProjectName = projectName,
                    ComposeResourceName = resourceName,
                    ComposeServiceName = resourceName,
                    ComposeContainerNumber = composeContainerNumber,
                    IsComposeOneOff = bool.TryParse(
                        GetLabel(labels, "com.docker.compose.oneoff"),
                        out bool oneOff) && oneOff,
                    ComposeWorkingDirectory = GetLabel(labels, "com.docker.compose.project.working_dir"),
                    ComposeConfigFiles = GetLabel(labels, "com.docker.compose.project.config_files"),
                    ProjectSource = projectSource
                });
            }
            return result;
        }

        private static string GetContainerStateColor(string? state)
        {
            return state?.Trim().ToLowerInvariant() switch
            {
                "running" => "#28a745",
                "paused" or "restarting" => "#ffc107",
                "exited" or "dead" => "#dc3545",
                _ => "#808080"
            };
        }

        public async Task<List<DockerContainer>> GetSwarmServicesAsync()
        {
            var services = await MakeRawDockerRequestAsync<List<SwarmServiceResponse>>(HttpMethod.Get, "services");

            return services.Select(service =>
            {
                string name = service.Spec?.Name ?? service.ID;
                string image = NormalizeImageReference(service.Spec?.TaskTemplate?.ContainerSpec?.Image ?? string.Empty);
                string mode = service.Spec?.Mode?.Replicated != null ? "replicated" :
                    service.Spec?.Mode?.Global != null ? "global" : "unknown";
                ulong desired = service.ServiceStatus?.DesiredTasks
                    ?? service.Spec?.Mode?.Replicated?.Replicas
                    ?? 0;
                ulong running = service.ServiceStatus?.RunningTasks ?? 0;
                var labels = CopyLabels(service.Spec?.Labels);

                return new DockerContainer
                {
                    Id = service.ID,
                    Name = name,
                    Image = image,
                    State = running > 0 || mode.Equals("global", StringComparison.OrdinalIgnoreCase)
                        ? "running"
                        : "stopped",
                    Ports = BuildSwarmServiceSummary(mode, running, desired, service.Endpoint?.Spec?.Ports),
                    Labels = labels,
                    ComposeProjectName = GetLabel(labels, "com.docker.stack.namespace"),
                    ComposeResourceName = name,
                    ComposeServiceName = name,
                    ComposeContainerNumber = 0,
                    ProjectSource = string.IsNullOrWhiteSpace(GetLabel(labels, "com.docker.stack.namespace"))
                        ? "Swarm"
                        : "Swarm Stack",
                    IsSwarmService = true,
                    SwarmMode = mode,
                    SwarmDesiredReplicas = desired,
                    SwarmRunningReplicas = running,
                    StateColor = running > 0 || mode.Equals("global", StringComparison.OrdinalIgnoreCase)
                        ? "#28a745"
                        : "#808080"
                };
            }).ToList();
        }

        public async Task<List<DockerSwarmTask>> GetSwarmServiceTasksAsync(string serviceId)
        {
            if (string.IsNullOrWhiteSpace(serviceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(serviceId));

            string filterJson = JsonConvert.SerializeObject(new
            {
                service = new Dictionary<string, bool> { [serviceId] = true }
            });
            var tasks = await MakeRawDockerRequestAsync<List<SwarmTaskResponse>>(
                HttpMethod.Get,
                $"tasks?filters={Uri.EscapeDataString(filterJson)}");

            var swarmNodes = await GetSwarmNodesAsync();
            var nodeNames = swarmNodes.ToDictionary(
                node => node.Id,
                node => FirstNonEmpty(node.Hostname, node.Name, node.Id),
                StringComparer.OrdinalIgnoreCase);

            return tasks
                .OrderBy(task => task.Slot)
                .ThenBy(task => task.ID, StringComparer.OrdinalIgnoreCase)
                .Select(task =>
                {
                    string state = task.Status?.State ?? string.Empty;
                    string nodeName = !string.IsNullOrWhiteSpace(task.NodeID) &&
                                      nodeNames.TryGetValue(task.NodeID, out string? resolvedNode)
                        ? resolvedNode
                        : FirstNonEmpty(task.NodeID, "-");

                    return new DockerSwarmTask
                    {
                        Id = task.ID,
                        Slot = task.Slot,
                        NodeId = task.NodeID,
                        NodeName = nodeName,
                        DesiredState = task.DesiredState,
                        CurrentState = state,
                        Image = NormalizeImageReference(task.Spec?.ContainerSpec?.Image ?? string.Empty),
                        Error = FirstNonEmpty(task.Status?.Err ?? string.Empty, task.Status?.Message ?? string.Empty),
                        ContainerId = task.Status?.ContainerStatus?.ContainerID ?? string.Empty,
                        StatusColor = GetSwarmTaskStatusColor(state)
                    };
                })
                .ToList();
        }

        public async Task<List<DockerSwarmNode>> GetSwarmNodesAsync()
        {
            var nodes = await MakeRawDockerRequestAsync<List<SwarmNodeResponse>>(HttpMethod.Get, "nodes");
            return nodes
                .Where(node => !string.IsNullOrWhiteSpace(node.ID))
                .OrderByDescending(node => string.Equals(node.Spec?.Role, "manager", StringComparison.OrdinalIgnoreCase))
                .ThenBy(node => node.Description?.Hostname ?? node.ID, StringComparer.OrdinalIgnoreCase)
                .Select(node =>
                {
                    string status = node.Status?.State ?? string.Empty;
                    string hostname = FirstNonEmpty(node.Description?.Hostname ?? string.Empty, node.ID);
                    return new DockerSwarmNode
                    {
                        Id = node.ID,
                        Name = hostname,
                        Hostname = hostname,
                        Role = node.Spec?.Role ?? string.Empty,
                        Availability = node.Spec?.Availability ?? string.Empty,
                        Status = status,
                        Address = node.Status?.Addr ?? string.Empty,
                        ManagerStatus = FirstNonEmpty(
                            node.ManagerStatus?.Leader == true ? "leader" : string.Empty,
                            node.ManagerStatus?.Reachability ?? string.Empty),
                        EngineVersion = node.Description?.Engine?.EngineVersion ?? string.Empty,
                        StateColor = GetSwarmNodeStatusColor(status)
                    };
                })
                .ToList();
        }

        public async Task<object> InspectSwarmServiceRawAsync(string serviceId)
        {
            if (string.IsNullOrWhiteSpace(serviceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(serviceId));

            return await MakeRawDockerRequestAsync<JObject>(
                HttpMethod.Get,
                $"services/{Uri.EscapeDataString(serviceId)}");
        }

        public async Task ScaleSwarmServiceAsync(string serviceId, ulong replicas)
        {
            if (string.IsNullOrWhiteSpace(serviceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(serviceId));

            var service = await MakeRawDockerRequestAsync<JObject>(
                HttpMethod.Get,
                $"services/{Uri.EscapeDataString(serviceId)}");

            var version = service["Version"]?["Index"]?.Value<ulong>()
                ?? throw new InvalidOperationException("Swarm service version 정보를 찾을 수 없습니다.");
            var spec = service["Spec"] as JObject
                ?? throw new InvalidOperationException("Swarm service spec 정보를 찾을 수 없습니다.");
            var mode = spec["Mode"] as JObject
                ?? throw new InvalidOperationException("Swarm service mode 정보를 찾을 수 없습니다.");

            if (mode["Global"] != null)
                throw new NotSupportedException("global mode service는 replica 수를 직접 조절할 수 없습니다.");

            if (mode["Replicated"] is not JObject replicated)
            {
                replicated = new JObject();
                mode["Replicated"] = replicated;
            }

            replicated["Replicas"] = replicas;

            await MakeRawDockerRequestAsync(
                HttpMethod.Post,
                $"services/{Uri.EscapeDataString(serviceId)}/update?version={version}",
                spec);
        }

        public async Task RemoveSwarmServiceAsync(string serviceId)
        {
            if (string.IsNullOrWhiteSpace(serviceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(serviceId));

            await MakeRawDockerRequestAsync(
                HttpMethod.Delete,
                $"services/{Uri.EscapeDataString(serviceId)}");
        }

        public async Task<List<DockerContainer>> GetKubernetesPodsAsync()
        {
            string json = await RunKubectlAsync("get", "pods", "--all-namespaces", "-o", "json");
            var root = JObject.Parse(json);
            var result = new List<DockerContainer>();

            foreach (var item in root["items"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = item["metadata"]?["name"]?.ToString() ?? string.Empty;
                string ns = item["metadata"]?["namespace"]?.ToString() ?? "default";
                string uid = item["metadata"]?["uid"]?.ToString() ?? $"{ns}/{name}";
                string phase = item["status"]?["phase"]?.ToString() ?? "Unknown";
                string podIp = item["status"]?["podIP"]?.ToString() ?? string.Empty;
                string nodeName = item["spec"]?["nodeName"]?.ToString() ?? string.Empty;

                var containers = item["spec"]?["containers"]?.OfType<JObject>().ToList() ?? [];
                var statuses = item["status"]?["containerStatuses"]?.OfType<JObject>().ToList() ?? [];
                int ready = statuses.Count(status => status["ready"]?.Value<bool>() == true);
                int total = containers.Count;
                int restarts = statuses.Sum(status => status["restartCount"]?.Value<int>() ?? 0);
                string images = string.Join(", ", containers
                    .Select(container => container["image"]?.ToString())
                    .Where(image => !string.IsNullOrWhiteSpace(image)));

                result.Add(new DockerContainer
                {
                    Id = uid,
                    Name = $"{ns}/{name}",
                    Image = images,
                    State = phase,
                    Ports = BuildKubernetesPodSummary(ns, ready, total, restarts, podIp, nodeName),
                    StateColor = GetKubernetesStatusColor(phase),
                    IsKubernetesPod = true,
                    KubernetesKind = "Pod",
                    KubernetesApiResource = "pod",
                    KubernetesApiVersion = item["apiVersion"]?.ToString() ?? "v1",
                    KubernetesNamespace = ns,
                    KubernetesNodeName = nodeName,
                    KubernetesReady = total > 0 ? $"{ready}/{total}" : "-",
                    KubernetesRestarts = restarts,
                    KubernetesPodIp = podIp,
                    KubernetesRawJson = item.ToString(Formatting.Indented)
                });
            }

            return result.OrderBy(pod => pod.KubernetesNamespace).ThenBy(pod => pod.Name).ToList();
        }

        public Task<List<DockerContainer>> GetKubernetesDeploymentsAsync() =>
            GetKubernetesResourcesAsync("deployments", "Deployment", BuildKubernetesDeploymentSummary, item => ResolveKubernetesDeploymentState(item));

        public Task<List<DockerContainer>> GetKubernetesReplicaSetsAsync() =>
            GetKubernetesResourcesAsync("replicasets", "ReplicaSet", BuildKubernetesReplicaSetSummary, item => ResolveKubernetesReplicaSetState(item));

        public Task<List<DockerContainer>> GetKubernetesServicesAsync() =>
            GetKubernetesResourcesAsync("services", "Service", BuildKubernetesServiceSummary, item => item["spec"]?["type"]?.ToString() ?? "Service");

        public Task<List<DockerContainer>> GetKubernetesConfigMapsAsync() =>
            GetKubernetesResourcesAsync("configmaps", "ConfigMap", item => BuildKubernetesKeyCountSummary(item, "data"), _ => "ConfigMap");

        public Task<List<DockerContainer>> GetKubernetesSecretsAsync() =>
            GetKubernetesResourcesAsync("secrets", "Secret", BuildKubernetesSecretSummary, _ => "Secret");

        public Task<List<DockerContainer>> GetKubernetesIngressesAsync() =>
            GetKubernetesResourcesAsync("ingresses", "Ingress", BuildKubernetesIngressSummary, _ => "Ingress");

        public Task<List<DockerContainer>> GetKubernetesPersistentVolumeClaimsAsync() =>
            GetKubernetesResourcesAsync("persistentvolumeclaims", "PVC", BuildKubernetesPvcSummary, item => item["status"]?["phase"]?.ToString() ?? "PVC");

        private static async Task<List<DockerContainer>> GetKubernetesResourcesAsync(
            string apiResource,
            string kind,
            Func<JObject, string> summaryFactory,
            Func<JObject, string> stateFactory)
        {
            string json = await RunKubectlAsync("get", apiResource, "--all-namespaces", "-o", "json");
            var root = JObject.Parse(json);
            var result = new List<DockerContainer>();

            foreach (var item in root["items"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = item["metadata"]?["name"]?.ToString() ?? string.Empty;
                string ns = item["metadata"]?["namespace"]?.ToString() ?? "default";
                string uid = item["metadata"]?["uid"]?.ToString() ?? $"{apiResource}:{ns}/{name}";
                string state = stateFactory(item);
                int desiredReplicas = item["spec"]?["replicas"]?.Value<int>() ?? 0;
                int readyReplicas = item["status"]?["readyReplicas"]?.Value<int>() ?? 0;

                result.Add(new DockerContainer
                {
                    Id = uid,
                    Name = $"{ns}/{name}",
                    Image = kind,
                    State = state,
                    Ports = summaryFactory(item),
                    StateColor = GetKubernetesStatusColor(state),
                    IsKubernetesPod = false,
                    KubernetesKind = kind,
                    KubernetesApiResource = apiResource,
                    KubernetesApiVersion = item["apiVersion"]?.ToString() ?? string.Empty,
                    KubernetesNamespace = ns,
                    KubernetesDesiredReplicas = desiredReplicas,
                    KubernetesReadyReplicas = readyReplicas,
                    KubernetesRawJson = item.ToString(Formatting.Indented)
                });
            }

            return result.OrderBy(resource => resource.KubernetesNamespace).ThenBy(resource => resource.Name).ToList();
        }

        public async Task<List<DockerKubernetesNode>> GetKubernetesNodesAsync()
        {
            string json = await RunKubectlAsync("get", "nodes", "-o", "json");
            var root = JObject.Parse(json);
            var result = new List<DockerKubernetesNode>();

            foreach (var item in root["items"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = item["metadata"]?["name"]?.ToString() ?? string.Empty;
                var labels = item["metadata"]?["labels"] as JObject;
                string role = ResolveKubernetesNodeRole(labels);
                string status = ResolveKubernetesNodeStatus(item);
                string internalIp = item["status"]?["addresses"]?
                    .OfType<JObject>()
                    .FirstOrDefault(address => string.Equals(address["type"]?.ToString(), "InternalIP", StringComparison.OrdinalIgnoreCase))?["address"]?.ToString() ?? string.Empty;

                result.Add(new DockerKubernetesNode
                {
                    Id = item["metadata"]?["uid"]?.ToString() ?? name,
                    Name = name,
                    Role = role,
                    Status = status,
                    Version = item["status"]?["nodeInfo"]?["kubeletVersion"]?.ToString() ?? string.Empty,
                    InternalIp = internalIp,
                    OsImage = item["status"]?["nodeInfo"]?["osImage"]?.ToString() ?? string.Empty,
                    StateColor = GetKubernetesStatusColor(status)
                });
            }

            return result.OrderBy(node => node.Name).ToList();
        }

        public async Task<object> InspectKubernetesPodRawAsync(string namespaceName, string podName)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
                throw new ArgumentException("Kubernetes namespace가 비어 있습니다.", nameof(namespaceName));
            if (string.IsNullOrWhiteSpace(podName))
                throw new ArgumentException("Kubernetes pod 이름이 비어 있습니다.", nameof(podName));

            string json = await RunKubectlAsync("get", "pod", podName, "-n", namespaceName, "-o", "json");
            return JObject.Parse(json);
        }

        public async Task<object> InspectKubernetesResourceRawAsync(string apiResource, string namespaceName, string resourceName)
        {
            ValidateKubernetesResourceTarget(apiResource, namespaceName, resourceName);
            string json = await RunKubectlAsync("get", apiResource, resourceName, "-n", namespaceName, "-o", "json");
            return JObject.Parse(json);
        }

        public async Task<string> GetKubernetesResourceYamlAsync(string apiResource, string namespaceName, string resourceName)
        {
            ValidateKubernetesResourceTarget(apiResource, namespaceName, resourceName);
            return await RunKubectlAsync("get", apiResource, resourceName, "-n", namespaceName, "-o", "yaml");
        }

        public async Task<string> DescribeKubernetesResourceAsync(string apiResource, string namespaceName, string resourceName)
        {
            ValidateKubernetesResourceTarget(apiResource, namespaceName, resourceName);
            return await RunKubectlAsync("describe", apiResource, resourceName, "-n", namespaceName);
        }

        public async Task<string> GetKubernetesPodYamlAsync(string namespaceName, string podName)
        {
            ValidateKubernetesPodTarget(namespaceName, podName);
            return await RunKubectlAsync("get", "pod", podName, "-n", namespaceName, "-o", "yaml");
        }

        public async Task<string> DescribeKubernetesPodAsync(string namespaceName, string podName)
        {
            ValidateKubernetesPodTarget(namespaceName, podName);
            return await RunKubectlAsync("describe", "pod", podName, "-n", namespaceName);
        }

        public async Task<string> GetKubernetesPodLogsAsync(string namespaceName, string podName, int tailCount = 500)
        {
            ValidateKubernetesPodTarget(namespaceName, podName);
            return await RunKubectlAsync(
                "logs",
                podName,
                "-n",
                namespaceName,
                "--all-containers=true",
                "--tail",
                tailCount <= 0 ? "-1" : tailCount.ToString());
        }

        public async Task ScaleKubernetesDeploymentAsync(string namespaceName, string deploymentName, int replicas)
        {
            ValidateKubernetesResourceTarget("deployment", namespaceName, deploymentName);
            if (replicas < 0)
                throw new ArgumentOutOfRangeException(nameof(replicas), "Replica 수는 0 이상이어야 합니다.");

            await RunKubectlAsync(
                "scale",
                "deployment",
                deploymentName,
                "-n",
                namespaceName,
                "--replicas",
                replicas.ToString());
        }

        public async Task RolloutRestartKubernetesResourceAsync(string apiResource, string namespaceName, string resourceName)
        {
            ValidateKubernetesResourceTarget(apiResource, namespaceName, resourceName);
            await RunKubectlAsync(
                "rollout",
                "restart",
                NormalizeKubernetesTarget(apiResource),
                resourceName,
                "-n",
                namespaceName);
        }

        public async Task DeleteKubernetesResourceAsync(string apiResource, string namespaceName, string resourceName)
        {
            ValidateKubernetesResourceTarget(apiResource, namespaceName, resourceName);
            await RunKubectlAsync(
                "delete",
                NormalizeKubernetesTarget(apiResource),
                resourceName,
                "-n",
                namespaceName);
        }

        public async Task ApplyKubernetesManifestAsync(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath))
                throw new ArgumentException("Kubernetes manifest 경로가 비어 있습니다.", nameof(manifestPath));
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Kubernetes manifest 파일을 찾을 수 없습니다.", manifestPath);

            await RunKubectlAsync("apply", "-f", manifestPath);
        }

        public void OpenKubernetesLogsFollow(string namespaceName, string podName)
        {
            ValidateKubernetesPodTarget(namespaceName, podName);
            OpenKubectlConsole(
                "logs",
                podName,
                "-n",
                namespaceName,
                "--all-containers=true",
                "-f");
        }

        public void OpenKubernetesPortForward(string apiResource, string namespaceName, string resourceName, int localPort, int remotePort)
        {
            ValidateKubernetesResourceTarget(apiResource, namespaceName, resourceName);
            if (!IsValidPort(localPort))
                throw new ArgumentOutOfRangeException(nameof(localPort), "Local port는 1~65535 사이여야 합니다.");
            if (!IsValidPort(remotePort))
                throw new ArgumentOutOfRangeException(nameof(remotePort), "Remote port는 1~65535 사이여야 합니다.");

            string target = $"{NormalizeKubernetesTarget(apiResource)}/{resourceName}";
            OpenKubectlConsole(
                "port-forward",
                "-n",
                namespaceName,
                target,
                $"{localPort}:{remotePort}");
        }

        private static void ValidateKubernetesPodTarget(string namespaceName, string podName)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
                throw new ArgumentException("Kubernetes namespace가 비어 있습니다.", nameof(namespaceName));
            if (string.IsNullOrWhiteSpace(podName))
                throw new ArgumentException("Kubernetes pod 이름이 비어 있습니다.", nameof(podName));
        }

        private static void ValidateKubernetesResourceTarget(string apiResource, string namespaceName, string resourceName)
        {
            if (string.IsNullOrWhiteSpace(apiResource))
                throw new ArgumentException("Kubernetes resource type이 비어 있습니다.", nameof(apiResource));
            if (string.IsNullOrWhiteSpace(namespaceName))
                throw new ArgumentException("Kubernetes namespace가 비어 있습니다.", nameof(namespaceName));
            if (string.IsNullOrWhiteSpace(resourceName))
                throw new ArgumentException("Kubernetes resource 이름이 비어 있습니다.", nameof(resourceName));
        }

        private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

        private static string NormalizeKubernetesTarget(string apiResource)
        {
            return apiResource.Trim().ToLowerInvariant() switch
            {
                "pod" or "pods" => "pod",
                "deployment" or "deployments" => "deployment",
                "service" or "services" or "svc" => "service",
                "replicaset" or "replicasets" or "rs" => "replicaset",
                "configmap" or "configmaps" or "cm" => "configmap",
                "secret" or "secrets" => "secret",
                "ingress" or "ingresses" or "ing" => "ingress",
                "persistentvolumeclaim" or "persistentvolumeclaims" or "pvc" => "pvc",
                var value => value
            };
        }

        private static void OpenKubectlConsole(params string[] args)
        {
            string command = "kubectl " + string.Join(" ", args.Select(QuoteCommandArgument));
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k " + command,
                UseShellExecute = true
            });
        }

        private static string QuoteCommandArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";

            return value.Any(char.IsWhiteSpace) || value.Contains('"')
                ? "\"" + value.Replace("\"", "\\\"") + "\""
                : value;
        }

        private static async Task<string> RunKubectlAsync(params string[] args)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("kubectl 프로세스를 시작할 수 없습니다.");

            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"kubectl failed with exit code {process.ExitCode}." : stderr.Trim());

            return stdout;
        }

        private static string BuildKubernetesPodSummary(string ns, int ready, int total, int restarts, string podIp, string nodeName)
        {
            string readyText = total > 0 ? $"{ready}/{total} ready" : "ready unknown";
            var parts = new List<string> { ns, readyText, $"restarts {restarts}" };
            if (!string.IsNullOrWhiteSpace(podIp))
                parts.Add(podIp);
            if (!string.IsNullOrWhiteSpace(nodeName))
                parts.Add(nodeName);
            return string.Join(" | ", parts);
        }

        private static string ResolveKubernetesDeploymentState(JObject item)
        {
            int replicas = item["status"]?["replicas"]?.Value<int>() ?? item["spec"]?["replicas"]?.Value<int>() ?? 0;
            int available = item["status"]?["availableReplicas"]?.Value<int>() ?? 0;
            return replicas == 0
                ? "Scaled 0"
                : available >= replicas ? "Available" : "Progressing";
        }

        private static string BuildKubernetesDeploymentSummary(JObject item)
        {
            int desired = item["spec"]?["replicas"]?.Value<int>() ?? 0;
            int ready = item["status"]?["readyReplicas"]?.Value<int>() ?? 0;
            int available = item["status"]?["availableReplicas"]?.Value<int>() ?? 0;
            int updated = item["status"]?["updatedReplicas"]?.Value<int>() ?? 0;
            return $"ready {ready}/{desired} | available {available} | updated {updated}";
        }

        private static string ResolveKubernetesReplicaSetState(JObject item)
        {
            int replicas = item["status"]?["replicas"]?.Value<int>() ?? item["spec"]?["replicas"]?.Value<int>() ?? 0;
            int ready = item["status"]?["readyReplicas"]?.Value<int>() ?? 0;
            return replicas == 0
                ? "Scaled 0"
                : ready >= replicas ? "Available" : "Progressing";
        }

        private static string BuildKubernetesReplicaSetSummary(JObject item)
        {
            int desired = item["spec"]?["replicas"]?.Value<int>() ?? 0;
            int ready = item["status"]?["readyReplicas"]?.Value<int>() ?? 0;
            int available = item["status"]?["availableReplicas"]?.Value<int>() ?? 0;
            return $"ready {ready}/{desired} | available {available}";
        }

        private static string BuildKubernetesServiceSummary(JObject item)
        {
            string type = item["spec"]?["type"]?.ToString() ?? "ClusterIP";
            string clusterIp = item["spec"]?["clusterIP"]?.ToString() ?? "-";
            var ports = item["spec"]?["ports"]?
                .OfType<JObject>()
                .Select(port =>
                {
                    string protocol = port["protocol"]?.ToString() ?? "TCP";
                    string portText = port["port"]?.ToString() ?? "-";
                    string target = port["targetPort"]?.ToString() ?? portText;
                    string nodePort = port["nodePort"]?.ToString() ?? string.Empty;
                    return string.IsNullOrWhiteSpace(nodePort)
                        ? $"{portText}->{target}/{protocol}"
                        : $"{portText}:{nodePort}->{target}/{protocol}";
                })
                .Where(text => !string.IsNullOrWhiteSpace(text)) ?? Enumerable.Empty<string>();
            return $"{type} | {clusterIp} | {string.Join(", ", ports)}";
        }

        private static string BuildKubernetesKeyCountSummary(JObject item, string propertyName)
        {
            int count = (item[propertyName] as JObject)?.Properties().Count() ?? 0;
            return $"{count} keys";
        }

        private static string BuildKubernetesSecretSummary(JObject item)
        {
            string type = item["type"]?.ToString() ?? "Opaque";
            int count = (item["data"] as JObject)?.Properties().Count() ?? 0;
            return $"{type} | {count} keys";
        }

        private static string BuildKubernetesIngressSummary(JObject item)
        {
            var hosts = item["spec"]?["rules"]?
                .OfType<JObject>()
                .Select(rule => rule["host"]?.ToString())
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Take(4)
                .ToList() ?? [];

            return hosts.Count > 0 ? string.Join(", ", hosts) : "no hosts";
        }

        private static string BuildKubernetesPvcSummary(JObject item)
        {
            string phase = item["status"]?["phase"]?.ToString() ?? "Unknown";
            string storageClass = item["spec"]?["storageClassName"]?.ToString() ?? "-";
            string requested = item["spec"]?["resources"]?["requests"]?["storage"]?.ToString() ?? "-";
            string capacity = item["status"]?["capacity"]?["storage"]?.ToString() ?? requested;
            return $"{phase} | {storageClass} | {capacity}";
        }

        private static string ResolveKubernetesNodeRole(JObject? labels)
        {
            if (labels == null)
                return "node";

            var roleLabel = labels.Properties()
                .FirstOrDefault(prop => prop.Name.StartsWith("node-role.kubernetes.io/", StringComparison.OrdinalIgnoreCase));
            if (roleLabel == null)
                return "node";

            string role = roleLabel.Name["node-role.kubernetes.io/".Length..];
            return string.IsNullOrWhiteSpace(role) ? "control-plane" : role;
        }

        private static string ResolveKubernetesNodeStatus(JObject item)
        {
            var ready = item["status"]?["conditions"]?
                .OfType<JObject>()
                .FirstOrDefault(condition => string.Equals(condition["type"]?.ToString(), "Ready", StringComparison.OrdinalIgnoreCase));

            return string.Equals(ready?["status"]?.ToString(), "True", StringComparison.OrdinalIgnoreCase)
                ? "Ready"
                : "NotReady";
        }

        private static string GetKubernetesStatusColor(string state)
        {
            return state.ToLowerInvariant() switch
            {
                "running" or "ready" or "succeeded" or "available" or "bound" or "service" or "configmap" or "secret" or "ingress" => "#28a745",
                "pending" or "containercreating" or "progressing" or "scaled 0" => "#ffc107",
                "failed" or "notready" or "unknown" or "lost" => "#dc3545",
                _ => "#808080"
            };
        }

        private static string NormalizeImageReference(string image)
        {
            int digestIndex = image.IndexOf('@');
            return digestIndex > 0 ? image[..digestIndex] : image;
        }

        private static string BuildSwarmServiceSummary(
            string mode,
            ulong running,
            ulong desired,
            IReadOnlyCollection<SwarmServicePortResponse>? ports)
        {
            string replicaText = mode.Equals("global", StringComparison.OrdinalIgnoreCase)
                ? $"global {running} running"
                : $"replicas {running}/{desired}";
            if (ports == null || ports.Count == 0)
                return replicaText;

            var portText = ports
                .Where(port => port.PublishedPort > 0 || port.TargetPort > 0)
                .Select(port =>
                {
                    string protocol = string.IsNullOrWhiteSpace(port.Protocol) ? "tcp" : port.Protocol;
                    return port.PublishedPort > 0
                        ? $"{port.PublishedPort}->{port.TargetPort}/{protocol}"
                        : $"{port.TargetPort}/{protocol}";
                });

            return $"{replicaText} | {string.Join(", ", portText)}";
        }

        private static string GetSwarmTaskStatusColor(string state)
        {
            return state.ToLowerInvariant() switch
            {
                "running" or "complete" => "#28a745",
                "new" or "pending" or "assigned" or "accepted" or "preparing" or "ready" or "starting" => "#ffc107",
                "failed" or "rejected" or "shutdown" or "orphaned" or "remove" => "#dc3545",
                _ => "#808080"
            };
        }

        private static string GetSwarmNodeStatusColor(string state)
        {
            return state.ToLowerInvariant() switch
            {
                "ready" => "#28a745",
                "down" or "disconnected" or "unknown" => "#dc3545",
                _ => "#808080"
            };
        }
    }
}
