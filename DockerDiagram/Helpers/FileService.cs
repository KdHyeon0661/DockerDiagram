using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 다이어그램(Sheet)의 상태를 파일(.vdm)로 저장하거나 반대로 불러오는(Load) 정적 서비스 클래스입니다.
    /// SSH 원격 접속 프로필과 같이 저장된 시트의 경우, 불러올 때 자동으로 터널링을 복구합니다.
    /// </summary>
    public static class FileService
    {
        /// <summary>
        /// 사용자에게 다이얼로그를 띄워 파일 경로를 지정받은 뒤, 현재 화면의 다이어그램을 파일로 저장합니다.
        /// </summary>
        public static string? SaveDiagramAs(MainViewModel mainVm, IDialogService dialogService)
        {
            // 🔥 [MVVM 수정 1] 하드코딩된 SaveFileDialog 제거 및 IDialogService 활용
            string defaultFileName = !string.IsNullOrEmpty(mainVm.CurrentFilePath)
                ? Path.GetFileName(mainVm.CurrentFilePath)
                : "MyDockerLayout";

            string? selectedFileName = dialogService.ShowSaveFileDialog(
                "Docker Diagram (*.vdm)|*.vdm",
                ".vdm",
                defaultFileName,
                "Save Diagram As");

            if (!string.IsNullOrEmpty(selectedFileName))
            {
                if (InternalSave(mainVm, selectedFileName))
                {
                    SaveLastFilePath(selectedFileName); // 다음 실행 시 자동 로드를 위해 경로 저장
                    dialogService.ShowInfo($"저장되었습니다.\n{selectedFileName}", "완료");
                    return selectedFileName;
                }
            }
            return null; // 저장 취소 또는 실패 시
        }

        /// <summary>
        /// 다이얼로그 없이 기존에 저장된 경로에 즉시 덮어쓰기 저장을 수행합니다. (빠른 저장)
        /// </summary>
        public static bool QuickSave(MainViewModel mainVm, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            bool result = InternalSave(mainVm, path);
            if (result) SaveLastFilePath(path);
            return result;
        }

        /// <summary>
        /// 실제 데이터 매핑 및 JSON 직렬화를 수행하여 지정된 경로에 파일을 쓰는 내부(Internal) 메서드입니다.
        /// </summary>
        private static bool InternalSave(MainViewModel mainVm, string filePath)
        {
            try
            {
                var fileData = new DiagramFile(); // 파일의 최상위 루트 껍데기 생성

                var allSheets = mainVm.AllSheets.ToList();
                if (mainVm.ActiveSheet != null)
                    fileData.ActiveSheetIndex = allSheets.IndexOf(mainVm.ActiveSheet);

                // 모든 시트(탭)를 순회하며 데이터 긁어모으기
                foreach (var sheetVm in allSheets)
                {
                    var sheetData = new SheetData
                    {
                        Title = sheetVm.Title,
                        Profile = sheetVm.Profile, // 시트의 도커 연결 프로필(로컬/원격) 정보 저장
                        MapWidth = sheetVm.MapWidth,
                        MapHeight = sheetVm.MapHeight,
                        OffsetX = sheetVm.OffsetX,
                        OffsetY = sheetVm.OffsetY,
                        Scale = sheetVm.Scale,
                        ComposeRawYaml = sheetVm.ComposeRawYaml
                    };

                    // 시트 안의 모든 노드 정보 수집
                    foreach (var nodeVm in sheetVm.Nodes)
                    {
                        sheetData.Nodes.Add(new NodeData
                        {
                            Id = nodeVm.Id,
                            DockerId = nodeVm.ContainerId,
                            Name = nodeVm.Name,
                            ImageName = nodeVm.ImageName,
                            Type = nodeVm.Type,
                            X = nodeVm.X,
                            Y = nodeVm.Y,
                            Width = nodeVm.Width,
                            Height = nodeVm.Height,
                            PortBindings = nodeVm.PortBindings ?? new List<string>(),
                            EnvironmentVariables = nodeVm.EnvironmentVariables ?? new List<string>(),
                            RestartPolicy = nodeVm.RestartPolicy ?? "no",
                            ComposeServiceName = nodeVm.ComposeServiceName,
                            ComposeRawServiceYaml = nodeVm.ComposeRawServiceYaml
                        });
                    }

                    // 시트 안의 모든 연결선 정보 수집
                    foreach (var connVm in sheetVm.Connectors)
                    {
                        sheetData.Connections.Add(new ConnectionData
                        {
                            SourceNodeId = connVm.Source.Id,
                            TargetNodeId = connVm.Target.Id,
                            SourceDir = connVm.SourceDir,
                            TargetDir = connVm.TargetDir,
                            RelationType = connVm.RelationType,
                            MountPath = connVm.MountPath,
                            IpAddress = connVm.IpAddress
                        });
                    }

                    // 시트 안의 모든 그룹 정보 수집
                    foreach (var group in sheetVm.Groups)
                    {
                        if (string.IsNullOrEmpty(group.Id)) group.Id = Guid.NewGuid().ToString();

                        var gData = new GroupData
                        {
                            Id = group.Id,
                            Title = group.Title,
                            X = group.X,
                            Y = group.Y,
                            Width = group.Width,
                            Height = group.Height,
                            Type = group.Type
                        };
                        gData.ContainedNodeIds = group.ContainedNodes.Select(n => n.Id).ToList();
                        sheetData.Groups.Add(gData);
                    }

                    fileData.Sheets.Add(sheetData);
                }

                // C# 객체를 JSON 포맷으로 직렬화(Serialize)하여 파일로 저장
                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true }; // 예쁘게 줄바꿈(Formatting) 옵션
                string jsonString = System.Text.Json.JsonSerializer.Serialize(fileData, options);
                System.IO.File.WriteAllText(filePath, jsonString);
                SaveLastFilePath(filePath);

                mainVm.IsModified = false; // "수정됨(*)" 상태 해제
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] Save Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 사용자에게 다이얼로그를 띄워 저장된 다이어그램 파일(.vdm)을 선택받고, 화면에 복원합니다.
        /// </summary>
        public static async Task<string?> LoadDiagramWithDialogAsync(
            MainViewModel mainVm,
            IContainerService containerService,
            IVolumeService volumeService,
            INetworkService networkService,
            IDialogService dialogService)
        {
            // 🔥 [MVVM 수정 2] 하드코딩된 OpenFileDialog 제거 및 IDialogService 활용
            string? selectedFileName = dialogService.ShowOpenFileDialog(
                "Docker Diagram (*.vdm)|*.vdm|JSON File (*.json)|*.json",
                "Load Diagram");

            if (!string.IsNullOrEmpty(selectedFileName))
            {
                bool success = await LoadDiagramFromPathAsync(mainVm, selectedFileName, containerService, volumeService, networkService, dialogService);

                if (success)
                {
                    SaveLastFilePath(selectedFileName);
                    return selectedFileName;
                }
            }
            return null; // 불러오기 취소 또는 실패 시
        }

        /// <summary>
        /// 지정된 경로의 다이어그램 파일(.vdm)을 읽어 C# 모델로 역직렬화하고, 
        /// 의존성 순서(노드 -> 그룹 -> 선)에 맞게 뷰모델을 생성하여 화면을 복원합니다.
        /// </summary>
        public static async Task<bool> LoadDiagramFromPathAsync(MainViewModel mainVm, string filePath, IContainerService containerService,
                                                                IVolumeService volumeService, INetworkService networkService, IDialogService dialogService)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                string json = await File.ReadAllTextAsync(filePath);
                var fileData = JsonSerializer.Deserialize<DiagramFile>(json);

                if (fileData == null) return false;

                foreach (var sheet in mainVm.AllSheets.ToList())
                {
                    // 🔥 [핵심 수정] 이벤트 해제 권한이 SheetManager로 이관되었으므로, SheetManager를 통해 호출
                    mainVm.SheetManager.UnsubscribeSheetEvents(sheet);

                    // 2. SSH 원격 접속 시트였다면 백그라운드 SSH 프로세스 종료
                    if (sheet.Profile != null && sheet.Profile.Type == EndpointType.SshRemote && !string.IsNullOrEmpty(sheet.Profile.HostIp))
                    {
                        SshTunnelManager.ReleaseTunnel(
                            sheet.Profile.HostIp,
                            sheet.Profile.SshPort,
                            sheet.Profile.SshUsername ?? "root"
                        );
                    }

                    // 3. 원격 전용 DockerApiService 자원 해제 및 전역 리스트 제거
                    if (sheet.DockerService != null && sheet.DockerService != containerService)
                    {
                        App.ActiveDockerServices.Remove(sheet.DockerService);
                        sheet.DockerService.Dispose();
                    }
                }

                mainVm.SheetManager.ClearAllWorkspaces();

                foreach (var sheetData in fileData.Sheets)
                {
                    // 1. 파일에서 저장된 프로필(신분증) 읽어오기 (없으면 로컬로 간주)
                    ConnectionProfile loadedProfile = sheetData.Profile ?? new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
                    IDockerService targetDockerService = (IDockerService)containerService; // 기본값은 로컬 서비스

                    // 2. 만약 원격(SSH) 접속용 시트라면, 백그라운드에서 터널을 다시 개통!
                    if (loadedProfile.Type == EndpointType.SshRemote && !string.IsNullOrEmpty(loadedProfile.HostIp))
                    {
                        try
                        {
                            int localPort = await SshTunnelManager.GetOrStartTunnelAsync(
                                loadedProfile.HostIp,
                                loadedProfile.SshPort,
                                loadedProfile.SshUsername ?? "root",
                                loadedProfile.SshKeyFilePath ?? "",
                                dialogService);

                            loadedProfile.LocalTunnelPort = localPort;
                            targetDockerService = new DockerApiService(loadedProfile); // 새로 뚫린 터널로 통신하는 전용 서비스 생성

                            App.ActiveDockerServices.Add(targetDockerService); // 앱 종료 시 연결을 끊기 위해 전역 리스트에 등록
                        }
                        catch (Exception ex)
                        {
                            dialogService.ShowMessage($"'{loadedProfile.Name}' 시트의 SSH 터널 복구에 실패했습니다.\n임시로 로컬 모드로 전환됩니다.\n({ex.Message})");
                            loadedProfile.Type = EndpointType.Local;
                            targetDockerService = (IDockerService)containerService; // 터널링 실패 시 로컬 서비스로 폴백(Fallback)
                        }
                    }

                    // 3. 복원된 프로필과 전용 도커 서비스로 시트 생성
                    var sheetVm = new SheetViewModel(sheetData.Title, loadedProfile, targetDockerService, dialogService);
                    sheetVm.MapWidth = sheetData.MapWidth;
                    sheetVm.MapHeight = sheetData.MapHeight;
                    sheetVm.OffsetX = sheetData.OffsetX;
                    sheetVm.OffsetY = sheetData.OffsetY;
                    sheetVm.Scale = sheetData.Scale;
                    sheetVm.ComposeRawYaml = sheetData.ComposeRawYaml ?? string.Empty;

                    var itemMap = new Dictionary<string, IConnectableItem>(); // 노드와 선을 엮기 위한 임시 딕셔너리

                    var currentContainerSvc = (IContainerService)targetDockerService;
                    var currentVolumeSvc = (IVolumeService)targetDockerService;
                    var currentNetworkSvc = (INetworkService)targetDockerService;

                    // ==========================================
                    // 의존성 복원 단계 1: 노드 (가장 먼저 생성)
                    // ==========================================
                    foreach (var nodeData in sheetData.Nodes)
                    {
                        var nodeVm = new NodeViewModel(currentContainerSvc, currentVolumeSvc, dialogService)
                        {
                            Id = nodeData.Id,
                            ContainerId = nodeData.DockerId,
                            Name = nodeData.Name,
                            ImageName = nodeData.ImageName,
                            Type = nodeData.Type,
                            X = nodeData.X,
                            Y = nodeData.Y,
                            Width = nodeData.Width,
                            Height = nodeData.Height,
                            PortBindings = nodeData.PortBindings ?? new List<string>(),
                            EnvironmentVariables = nodeData.EnvironmentVariables ?? new List<string>(),
                            RestartPolicy = nodeData.RestartPolicy ?? "no",
                            ComposeServiceName = nodeData.ComposeServiceName ?? string.Empty,
                            ComposeRawServiceYaml = nodeData.ComposeRawServiceYaml ?? string.Empty,
                            StatusColor = "#808080" // 초기 색상은 회색(Unkown)으로 고정, 이후 실시간 갱신됨
                        };

                        sheetVm.Nodes.Add(nodeVm);
                        itemMap[nodeVm.Id] = nodeVm; // 나중에 선을 연결하기 위해 ID로 기억
                    }

                    // ==========================================
                    // 의존성 복원 단계 2: 그룹 (노드를 품어야 하므로 두 번째)
                    // ==========================================
                    foreach (var groupData in sheetData.Groups)
                    {
                        var groupVm = new GroupViewModel(
                            groupData.X,
                            groupData.Y,
                            groupData.Width,
                            groupData.Height,
                            currentNetworkSvc,
                            dialogService,
                            groupData.Title,
                            groupData.Type
                        )
                        {
                            Id = string.IsNullOrEmpty(groupData.Id) ? Guid.NewGuid().ToString() : groupData.Id,
                            ParentSheet = sheetVm
                        };

                        // 그룹 안에 저장되어 있던 노드들을 다시 묶어줌
                        foreach (var nodeId in groupData.ContainedNodeIds)
                        {
                            if (itemMap.TryGetValue(nodeId, out var item) && item is NodeViewModel node)
                            {
                                // isRestoring 플래그를 통해 불필요한 도커 API(ConnectNetwork 등) 호출 방지
                                await groupVm.AddNodeAsync(node, isRestoring: true);
                            }
                        }
                        sheetVm.Groups.Add(groupVm);
                        itemMap[groupVm.Id] = groupVm; // 선 연결을 위해 그룹도 기억
                    }

                    // ==========================================
                    // 의존성 복원 단계 3: 연결선 (노드와 그룹이 모두 있어야 하므로 마지막)
                    // ==========================================
                    foreach (var connData in sheetData.Connections)
                    {
                        if (itemMap.TryGetValue(connData.SourceNodeId, out var source) &&
                            itemMap.TryGetValue(connData.TargetNodeId, out var target))
                        {
                            var connVm = new ConnectorViewModel(source, target, connData.SourceDir, connData.TargetDir, dialogService)
                            {
                                RelationType = connData.RelationType,
                                MountPath = connData.MountPath,
                                IpAddress = connData.IpAddress
                            };

                            sheetVm.Connectors.Add(connVm);
                        }
                    }

                    mainVm.SheetManager.AddExistingSheet(sheetVm, activate: false); // 완성된 시트를 접속 탭 아래에 추가
                }

                // 저장할 때 보고 있던 탭(ActiveSheet)으로 다시 포커스 이동
                var restoredSheets = mainVm.AllSheets.ToList();
                if (restoredSheets.Count > 0 && fileData.ActiveSheetIndex < restoredSheets.Count)
                {
                    mainVm.ActiveSheet = restoredSheets[fileData.ActiveSheetIndex];
                }
                else if (restoredSheets.Count == 0)
                {
                    mainVm.SheetManager.AddSheet();
                }

                return true;
            }
            catch (Exception ex)
            {
                dialogService.ShowMessage($"파일 불러오기 실패 ({filePath}):\n{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 프로그램이 다시 켜졌을 때 마지막으로 작업하던 파일을 자동으로 불러오기 위해 경로를 레지스트리/설정에 저장합니다.
        /// </summary>
        private static void SaveLastFilePath(string path)
        {
            try
            {
                Properties.Settings.Default.LastFilePath = path;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] Failed to save LastFilePath='{path}'. {ex}");
            }
        }
    }
}
