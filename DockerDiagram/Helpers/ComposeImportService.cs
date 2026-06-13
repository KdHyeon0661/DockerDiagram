using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DockerDiagram.Helpers
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
                // =================================================================

                var currentContainerSvc = (IContainerService)targetDockerService;
                var currentVolumeSvc = (IVolumeService)targetDockerService;
                var currentNetworkSvc = (INetworkService)targetDockerService;

                var nodeMap = new Dictionary<string, NodeViewModel>();
                var groupMap = new Dictionary<string, GroupViewModel>();

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
                            Height = 80
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
                        ComposeServiceName = svc.Key,
                        ComposeRawServiceYaml = ComposeYamlHelper.GetServiceYaml(rawComposeRoot, svc.Key)
                    };
                    newSheet.Nodes.Add(nodeMap[svc.Key]);
                }

                // =================================================================
                // 2단계: 네트워크 분류 및 의존성/볼륨 선 긋기 로직
                // =================================================================
                var visualGroupMap = new Dictionary<string, List<NodeViewModel>>();
                var unassignedNodes = new List<NodeViewModel>();
                var volumeToNetMap = new Dictionary<NodeViewModel, string>();

                if (composeData.Networks != null)
                {
                    foreach (var net in composeData.Networks.Keys)
                        visualGroupMap[net] = new List<NodeViewModel>();
                }

                foreach (var svc in composeData.Services)
                {
                    if (!nodeMap.TryGetValue(svc.Key, out var sourceContainer)) continue;

                    var serviceNetworks = ComposeYamlHelper.ToNetworkNames(svc.Value.Networks);
                    string? primaryNet = serviceNetworks.FirstOrDefault();

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
                        unassignedNodes.Add(sourceContainer);
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
                                    Height = 80
                                };
                                ApplyComposeVolumeSettings(targetVolume, rawComposeRoot, volName);
                                nodeMap[volName] = targetVolume;
                                newSheet.Nodes.Add(targetVolume);
                            }

                            if (!newSheet.Connectors.Any(c => c.Source == sourceContainer && c.Target == targetVolume))
                            {
                                newSheet.Connectors.Add(new ConnectorViewModel(sourceContainer, targetVolume, PortDirection.Bottom, PortDirection.Top, dialogService)
                                {
                                    RelationType = RelationType.VolumeMount,
                                    MountPath = mountPath
                                });

                                // 볼륨을 네트워크 그룹 안에 포함시키기 위한 처리
                                if (primaryNet != null && !volumeToNetMap.ContainsKey(targetVolume))
                                {
                                    volumeToNetMap[targetVolume] = primaryNet;
                                    visualGroupMap[primaryNet].Add(targetVolume);
                                }
                            }
                        }
                    }

                    // 의존성(depends_on) 선 연결
                    var dependsOn = ComposeYamlHelper.ToDependsOnServiceNames(svc.Value.DependsOn);
                    if (dependsOn.Count > 0)
                    {
                        foreach (var dep in dependsOn)
                        {
                            if (nodeMap.TryGetValue(dep, out var targetContainer))
                            {
                                newSheet.Connectors.Add(new ConnectorViewModel(targetContainer, sourceContainer, PortDirection.Bottom, PortDirection.Top, dialogService)
                                {
                                    RelationType = RelationType.Dependency
                                });
                            }
                        }
                    }
                }

                // 남은 볼륨 노드 처리
                foreach (var volNode in nodeMap.Values.Where(n => n.Type == NodeType.Volume))
                {
                    if (!volumeToNetMap.ContainsKey(volNode)) unassignedNodes.Add(volNode);
                }

                // =================================================================
                // 3단계: 화면 상의 2열(2xN) 자동 배치 알고리즘
                // =================================================================
                double currentGroupX = 50;
                double currentGroupY = 50;
                double maxGroupHeightInRow = 0;

                foreach (var kvp in visualGroupMap)
                {
                    string netName = kvp.Key;
                    var nodesInGroup = kvp.Value;

                    int cols = 2;
                    int rows = Math.Max(1, (int)Math.Ceiling(nodesInGroup.Count / (double)cols));

                    // 노드 규격이 160x80으로 커졌으므로, 그룹이 품어줄 수 있도록 셀 크기도 살짝 넉넉하게 180x110 그대로 유지합니다.
                    double cellWidth = 180, cellHeight = 110;
                    double gWidth = (cols * cellWidth) + 40;
                    double gHeight = (rows * cellHeight) + 60;

                    var groupVm = new GroupViewModel(currentGroupX, currentGroupY, gWidth, gHeight, currentNetworkSvc, dialogService, netName, GroupType.Network)
                    {
                        Id = Guid.NewGuid().ToString(),
                        ParentSheet = newSheet // 그룹이 소속된 시트를 명시적으로 할당
                    };
                    ApplyComposeNetworkSettings(groupVm, rawComposeRoot, netName);

                    newSheet.Groups.Add(groupVm);
                    groupMap[netName] = groupVm;

                    // 그룹 내 노드들을 격자(Grid) 형태로 예쁘게 배치
                    for (int i = 0; i < nodesInGroup.Count; i++)
                    {
                        var node = nodesInGroup[i];
                        int r = i / cols;
                        int c = i % cols;

                        node.X = currentGroupX + 20 + (c * cellWidth);
                        node.Y = currentGroupY + 50 + (r * cellHeight);

                        await groupVm.AddNodeAsync(node, isRestoring: true);
                    }

                    // 다음 그룹 배치를 위해 X좌표 이동
                    currentGroupX += gWidth + 50;
                    if (gHeight > maxGroupHeightInRow) maxGroupHeightInRow = gHeight;

                    // 화면 가로 끝에 도달하면 다음 줄로 이동 (줄바꿈)
                    if (currentGroupX > 1200)
                    {
                        currentGroupX = 50;
                        currentGroupY += maxGroupHeightInRow + 50;
                        maxGroupHeightInRow = 0;
                    }
                }

                // 네트워크 그룹에 속하지 않은 노드를 별도 배치합니다.
                if (unassignedNodes.Count > 0)
                {
                    currentGroupY += maxGroupHeightInRow + 50;
                    currentGroupX = 50;

                    for (int i = 0; i < unassignedNodes.Count; i++)
                    {
                        var node = unassignedNodes[i];
                        node.X = currentGroupX + ((i % 4) * 180); // 셀 너비 180 간격
                        node.Y = currentGroupY + ((i / 4) * 110); // 셀 높이 110 간격
                    }
                }

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

                            // 배포된 컨테이너들의 실제 ID를 매핑하여 상세 정보를 다시 불러옴
                            foreach (var node in containerNodes)
                            {
                                var matched = allContainers.FirstOrDefault(c =>
                                    c.Name == $"/{node.Name}" ||
                                    c.Name.Contains($"-{node.Name}-") ||
                                    c.Name.Contains($"_{node.Name}_") ||
                                    c.Name.EndsWith(node.Name));

                                if (matched != null)
                                {
                                    node.ContainerId = matched.Id;
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
