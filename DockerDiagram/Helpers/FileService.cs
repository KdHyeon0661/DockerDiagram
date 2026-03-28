using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Win32;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Helpers
{
    public static class FileService
    {
        // [저장 기능]
        public static string? SaveDiagramAs(MainViewModel mainVm, IDialogService dialogService)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Docker Diagram (*.vdm)|*.vdm",
                DefaultExt = ".vdm",
                FileName = !string.IsNullOrEmpty(mainVm.CurrentFilePath)
                           ? Path.GetFileName(mainVm.CurrentFilePath)
                           : "MyDockerLayout"
            };

            if (dlg.ShowDialog() == true)
            {
                if (InternalSave(mainVm, dlg.FileName))
                {
                    SaveLastFilePath(dlg.FileName);
                    dialogService.ShowInfo($"저장되었습니다.\n{dlg.FileName}", "완료");
                    return dlg.FileName;
                }
            }
            return null;
        }

        public static bool QuickSave(MainViewModel mainVm, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            bool result = InternalSave(mainVm, path);
            if (result) SaveLastFilePath(path);
            return result;
        }

        private static bool InternalSave(MainViewModel mainVm, string filePath)
        {
            try
            {
                var fileData = new DiagramFile();

                if (mainVm.ActiveSheet != null)
                    fileData.ActiveSheetIndex = mainVm.Sheets.IndexOf(mainVm.ActiveSheet);

                foreach (var sheetVm in mainVm.Sheets)
                {
                    var sheetData = new SheetData
                    {
                        Title = sheetVm.Title,
                        // =================================================================
                        // ★ [추가] 시트의 신분증(로컬인지 원격인지, SSH 키는 어딨는지)을 저장합니다!
                        // =================================================================
                        Profile = sheetVm.Profile,
                        MapWidth = sheetVm.MapWidth,
                        MapHeight = sheetVm.MapHeight,
                        OffsetX = sheetVm.OffsetX,
                        OffsetY = sheetVm.OffsetY,
                        Scale = sheetVm.Scale
                    };

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
                            RestartPolicy = nodeVm.RestartPolicy ?? "no"
                        });
                    }

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

                // JSON 파일로 저장
                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                string jsonString = System.Text.Json.JsonSerializer.Serialize(fileData, options);
                System.IO.File.WriteAllText(filePath, jsonString);
                SaveLastFilePath(filePath);

                mainVm.IsModified = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] Save Error: {ex.Message}");
                return false;
            }
        }

        // [불러오기 기능 1] 다이얼로그
        public static async Task<string?> LoadDiagramWithDialogAsync(
            MainViewModel mainVm,
            IContainerService containerService,
            IVolumeService volumeService,
            INetworkService networkService,
            IDialogService dialogService)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Docker Diagram (*.vdm)|*.vdm|JSON File (*.json)|*.json"
            };

            if (dlg.ShowDialog() == true)
            {
                bool success = await LoadDiagramFromPathAsync(mainVm, dlg.FileName, containerService, volumeService, networkService, dialogService);

                if (success)
                {
                    SaveLastFilePath(dlg.FileName);
                    return dlg.FileName;
                }
            }
            return null;
        }

        // [불러오기 기능 2] 경로 로드 (불러오기 순서 완벽 수정)
        public static async Task<bool> LoadDiagramFromPathAsync(
    MainViewModel mainVm,
    string filePath,
    IContainerService containerService,
    IVolumeService volumeService,
    INetworkService networkService,
    IDialogService dialogService)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                string json = await File.ReadAllTextAsync(filePath);
                var fileData = JsonSerializer.Deserialize<DiagramFile>(json);

                if (fileData == null) return false;

                mainVm.Sheets.Clear();

                foreach (var sheetData in fileData.Sheets)
                {
                    // =================================================================
                    // ★ [핵심 수정 1] 파일에서 저장된 프로필(신분증)을 읽어옵니다. (없으면 로컬)
                    // =================================================================
                    ConnectionProfile loadedProfile = sheetData.Profile ?? new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };

                    // 기본 도커 서비스는 인자로 받은 로컬 서비스로 설정
                    IDockerService targetDockerService = (IDockerService)containerService;

                    // ★ [핵심 수정 2] SSH 원격 시트라면 백그라운드에서 터널을 다시 뚫습니다!
                    if (loadedProfile.Type == EndpointType.SshRemote && !string.IsNullOrEmpty(loadedProfile.HostIp))
                    {
                        try
                        {
                            int localPort = await SshTunnelManager.GetOrStartTunnelAsync(
                                loadedProfile.HostIp,
                                loadedProfile.SshPort,
                                loadedProfile.SshUsername ?? "root",
                                loadedProfile.SshKeyFilePath ?? "");

                            loadedProfile.LocalTunnelPort = localPort;
                            targetDockerService = new DockerApiService(loadedProfile); // 원격 접속용 서비스 생성

                            // 앱 전역 서비스 목록에 등록 (종료 시 자원 해제용)
                            App.ActiveDockerServices.Add(targetDockerService);
                        }
                        catch (Exception ex)
                        {
                            dialogService.ShowMessage($"'{loadedProfile.Name}' 시트의 SSH 터널 복구에 실패했습니다.\n임시로 로컬 모드로 전환됩니다.\n({ex.Message})");
                            loadedProfile.Type = EndpointType.Local;
                            targetDockerService = (IDockerService)containerService;
                        }
                    }

                    var sheetVm = new SheetViewModel(sheetData.Title, loadedProfile, targetDockerService, dialogService);

                    sheetVm.MapWidth = sheetData.MapWidth;
                    sheetVm.MapHeight = sheetData.MapHeight;
                    sheetVm.OffsetX = sheetData.OffsetX;
                    sheetVm.OffsetY = sheetData.OffsetY;
                    sheetVm.Scale = sheetData.Scale;

                    var itemMap = new Dictionary<string, IConnectableItem>();

                    // ★ [핵심 수정 3] 노드/그룹을 만들 때, 로컬 서비스가 아닌 '현재 시트에 맞는 서비스'를 주입합니다!
                    var currentContainerSvc = (IContainerService)targetDockerService;
                    var currentVolumeSvc = (IVolumeService)targetDockerService;
                    var currentNetworkSvc = (INetworkService)targetDockerService;

                    // 1. 노드 먼저 불러오기
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
                            StatusColor = "#808080"
                        };

                        sheetVm.Nodes.Add(nodeVm);
                        itemMap[nodeVm.Id] = nodeVm;
                    }

                    // 2. 그룹을 두 번째로 불러오기
                    foreach (var groupData in sheetData.Groups)
                    {
                        var groupVm = new GroupViewModel(
                            groupData.X,
                            groupData.Y,
                            groupData.Width,
                            groupData.Height,
                            currentNetworkSvc,
                            dialogService,
                            groupData.Title
                        );

                        groupVm.Id = string.IsNullOrEmpty(groupData.Id) ? Guid.NewGuid().ToString() : groupData.Id;
                        groupVm.Type = groupData.Type;
                        groupVm.ParentSheet = sheetVm;

                        foreach (var nodeId in groupData.ContainedNodeIds)
                        {
                            if (itemMap.TryGetValue(nodeId, out var item) && item is NodeViewModel node)
                            {
                                // ★ isRestoring: true를 주어 파일을 불러올 때 불필요한 도커 API 연결 호출을 막습니다.
                                groupVm.AddNode(node, isRestoring: true);
                            }
                        }
                        sheetVm.Groups.Add(groupVm);
                        itemMap[groupVm.Id] = groupVm;
                    }

                    // 3. 선(Connection)을 가장 마지막에 불러오기
                    foreach (var connData in sheetData.Connections)
                    {
                        if (itemMap.TryGetValue(connData.SourceNodeId, out var source) &&
                            itemMap.TryGetValue(connData.TargetNodeId, out var target))
                        {
                            var connVm = new ConnectorViewModel(source, target, connData.SourceDir, connData.TargetDir, dialogService);

                            connVm.RelationType = connData.RelationType;
                            connVm.MountPath = connData.MountPath;
                            connVm.IpAddress = connData.IpAddress;

                            sheetVm.Connectors.Add(connVm);
                        }
                    }

                    mainVm.Sheets.Add(sheetVm);
                }

                if (mainVm.Sheets.Count > 0 && fileData.ActiveSheetIndex < mainVm.Sheets.Count)
                {
                    mainVm.ActiveSheet = mainVm.Sheets[fileData.ActiveSheetIndex];
                }

                return true;
            }
            catch (Exception ex)
            {
                dialogService.ShowMessage($"파일 불러오기 실패 ({filePath}):\n{ex.Message}");
                return false;
            }
        }

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