using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using Docker.DotNet.Models;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
                using var timeout = new CancellationTokenSource(DockerHealthCheckTimeout);
                await _client.System.PingAsync(timeout.Token);
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
                    Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
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

                // 일반 images/containers/volumes 목록을 합치면 Volume.UsageData가 채워지지 않아
                // 실제 크기를 알 수 없는 볼륨도 0 B로 오인하게 된다. Docker가 크기와 참조 수를
                // 계산해서 반환하는 공식 system/df 응답을 그대로 사용한다.
                SystemDiskUsage usage = await MakeRawDockerRequestAsync<SystemDiskUsage>(
                    HttpMethod.Get,
                    "system/df");
                bool hasBuildCacheData = usage.BuildCache != null;
                usage.Images ??= [];
                usage.Containers ??= [];
                usage.Volumes ??= [];
                usage.BuildCache ??= [];
                usage.HasLayersSize = true;
                usage.HasBuildCacheData = hasBuildCacheData;

                _systemDiskUsageCache = usage;
                _systemDiskUsageCacheAt = DateTimeOffset.UtcNow;
                return _systemDiskUsageCache;
            }
            finally
            {
                _systemDiskUsageCacheLock.Release();
            }
        }

        private void InvalidateSystemDiskUsageCache()
        {
            _systemDiskUsageCache = null;
            _systemDiskUsageCacheAt = DateTimeOffset.MinValue;
        }

        public async Task<DockerPruneResult> PruneAsync(DockerPruneOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            try
            {
                if (options.Resources != DockerPruneResource.None)
                    return await PruneSelectedResourcesAsync(options);

                if (options.Target == DockerPruneTarget.AllResources)
                    return await PruneAllResourcesAsync();

                if (options.Target == DockerPruneTarget.System)
                    return await PruneSystemAsync(options);

                string path = options.Target switch
                {
                    DockerPruneTarget.Container => "containers/prune",
                    DockerPruneTarget.Image => BuildImagePrunePath(options.AllImages),
                    DockerPruneTarget.Volume => "volumes/prune",
                    DockerPruneTarget.Network => "networks/prune",
                    _ => throw new NotSupportedException($"지원하지 않는 prune 대상입니다: {options.Target}")
                };

                return await MakeRawDockerRequestAsync<DockerPruneResult>(HttpMethod.Post, path);
            }
            finally
            {
                // 성공과 부분 실패 모두 디스크 상태를 바꿀 수 있으므로 다음 조회는 반드시 새로 읽는다.
                InvalidateSystemDiskUsageCache();
            }
        }

        private async Task<DockerPruneResult> PruneSelectedResourcesAsync(DockerPruneOptions options)
        {
            var aggregate = new DockerPruneResult();
            DockerPruneResource resources = options.EffectiveResources;

            if (resources.HasFlag(DockerPruneResource.Containers))
                await PruneSelectedContainersAsync(options.IncludeRunningContainers, aggregate);

            if (resources.HasFlag(DockerPruneResource.Networks))
            {
                await MergePruneStepAsync(
                    aggregate,
                    "네트워크",
                    () => MakeRawDockerRequestAsync<DockerPruneResult>(HttpMethod.Post, "networks/prune"));
            }

            if (resources.HasFlag(DockerPruneResource.Images))
            {
                await MergePruneStepAsync(
                    aggregate,
                    "이미지",
                    () => MakeRawDockerRequestAsync<DockerPruneResult>(
                        HttpMethod.Post,
                        BuildImagePrunePath(options.AllImages)));
            }

            if (resources.HasFlag(DockerPruneResource.BuildCache))
            {
                await MergePruneStepAsync(
                    aggregate,
                    "빌드 캐시",
                    () => MakeRawDockerRequestAsync<DockerPruneResult>(
                        HttpMethod.Post,
                        BuildCachePrunePath(all: true)));
            }

            if (resources.HasFlag(DockerPruneResource.Volumes))
                await PruneSelectedVolumesAsync(options, aggregate);

            return aggregate;
        }

        private async Task PruneSelectedContainersAsync(
            bool includeRunningContainers,
            DockerPruneResult aggregate)
        {
            List<DockerContainer> containers;
            try
            {
                containers = await GetContainersAsync();
            }
            catch (Exception ex)
            {
                aggregate.Failures.Add($"컨테이너 목록 조회: {ex.Message}");
                return;
            }

            foreach (DockerContainer container in containers)
            {
                // 오케스트레이터가 다시 생성하거나 상태를 관리하는 대상은 선택과 무관하게 보호한다.
                if (DockerPruneSafety.IsOrchestratorManaged(container.Labels))
                    continue;

                bool isActive = IsActiveContainer(container);
                if (isActive && !includeRunningContainers)
                    continue;

                try
                {
                    if (isActive)
                    {
                        try
                        {
                            if (container.State.Equals("paused", StringComparison.OrdinalIgnoreCase))
                                await UnpauseContainerAsync(container.Id);

                            await StopContainerAsync(container.Id);
                        }
                        catch (Exception stopException)
                        {
                            // 사용자가 실행 중 삭제를 명시했으므로 정상 종료 실패 시 강제 삭제로 이어 간다.
                            AppLog.Warning(
                                $"컨테이너 '{container.Name}' 정상 종료 실패; 강제 삭제로 전환합니다.",
                                stopException);
                        }
                    }

                    await RemoveContainerAsync(container.Id);
                    if (!aggregate.ContainersDeleted.Contains(container.Id, StringComparer.OrdinalIgnoreCase))
                        aggregate.ContainersDeleted.Add(container.Id);
                }
                catch (Exception ex)
                {
                    aggregate.Failures.Add(
                        $"컨테이너 {DisplayContainerForPrune(container)}: {ex.Message}");
                }
            }
        }

        private async Task PruneSelectedVolumesAsync(
            DockerPruneOptions options,
            DockerPruneResult aggregate)
        {
            List<DockerVolume> volumes;
            try
            {
                volumes = await GetVolumesAsync();
            }
            catch (Exception ex)
            {
                aggregate.Failures.Add($"볼륨 목록 조회: {ex.Message}");
                return;
            }

            foreach (DockerVolume volume in volumes)
            {
                if (string.IsNullOrWhiteSpace(volume.Name))
                    continue;

                bool anonymous = IsAnonymousVolume(volume);
                if (anonymous && !options.IncludeAnonymousVolumes ||
                    !anonymous && !options.IncludeNamedVolumes)
                {
                    continue;
                }

                try
                {
                    List<string> users = await GetContainersUsingVolumeAsync(volume.Name);
                    if (users.Count > 0)
                        continue;

                    await RemoveVolumeAsync(volume.Name, force: false);
                    if (!aggregate.VolumesDeleted.Contains(volume.Name, StringComparer.OrdinalIgnoreCase))
                        aggregate.VolumesDeleted.Add(volume.Name);
                }
                catch (Exception ex)
                {
                    aggregate.Failures.Add($"볼륨 {volume.Name}: {ex.Message}");
                }
            }
        }

        private static bool IsAnonymousVolume(DockerVolume volume)
        {
            if (volume.Labels.ContainsKey("com.docker.volume.anonymous"))
                return true;

            return volume.Name.Length == 64 && volume.Name.All(Uri.IsHexDigit);
        }

        private static string DisplayContainerForPrune(DockerContainer container) =>
            !string.IsNullOrWhiteSpace(container.Name)
                ? container.Name
                : DockerIdFormatter.Shorten(container.Id);

        private static async Task MergePruneStepAsync(
            DockerPruneResult aggregate,
            string stepName,
            Func<Task<DockerPruneResult>> action)
        {
            try
            {
                MergePruneResult(aggregate, await action());
            }
            catch (Exception ex)
            {
                aggregate.Failures.Add($"{stepName}: {ex.Message}");
            }
        }

        private async Task<DockerPruneResult> PruneAllResourcesAsync()
        {
            List<DockerContainer> containers = await GetContainersAsync();
            List<DockerContainer> orchestratorManaged = containers
                .Where(container => DockerPruneSafety.IsOrchestratorManaged(container.Labels))
                .ToList();

            if (orchestratorManaged.Count > 0)
            {
                string names = string.Join(", ", orchestratorManaged
                    .Take(5)
                    .Select(container => string.IsNullOrWhiteSpace(container.Name) ? container.Id : container.Name));
                string remainder = orchestratorManaged.Count > 5
                    ? $" 외 {orchestratorManaged.Count - 5}개"
                    : string.Empty;
                throw new InvalidOperationException(
                    $"Swarm 또는 Kubernetes가 관리하는 컨테이너가 있어 전체 삭제를 중단했습니다: {names}{remainder}. " +
                    "해당 오케스트레이터에서 서비스나 워크로드를 먼저 삭제한 뒤 다시 실행하세요.");
            }

            var forceRemoved = new List<string>();
            foreach (DockerContainer container in containers.Where(IsActiveContainer))
            {
                try
                {
                    if (container.State.Equals("paused", StringComparison.OrdinalIgnoreCase))
                        await UnpauseContainerAsync(container.Id);

                    await StopContainerAsync(container.Id);
                }
                catch (Exception ex)
                {
                    // 정상 종료가 불가능해도 사용자가 선택한 '전체 삭제' 의미를 지키기 위해
                    // Docker의 강제 삭제로 이어 간다. 이 호출도 실패하면 예외를 전달한다.
                    AppLog.Warning(
                        $"컨테이너 '{container.Name}' 정상 종료 실패; 강제 삭제로 전환합니다.",
                        ex);
                    await RemoveContainerAsync(container.Id);
                    forceRemoved.Add(container.Id);
                }
            }

            var options = new DockerPruneOptions
            {
                Target = DockerPruneTarget.System,
                AllImages = true,
                IncludeVolumes = true
            };
            DockerPruneResult result = await PruneSystemAsync(options, includeNamedVolumes: true);
            foreach (string containerId in forceRemoved)
            {
                if (!result.ContainersDeleted.Contains(containerId, StringComparer.OrdinalIgnoreCase))
                    result.ContainersDeleted.Add(containerId);
            }

            return result;
        }

        private static bool IsActiveContainer(DockerContainer container) =>
            container.State.Equals("running", StringComparison.OrdinalIgnoreCase) ||
            container.State.Equals("paused", StringComparison.OrdinalIgnoreCase) ||
            container.State.Equals("restarting", StringComparison.OrdinalIgnoreCase);

        private async Task<DockerPruneResult> PruneSystemAsync(
            DockerPruneOptions options,
            bool includeNamedVolumes = false)
        {
            // Docker Engine API에는 /system/prune 엔드포인트가 없다. docker system prune CLI처럼
            // 각 리소스의 prune API를 순서대로 호출하고 결과를 하나로 합친다.
            var aggregate = new DockerPruneResult();

            MergePruneResult(aggregate, await MakeRawDockerRequestAsync<DockerPruneResult>(
                HttpMethod.Post,
                "containers/prune"));
            MergePruneResult(aggregate, await MakeRawDockerRequestAsync<DockerPruneResult>(
                HttpMethod.Post,
                "networks/prune"));
            MergePruneResult(aggregate, await MakeRawDockerRequestAsync<DockerPruneResult>(
                HttpMethod.Post,
                BuildImagePrunePath(options.AllImages)));
            MergePruneResult(aggregate, await MakeRawDockerRequestAsync<DockerPruneResult>(
                HttpMethod.Post,
                BuildCachePrunePath(options.AllImages)));

            if (options.IncludeVolumes)
            {
                MergePruneResult(aggregate, await MakeRawDockerRequestAsync<DockerPruneResult>(
                    HttpMethod.Post,
                    "volumes/prune"));

                if (includeNamedVolumes)
                {
                    List<DockerVolume> remainingVolumes = await GetVolumesAsync();
                    foreach (DockerVolume volume in remainingVolumes)
                    {
                        if (string.IsNullOrWhiteSpace(volume.Name)) continue;

                        await RemoveVolumeAsync(volume.Name, force: false);
                        if (!aggregate.VolumesDeleted.Contains(volume.Name, StringComparer.OrdinalIgnoreCase))
                            aggregate.VolumesDeleted.Add(volume.Name);
                    }
                }
            }

            return aggregate;
        }

        private static string BuildImagePrunePath(bool allImages) =>
            allImages
                ? $"images/prune?filters={Uri.EscapeDataString("{\"dangling\":{\"false\":true}}")}"
                : "images/prune";

        private static string BuildCachePrunePath(bool all) =>
            all ? "build/prune?all=1" : "build/prune";

        private static void MergePruneResult(DockerPruneResult aggregate, DockerPruneResult result)
        {
            aggregate.ContainersDeleted.AddRange(result.ContainersDeleted);
            aggregate.ImagesDeleted.AddRange(result.ImagesDeleted);
            aggregate.VolumesDeleted.AddRange(result.VolumesDeleted);
            aggregate.NetworksDeleted.AddRange(result.NetworksDeleted);
            aggregate.BuildCacheDeleted.AddRange(result.BuildCacheDeleted);
            aggregate.Failures.AddRange(result.Failures);
            aggregate.SpaceReclaimed += result.SpaceReclaimed;
        }

        private async Task<T> MakeRawDockerRequestAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken cancellationToken = default)
        {
            var responseBody = await MakeRawDockerApiRequestAsync(method, path, body, cancellationToken);
            var result = JsonConvert.DeserializeObject<T>(responseBody);
            return result ?? throw new InvalidOperationException($"Docker API 응답을 해석할 수 없습니다: {path}");
        }

        private async Task MakeRawDockerRequestAsync(HttpMethod method, string path, object? body = null)
        {
            await MakeRawDockerApiRequestAsync(method, path, body);
        }

        private async Task<string> MakeRawDockerApiRequestAsync(HttpMethod method, string path, object? body = null, CancellationToken cancellationToken = default)
        {
            return await _rawApiClient.SendAsync(method, path, body, cancellationToken)
                .ConfigureAwait(false);
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
                        // Docker 목록 API에는 Dockerfile EXPOSE처럼 호스트에 게시되지 않은
                        // 컨테이너 전용 포트도 PublicPort=0으로 포함됩니다.
                        if (p.PublicPort == 0) continue;

                        var proto = string.IsNullOrWhiteSpace(p.Type) ? "tcp" : p.Type;
                        var ipPrefix = string.IsNullOrWhiteSpace(p.IP) ? "" : $"{p.IP}:";
                        ports.Add($"{ipPrefix}{p.PublicPort}->{p.PrivatePort}/{proto}");
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
                "running" => StatusColors.Running,
                "paused" or "restarting" => StatusColors.InProgress,
                "exited" or "dead" => StatusColors.Failed,
                _ => StatusColors.Unknown
            };
        }

        /// <summary>
        /// Docker /info 응답으로 현재 엔진의 Swarm 가입 상태와 로컬 역할을 판별합니다.
        /// nodes API는 Manager 전용이므로 상태 판별에는 사용하지 않습니다.
        /// </summary>
        public Task<SwarmClusterState> GetSwarmStateAsync(
            CancellationToken cancellationToken = default) =>
            GetSwarmStateCoreAsync(cancellationToken);

        private async Task<SwarmClusterState> GetSwarmStateCoreAsync(CancellationToken cancellationToken)
        {
            var info = await MakeRawDockerRequestAsync<JObject>(HttpMethod.Get, "info", cancellationToken: cancellationToken);
            var swarm = info["Swarm"] as JObject;

            if (swarm == null)
            {
                return SwarmClusterState.Create(
                    localNodeState: null,
                    controlAvailable: false,
                    errorMessage: "Docker info response did not include Swarm state.");
            }

            IReadOnlyList<SwarmManagerEndpoint> remoteManagers =
                (swarm["RemoteManagers"] as JArray)?
                    .OfType<JObject>()
                    .Select(manager => new SwarmManagerEndpoint(
                        manager.Value<string>("NodeID") ?? string.Empty,
                        manager.Value<string>("Addr") ?? string.Empty))
                    .Where(manager =>
                        !string.IsNullOrWhiteSpace(manager.NodeId) ||
                        !string.IsNullOrWhiteSpace(manager.Address))
                    .ToArray()
                ?? Array.Empty<SwarmManagerEndpoint>();

            return SwarmClusterState.Create(
                swarm.Value<string>("LocalNodeState"),
                swarm.Value<bool?>("ControlAvailable") ?? false,
                swarm.Value<string>("NodeID"),
                swarm.Value<string>("NodeAddr"),
                swarm.Value<string>("Error"),
                remoteManagers);
        }

        public async Task<string> InitializeSwarmAsync(
            SwarmInitializeOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();

            SwarmClusterState currentState = await GetSwarmStateCoreAsync(cancellationToken);
            if (currentState.Membership != SwarmMembershipState.Inactive)
            {
                throw new InvalidOperationException(
                    $"Swarm을 초기화하려면 Docker Engine이 미가입 상태여야 합니다. 현재 상태: {currentState.Membership}");
            }

            string response = await _client.Swarm.InitSwarmAsync(
                new SwarmInitParameters
                {
                    ListenAddr = options.ListenAddress.Trim(),
                    AdvertiseAddr = options.AdvertiseAddress.Trim(),
                    DataPathAddr = options.DataPathAddress.Trim(),
                    DataPathPort = options.DataPathPort,
                    AutoLockManagers = options.AutoLockManagers,
                    Availability = options.Availability.Trim().ToLowerInvariant()
                },
                cancellationToken);
            // Docker.DotNet은 init의 JSON 문자열 응답 본문을 그대로 반환합니다.
            string nodeId = response.Trim();
            if (nodeId.StartsWith('"'))
                nodeId = JsonConvert.DeserializeObject<string>(nodeId) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new InvalidOperationException("Swarm 초기화 응답에 Node ID가 없습니다. 현재 상태를 다시 확인해 주세요.");
            return nodeId;
        }

        public async Task<SwarmJoinTokens> GetJoinTokensAsync(
            CancellationToken cancellationToken = default)
        {
            SwarmClusterState currentState = await GetSwarmStateCoreAsync(cancellationToken);
            if (!currentState.IsManager)
                throw new InvalidOperationException("Swarm Join Token은 Manager에서만 조회할 수 있습니다.");

            SwarmInspectResponse swarm = await _client.Swarm.InspectSwarmAsync(cancellationToken);
            string workerToken = swarm.JoinTokens?.Worker ?? string.Empty;
            string managerToken = swarm.JoinTokens?.Manager ?? string.Empty;

            if (string.IsNullOrWhiteSpace(workerToken) || string.IsNullOrWhiteSpace(managerToken))
                throw new InvalidOperationException("Manager가 유효한 Swarm Join Token을 반환하지 않았습니다.");

            return new SwarmJoinTokens(workerToken, managerToken);
        }

        public async Task JoinSwarmAsync(
            SwarmJoinOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();

            SwarmClusterState currentState = await GetSwarmStateCoreAsync(cancellationToken);
            if (currentState.Membership != SwarmMembershipState.Inactive)
            {
                throw new InvalidOperationException(
                    $"Swarm에 참가하려면 Docker Engine이 미가입 상태여야 합니다. 현재 상태: {currentState.Membership}");
            }

            await _client.Swarm.JoinSwarmAsync(
                new SwarmJoinParameters
                {
                    ListenAddr = options.ListenAddress.Trim(),
                    AdvertiseAddr = options.AdvertiseAddress.Trim(),
                    DataPathAddr = options.DataPathAddress.Trim(),
                    RemoteAddrs = options.GetNormalizedManagerAddresses().ToList(),
                    JoinToken = options.JoinToken.Trim(),
                    Availability = options.Availability.Trim().ToLowerInvariant()
                },
                cancellationToken);
        }

        public async Task LeaveSwarmAsync(
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            SwarmClusterState currentState = await GetSwarmStateCoreAsync(cancellationToken);
            if (!currentState.IsActive)
            {
                throw new InvalidOperationException(
                    $"활성 Swarm 노드만 탈퇴할 수 있습니다. 현재 상태: {currentState.Membership}");
            }

            // force는 현재 노드만 있는 Manager 클러스터의 명시적 종료에만 허용합니다.
            var nodes = currentState.IsManager ? await GetSwarmNodesAsync().WaitAsync(cancellationToken) : null;
            SwarmLeavePlan plan = SwarmLeavePolicy.Evaluate(currentState, nodes);
            if (force != plan.Force)
                throw new InvalidOperationException("현재 노드 역할에 맞는 탈퇴 방식이 아닙니다. 탈퇴 조건을 다시 확인해 주세요.");
            cancellationToken.ThrowIfCancellationRequested();

            await _client.Swarm.LeaveSwarmAsync(
                new SwarmLeaveParameters { Force = force },
                cancellationToken);
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
                        ? StatusColors.Running
                        : StatusColors.Unknown
                };
            }).ToList();
        }

        public async Task<List<DockerSwarmTask>> GetSwarmServiceTasksAsync(string serviceId)
        {
            SwarmServiceTaskSnapshot snapshot = await GetSwarmServiceTaskSnapshotAsync(serviceId);
            return snapshot.Tasks.ToList();
        }

        public async Task<SwarmServiceTaskSnapshot> GetSwarmServiceTaskSnapshotAsync(string serviceId)
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

            List<DockerSwarmTask> mappedTasks = tasks
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

            return new SwarmServiceTaskSnapshot(mappedTasks, swarmNodes);
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
                replicated = [];
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
                tailCount <= 0
                    ? "-1"
                    : tailCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                replicas.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            using var timeout = new CancellationTokenSource(SwarmOperationTiming.RuntimeQueryTimeout);
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

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception killException)
                {
                    AppLog.Warning("취소된 kubectl 프로세스 종료 실패", killException);
                }

                throw new TimeoutException("kubectl 명령이 60초 안에 완료되지 않아 중단했습니다.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

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
                "running" or "ready" or "succeeded" or "available" or "bound" or "service" or "configmap" or "secret" or "ingress" => StatusColors.Running,
                "pending" or "containercreating" or "progressing" or "scaled 0" => StatusColors.InProgress,
                "failed" or "notready" or "unknown" or "lost" => StatusColors.Failed,
                _ => StatusColors.Unknown
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
                "running" or "complete" => StatusColors.Running,
                "new" or "pending" or "assigned" or "accepted" or "preparing" or "ready" or "starting" => StatusColors.InProgress,
                "failed" or "rejected" or "shutdown" or "orphaned" or "remove" => StatusColors.Failed,
                _ => StatusColors.Unknown
            };
        }

        private static string GetSwarmNodeStatusColor(string state)
        {
            return state.ToLowerInvariant() switch
            {
                "ready" => StatusColors.Running,
                "down" or "disconnected" or "unknown" => StatusColors.Failed,
                _ => StatusColors.Unknown
            };
        }
    }
}
