using DockerDiagram.Diagram;
using DockerDiagram.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// 외부의 docker-compose.yml 파일을 읽어와 다이어그램 화면(Sheet)으로 시각화하고,
    /// 사용자의 선택에 따라 실제 도커 엔진에 배포(docker compose up)까지 수행하는 정적 서비스 클래스입니다.
    /// </summary>
    public static class ComposeImportService
    {
        /// <summary>
        /// 사용자에게 다이얼로그를 띄워 YAML 파일을 선택받고, 이를 분석하여 새로운 시트를 생성합니다.
        /// </summary>
        public static async Task ImportFromCompose(
            MainViewModel mainVm,
            IContainerService containerService,
            IVolumeService volumeService,
            INetworkService networkService,
            IDialogService dialogService,
            IComposeService composeService)
        {
            string? selectedFileName = dialogService.ShowOpenFileDialog(
                "Docker Compose File (*.yml;*.yaml)|*.yml;*.yaml|All Files (*.*)|*.*",
                "Import from Docker Compose");

            if (string.IsNullOrEmpty(selectedFileName)) return;

            try
            {
                string yamlContent = File.ReadAllText(selectedFileName);

                // YamlDotNet을 사용하여 YAML 문자열을 C# 객체(ComposeFileModel)로 역직렬화(Deserialize)
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance) // container_name -> ContainerName 자동 변환
                    .IgnoreUnmatchedProperties() // 모델에 없는 속성은 무시하여 에러 방지
                    .Build();

                var composeData = deserializer.Deserialize<ComposeFileModel>(yamlContent);
                var rawComposeRoot = ComposeYamlHelper.ParseMapping(yamlContent);

                if (composeData == null || composeData.Services == null || composeData.Services.Count == 0)
                {
                    dialogService.ShowMessage("유효한 Compose 파일이 아니거나 Service가 없습니다.");
                    return;
                }

                // 탭(Sheet) 이름을 Compose 파일명으로 설정하여 시각적 인지 향상
                string sheetName = $"Compose ({Path.GetFileName(selectedFileName)})";

                // 현재 활성 시트의 연결 프로필과 Docker 서비스를 사용합니다.
                ConnectionProfile targetProfile;
                IDockerService targetDockerService;

                if (mainVm.ActiveSheet != null)
                {
                    // 현재 보고 있는 탭이 있다면 그 탭의 접속 정보(로컬이든 SSH든)를 그대로 사용
                    targetProfile = mainVm.ActiveSheet.Profile;
                    targetDockerService = mainVm.ActiveSheet.DockerService;
                }
                else
                {
                    // 열려있는 탭이 하나도 없을 때만 기본 로컬 환경으로 폴백(Fallback)
                    targetProfile = new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
                    targetDockerService = (IDockerService)containerService; // 주입받은 기본 로컬 서비스
                }

                var newSheet = new SheetViewModel(sheetName, targetProfile, targetDockerService, dialogService)
                {
                    ComposeRawYaml = yamlContent
                };
                string layoutInstanceId = Guid.NewGuid().ToString("N");
                // =================================================================

                var currentContainerSvc = (IContainerService)targetDockerService;
                var currentVolumeSvc = (IVolumeService)targetDockerService;
                var currentNetworkSvc = (INetworkService)targetDockerService;

                var nodeMap = new Dictionary<string, NodeViewModel>();
                // =================================================================
                // 1단계: YAML의 Volumes와 Services를 분석하여 시각적 노드 객체 생성
                // =================================================================
                if (composeData.Volumes != null)
                {
                    foreach (var vol in composeData.Volumes)
                    {
                        nodeMap[vol.Key] = new NodeViewModel(currentContainerSvc, currentVolumeSvc, dialogService)
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = vol.Key,
                            Type = NodeType.Volume,
                            Width = 160,
                            Height = 80,
                            ComposeLayoutInstanceId = layoutInstanceId
                        };
                        ApplyComposeVolumeSettings(nodeMap[vol.Key], rawComposeRoot, vol.Key);
                        newSheet.Nodes.Add(nodeMap[vol.Key]);
                    }
                }

                foreach (var svc in composeData.Services)
                {
                    var portBindings = ComposeYamlHelper.ToPortBindingList(svc.Value.Ports);
                    var envVars = ComposeYamlHelper.ToEnvironmentList(svc.Value.Environment);

                    nodeMap[svc.Key] = new NodeViewModel(currentContainerSvc, currentVolumeSvc, dialogService)
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = !string.IsNullOrEmpty(svc.Value.ContainerName) ? svc.Value.ContainerName : svc.Key,
                        ImageName = svc.Value.Image ?? ComposeYamlHelper.GetBuildLabel(svc.Value.Build) ?? "unknown-image",
                        Type = NodeType.Container,
                        Width = 160,
                        Height = 80,
                        RestartPolicy = svc.Value.Restart ?? "no",
                        PortBindings = portBindings,
                        EnvironmentVariables = envVars,
                        ComposeLayoutInstanceId = layoutInstanceId,
                        ComposeServiceName = svc.Key,
                        ComposeRawServiceYaml = ComposeYamlHelper.GetServiceYaml(rawComposeRoot, svc.Key)
                    };
                    newSheet.Nodes.Add(nodeMap[svc.Key]);
                }

                // =================================================================
                // 2단계: 네트워크 분류 및 의존성/볼륨 선 긋기 로직
                // =================================================================
                var visualGroupMap = new Dictionary<string, List<NodeViewModel>>();
                var dependencyMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                if (composeData.Networks != null)
                {
                    foreach (var net in composeData.Networks.Keys)
                        visualGroupMap[net] = new List<NodeViewModel>();
                }

                foreach (var svc in composeData.Services)
                {
                    if (!nodeMap.TryGetValue(svc.Key, out var sourceContainer)) continue;

                    var serviceNetworks = ComposeYamlHelper.ToNetworkNames(svc.Value.Networks);

                    if (serviceNetworks.Count > 0)
                    {
                        foreach (var netName in serviceNetworks)
                        {
                            if (!visualGroupMap.ContainsKey(netName)) visualGroupMap[netName] = new List<NodeViewModel>();
                            if (!visualGroupMap[netName].Contains(sourceContainer)) visualGroupMap[netName].Add(sourceContainer);

                            var networkOptions = ComposeYamlHelper.GetNetworkOptions(svc.Value.Networks, netName);
                            if (networkOptions.HasAnyOption)
                                sourceContainer.NetworkOptionsMap[netName] = networkOptions;
                            if (!string.IsNullOrWhiteSpace(networkOptions.StaticIPv4))
                                sourceContainer.NetworkIpMap[netName] = networkOptions.StaticIPv4;
                        }
                    }
                    else
                    {
                        const string defaultNetworkName = "default";
                        if (!visualGroupMap.ContainsKey(defaultNetworkName))
                            visualGroupMap[defaultNetworkName] = new List<NodeViewModel>();
                        visualGroupMap[defaultNetworkName].Add(sourceContainer);
                    }
                    // 볼륨 마운트 선 연결
                    var volumeMounts = ComposeYamlHelper.ToVolumeMounts(svc.Value.Volumes);
                    if (volumeMounts.Count > 0)
                    {
                        foreach (var mount in volumeMounts)
                        {
                            string volName = mount.Source;
                            string mountPath = mount.Target;

                            if (!nodeMap.TryGetValue(volName, out var targetVolume))
                            {
                                targetVolume = new NodeViewModel(currentContainerSvc, currentVolumeSvc, dialogService)
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    Name = volName,
                                    Type = NodeType.Volume,
                                    Width = 160,
                                    Height = 80,
                                    ComposeLayoutInstanceId = layoutInstanceId
                                };
                                ApplyComposeVolumeSettings(targetVolume, rawComposeRoot, volName);
                                nodeMap[volName] = targetVolume;
                                newSheet.Nodes.Add(targetVolume);
                            }

                            if (!newSheet.Connectors.Any(c => c.Source == sourceContainer && c.Target == targetVolume))
                            {
                                newSheet.Connectors.Add(new ConnectorViewModel(sourceContainer, targetVolume, PortDirection.Right, PortDirection.Left, dialogService)
                                {
                                    RelationType = RelationType.VolumeMount,
                                    MountPath = mountPath
                                });
                            }
                        }
                    }

                    // 의존성(depends_on) 선 연결
                    var dependsOn = ComposeYamlHelper.ToDependsOnServiceNames(svc.Value.DependsOn);
                    dependencyMap[svc.Key] = dependsOn;
                    if (dependsOn.Count > 0)
                    {
                        foreach (var dep in dependsOn)
                        {
                            if (nodeMap.TryGetValue(dep, out var targetContainer))
                            {
                                newSheet.Connectors.Add(new ConnectorViewModel(targetContainer, sourceContainer, PortDirection.Right, PortDirection.Left, dialogService)
                                {
                                    RelationType = RelationType.Dependency
                                });
                            }
                        }
                    }
                }

                // =================================================================
                // 3단계: depends_on 기반 상단 정렬 트리와 네트워크 영역 배치
                // =================================================================
                foreach (var kvp in visualGroupMap)
                {
                    string netName = kvp.Key;
                    var nodesInGroup = kvp.Value;

                    var groupVm = new GroupViewModel(50, 50, 220, 150, currentNetworkSvc, dialogService, netName, GroupType.Network)
                    {
                        Id = Guid.NewGuid().ToString(),
                        ComposeLayoutInstanceId = layoutInstanceId,
                        ParentSheet = newSheet // 그룹이 소속된 시트를 명시적으로 할당
                    };
                    ApplyComposeNetworkSettings(groupVm, rawComposeRoot, netName);

                    newSheet.Groups.Add(groupVm);
                    foreach (NodeViewModel node in nodesInGroup.Distinct())
                        await groupVm.AddNodeAsync(node, isRestoring: true);
                }

                var serviceNodeMap = composeData.Services.Keys.ToDictionary(
                    serviceName => serviceName,
                    serviceName => nodeMap[serviceName],
                    StringComparer.OrdinalIgnoreCase);
                ComposeDiagramLayoutService.Arrange(newSheet, serviceNodeMap, dependencyMap, 50, 50);

                mainVm.SheetManager.AddExistingSheet(newSheet);

                // =================================================================
                // 4단계: 실제 도커 엔진에 배포(docker compose up)할지 묻는 프로세스
                // =================================================================
                bool isYes = dialogService.ShowConfirm(
                    $"[{sheetName}] 화면 구성을 완료했습니다.\n이 구성대로 실제 도커 엔진에 컨테이너들을 배포(실행)하시겠습니까?",
                    "실제 도커 배포");

                if (isYes)
                {
                    var containerNodes = newSheet.Nodes.Where(n => n.Type == NodeType.Container).ToList();
                    foreach (var node in containerNodes) node.IsCreating = true; // UI에 로딩 스피너 표시용

                    try
                    {
                        var composeResult = await composeService.UpAsync(selectedFileName, targetProfile);

                        foreach (var node in containerNodes) node.IsCreating = false;

                        if (composeResult.Success)
                        {
                            // 배포 대상이 원격일 수도 있으므로, 동적으로 결정된 targetDockerService를 사용
                            var containerSvc = (IContainerService)targetDockerService;
                            var allContainers = await containerSvc.GetContainersAsync();
                            string composeProjectName = DetectComposeProjectName(
                                allContainers,
                                selectedFileName,
                                composeData);

                            if (!string.IsNullOrWhiteSpace(composeProjectName) &&
                                newSheet.Groups.FirstOrDefault(group =>
                                    group.Type == GroupType.Network &&
                                    string.Equals(group.Title, "default", StringComparison.OrdinalIgnoreCase)) is GroupViewModel defaultNetwork)
                            {
                                defaultNetwork.ComposeNetworkName = $"{composeProjectName}_default";
                            }

                            // 배포된 컨테이너들의 실제 ID를 매핑하여 상세 정보를 다시 불러옴
                            foreach (var node in containerNodes)
                            {
                                var matched = FindComposeContainer(
                                    allContainers,
                                    composeProjectName,
                                    node.ComposeServiceName,
                                    node.Name);

                                if (matched != null)
                                {
                                    node.ContainerId = matched.Id;
                                    node.ComposeProjectName = matched.ComposeProjectName;
                                    node.ComposeServiceName = matched.ComposeServiceName;
                                    node.ComposeContainerNumber = matched.ComposeContainerNumber;
                                    await node.RefreshDetailsAsync(); // 실시간 상태(CPU, 메모리 등) 갱신
                                }
                            }

                            dialogService.ShowInfo($"성공적으로 도커 엔진에 배포되었습니다!\n\n(서비스: {composeData.Services.Count}개)", "배포 완료");
                        }
                        else
                        {
                            dialogService.ShowMessage($"도커 배포 중 오류가 발생했습니다:\n{composeResult.CombinedOutput}");
                        }
                    }
                    catch (Exception ex)
                    {
                        foreach (var node in containerNodes) node.IsCreating = false;
                        dialogService.ShowMessage($"도커 배포 실패 (도커 엔진 상태 및 명령어 확인 필요):\n{ex.Message}");
                    }
                }
                else
                {
                    dialogService.ShowInfo("도커 배포를 취소했습니다. 화면에서 시각적 구성만 확인하실 수 있습니다.", "불러오기 완료");
                }
            }
            catch (Exception ex)
            {
                dialogService.ShowMessage($"불러오기 실패: {ex.Message}");
            }
        }

        private static string DetectComposeProjectName(
            IReadOnlyCollection<DockerContainer> containers,
            string composeFilePath,
            ComposeFileModel composeData)
        {
            var configMatches = containers
                .Where(container => container.IsComposeManaged)
                .Where(container => ComposeConfigContainsFile(container, composeFilePath))
                .Select(container => container.ComposeProjectName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (configMatches.Count == 1)
                return configMatches[0];

            if (!string.IsNullOrWhiteSpace(composeData.Name))
            {
                string? namedProject = containers
                    .Where(container => container.IsComposeManaged)
                    .Select(container => container.ComposeProjectName)
                    .FirstOrDefault(project => project.Equals(composeData.Name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(namedProject))
                    return namedProject;
            }

            var serviceNames = composeData.Services.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rankedProjects = containers
                .Where(container => container.IsComposeManaged && serviceNames.Contains(container.ComposeServiceName))
                .GroupBy(container => container.ComposeProjectName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Name = group.Key, Count = group.Count() })
                .OrderByDescending(project => project.Count)
                .ToList();

            return rankedProjects.Count == 1 ||
                   (rankedProjects.Count > 1 && rankedProjects[0].Count > rankedProjects[1].Count)
                ? rankedProjects[0].Name
                : string.Empty;
        }

        private static bool ComposeConfigContainsFile(DockerContainer container, string composeFilePath)
        {
            if (string.IsNullOrWhiteSpace(container.ComposeConfigFiles))
                return false;

            string expectedPath = Path.GetFullPath(composeFilePath);
            foreach (string configFile in container.ComposeConfigFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidatePath = configFile;
                if (!Path.IsPathRooted(candidatePath) && !string.IsNullOrWhiteSpace(container.ComposeWorkingDirectory))
                    candidatePath = Path.Combine(container.ComposeWorkingDirectory, candidatePath);

                try
                {
                    if (Path.GetFullPath(candidatePath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // 잘못된 경로 라벨은 다른 매칭 방법으로 폴백합니다.
                }
            }

            return false;
        }

        private static DockerContainer? FindComposeContainer(
            IReadOnlyCollection<DockerContainer> containers,
            string projectName,
            string serviceName,
            string fallbackNodeName)
        {
            var labeledMatches = containers
                .Where(container => container.IsComposeManaged)
                .Where(container => string.IsNullOrWhiteSpace(projectName) ||
                                    container.ComposeProjectName.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                .Where(container => container.ComposeServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(container => container.ComposeContainerNumber)
                .ToList();
            if (labeledMatches.Count > 0)
                return labeledMatches[0];

            return containers.FirstOrDefault(container =>
                container.Name.Equals(fallbackNodeName, StringComparison.OrdinalIgnoreCase) ||
                container.Name.Contains($"-{fallbackNodeName}-", StringComparison.OrdinalIgnoreCase) ||
                container.Name.Contains($"_{fallbackNodeName}_", StringComparison.OrdinalIgnoreCase) ||
                container.Name.EndsWith(fallbackNodeName, StringComparison.OrdinalIgnoreCase));
        }

        private static void ApplyComposeNetworkSettings(GroupViewModel group, Dictionary<object, object>? rawComposeRoot, string networkName)
        {
            var networkMap = ComposeYamlHelper.GetNetworkMap(rawComposeRoot, networkName);
            if (networkMap == null) return;

            group.ComposeRawNetworkYaml = ComposeYamlHelper.GetNetworkYaml(rawComposeRoot, networkName);
            group.Driver = ComposeYamlHelper.GetValue(networkMap, "driver")?.ToString() ?? group.Driver;
            group.Internal = ToBool(ComposeYamlHelper.GetValue(networkMap, "internal"));
            group.Attachable = ToBool(ComposeYamlHelper.GetValue(networkMap, "attachable"));
            group.External = ToBool(ComposeYamlHelper.GetValue(networkMap, "external"));
            group.EnableIPv6 = ToBool(ComposeYamlHelper.GetValue(networkMap, "enable_ipv6"));
            group.ComposeNetworkName = ComposeYamlHelper.GetValue(networkMap, "name")?.ToString() ?? "";
            group.Labels = ComposeYamlHelper.ToStringDictionary(ComposeYamlHelper.GetValue(networkMap, "labels"));
            group.DriverOptions = ComposeYamlHelper.ToStringDictionary(ComposeYamlHelper.GetValue(networkMap, "driver_opts"));

            var externalMap = ComposeYamlHelper.GetMapping(ComposeYamlHelper.GetValue(networkMap, "external"));
            if (externalMap != null)
            {
                group.External = true;
                group.ComposeNetworkName = ComposeYamlHelper.GetValue(externalMap, "name")?.ToString() ?? group.ComposeNetworkName;
            }

            var ipamMap = ComposeYamlHelper.GetMapping(ComposeYamlHelper.GetValue(networkMap, "ipam"));
            var configList = ComposeYamlHelper.GetValue(ipamMap, "config") as System.Collections.IEnumerable;
            var firstConfig = configList?
                .Cast<object>()
                .Select(ComposeYamlHelper.GetMapping)
                .FirstOrDefault(map => map != null);

            if (firstConfig != null)
            {
                group.Subnet = ComposeYamlHelper.GetValue(firstConfig, "subnet")?.ToString() ?? "";
                group.Gateway = ComposeYamlHelper.GetValue(firstConfig, "gateway")?.ToString() ?? "";
                group.IpRange = ComposeYamlHelper.GetValue(firstConfig, "ip_range")?.ToString() ?? "";
                group.AuxAddresses = ComposeYamlHelper.ToStringDictionary(ComposeYamlHelper.GetValue(firstConfig, "aux_addresses"));
            }
        }

        private static void ApplyComposeVolumeSettings(NodeViewModel node, Dictionary<object, object>? rawComposeRoot, string volumeName)
        {
            var volumeMap = ComposeYamlHelper.GetVolumeMap(rawComposeRoot, volumeName);
            if (volumeMap == null) return;

            node.ComposeRawVolumeYaml = ComposeYamlHelper.GetVolumeYaml(rawComposeRoot, volumeName);
            node.Driver = ComposeYamlHelper.GetValue(volumeMap, "driver")?.ToString() ?? node.Driver;
            node.ImageName = string.IsNullOrWhiteSpace(node.Driver) || node.Driver == "-" ? node.ImageName : node.Driver;
            node.VolumeExternal = ToBool(ComposeYamlHelper.GetValue(volumeMap, "external"));
            node.DockerVolumeName = ComposeYamlHelper.GetValue(volumeMap, "name")?.ToString() ?? string.Empty;
            node.VolumeLabels = ComposeYamlHelper.ToStringDictionary(ComposeYamlHelper.GetValue(volumeMap, "labels"));
            node.VolumeDriverOptions = ComposeYamlHelper.ToStringDictionary(ComposeYamlHelper.GetValue(volumeMap, "driver_opts"));

            var externalMap = ComposeYamlHelper.GetMapping(ComposeYamlHelper.GetValue(volumeMap, "external"));
            if (externalMap != null)
            {
                node.VolumeExternal = true;
                node.DockerVolumeName = ComposeYamlHelper.GetValue(externalMap, "name")?.ToString() ?? node.DockerVolumeName;
            }
        }

        private static bool ToBool(object? value)
        {
            if (value is bool boolean) return boolean;
            return bool.TryParse(value?.ToString(), out var parsed) && parsed;
        }
    }
}
