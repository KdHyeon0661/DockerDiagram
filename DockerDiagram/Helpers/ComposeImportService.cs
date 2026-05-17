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
            IDialogService dialogService)
        {
            // 🔥 [MVVM 수정 1] UI(OpenFileDialog) 종속성 제거 및 IDialogService 활용
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

                if (composeData == null || composeData.Services == null || composeData.Services.Count == 0)
                {
                    dialogService.ShowMessage("유효한 Compose 파일이 아니거나 Service가 없습니다.");
                    return;
                }

                // 탭(Sheet) 이름을 Compose 파일명으로 설정하여 시각적 인지 향상
                string sheetName = $"Compose ({Path.GetFileName(selectedFileName)})";

                // =================================================================
                // 🔥 [보안 및 유연성 수정] 현재 활성화된 시트의 프로필(신분증)과 도커 서비스를 상속받음
                // =================================================================
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

                var newSheet = new SheetViewModel(sheetName, targetProfile, targetDockerService, dialogService);
                // =================================================================

                var nodeMap = new Dictionary<string, NodeViewModel>();
                var groupMap = new Dictionary<string, GroupViewModel>();

                // =================================================================
                // 1단계: YAML의 Volumes와 Services를 분석하여 시각적 노드 객체 생성
                // =================================================================
                if (composeData.Volumes != null)
                {
                    foreach (var vol in composeData.Volumes)
                    {
                        nodeMap[vol.Key] = new NodeViewModel(containerService, volumeService, dialogService)
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = vol.Key,
                            Type = NodeType.Volume,
                            Width = 160,
                            Height = 80
                        };
                        newSheet.Nodes.Add(nodeMap[vol.Key]);
                    }
                }

                foreach (var svc in composeData.Services)
                {
                    nodeMap[svc.Key] = new NodeViewModel(containerService, volumeService, dialogService)
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = !string.IsNullOrEmpty(svc.Value.ContainerName) ? svc.Value.ContainerName : svc.Key,
                        ImageName = svc.Value.Image ?? "unknown-image",
                        Type = NodeType.Container,
                        Width = 160,
                        Height = 80,
                        RestartPolicy = svc.Value.Restart ?? "no",
                        PortBindings = svc.Value.Ports != null ? new List<string>(svc.Value.Ports) : new List<string>(),
                        EnvironmentVariables = svc.Value.Environment != null ? new List<string>(svc.Value.Environment) : new List<string>()
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

                    string? primaryNet = null;

                    // 네트워크 정보 파싱 (List 방식과 Dictionary 방식 모두 대응)
                    if (svc.Value.Networks is List<object> netList && netList.Count > 0)
                        primaryNet = netList[0].ToString();
                    else if (svc.Value.Networks is Dictionary<object, object> netDict && netDict.Count > 0)
                        primaryNet = netDict.Keys.First().ToString();

                    if (primaryNet != null)
                    {
                        if (!visualGroupMap.ContainsKey(primaryNet)) visualGroupMap[primaryNet] = new List<NodeViewModel>();
                        visualGroupMap[primaryNet].Add(sourceContainer);
                    }
                    else
                    {
                        unassignedNodes.Add(sourceContainer);
                    }

                    // 볼륨 마운트 선 연결
                    if (svc.Value.Volumes != null)
                    {
                        foreach (var volMapping in svc.Value.Volumes)
                        {
                            string volName = volMapping;
                            string mountPath = "/data";

                            int lastColon = volMapping.LastIndexOf(':');
                            if (lastColon > 0 && lastColon != 1) // C:\ 같은 윈도우 경로 보호
                            {
                                volName = volMapping.Substring(0, lastColon);
                                mountPath = volMapping.Substring(lastColon + 1);
                            }

                            if (nodeMap.TryGetValue(volName, out var targetVolume))
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
                    if (svc.Value.DependsOn != null)
                    {
                        foreach (var dep in svc.Value.DependsOn)
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

                    var groupVm = new GroupViewModel(currentGroupX, currentGroupY, gWidth, gHeight, networkService, dialogService, netName, GroupType.Network)
                    {
                        Id = Guid.NewGuid().ToString(),
                        ParentSheet = newSheet // 그룹이 소속된 시트를 명시적으로 할당
                    };

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

                // 네트워크 그룹에 속하지 못한 깍두기 노드들 배치
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

                mainVm.Sheets.Add(newSheet);
                mainVm.ActiveSheet = newSheet;

                // =================================================================
                // 4단계: 실제 도커 엔진에 배포(docker compose up)할지 묻는 프로세스
                // =================================================================
                // 🔥 [MVVM 수정 2] 하드코딩된 MessageBox 제거 및 IDialogService 활용
                bool isYes = dialogService.ShowConfirm(
                    $"[{sheetName}] 화면 구성을 완료했습니다.\n이 구성대로 실제 도커 엔진에 컨테이너들을 배포(실행)하시겠습니까?",
                    "실제 도커 배포");

                if (isYes)
                {
                    var containerNodes = newSheet.Nodes.Where(n => n.Type == NodeType.Container).ToList();
                    foreach (var node in containerNodes) node.IsCreating = true; // UI에 로딩 스피너 표시용

                    try
                    {
                        // 백그라운드에서 CMD 창 없이 docker compose 명령 실행
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "docker",
                            Arguments = $"compose -f \"{selectedFileName}\" up -d",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(selectedFileName)
                        };

                        using var process = new System.Diagnostics.Process { StartInfo = psi };
                        var errorBuilder = new System.Text.StringBuilder();

                        process.OutputDataReceived += (sender, e) => { }; // 표준 출력은 무시
                        process.ErrorDataReceived += (sender, e) => // 에러 로그 수집
                        {
                            if (!string.IsNullOrEmpty(e.Data)) errorBuilder.AppendLine(e.Data);
                        };

                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        await Task.Run(() => process.WaitForExit()); // 프로세스 종료 대기

                        foreach (var node in containerNodes) node.IsCreating = false;

                        if (process.ExitCode == 0)
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
                            dialogService.ShowMessage($"도커 배포 중 오류가 발생했습니다:\n{errorBuilder}");
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
    }
}