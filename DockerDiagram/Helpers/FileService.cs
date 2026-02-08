using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using System.Diagnostics;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Threading.Tasks; // Task 사용을 위해 추가
using System.Collections.Generic; // List, Dictionary 등을 위해 추가
using System.Linq; // Select 등을 위해 추가

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
                        var gData = new GroupData
                        {
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

                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(fileData, options);
                File.WriteAllText(filePath, jsonString);
                SaveLastFilePath(filePath);

                mainVm.IsModified = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileService] Save Error: {ex.Message}");
                return false;
            }
        }

        // [불러오기 기능 1] 다이얼로그 (수정됨: 성공 여부 확인 후 경로 반환)
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
                // ★ 여기서 true가 리턴되어야만 파일 경로를 반환함
                bool success = await LoadDiagramFromPathAsync(mainVm, dlg.FileName, containerService, volumeService, networkService, dialogService);

                if (success)
                {
                    SaveLastFilePath(dlg.FileName);
                    return dlg.FileName;
                }
            }
            return null;
        }

        // [불러오기 기능 2] 경로 로드 (수정됨: bool 반환)
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
                    var sheetVm = new SheetViewModel(sheetData.Title, containerService, volumeService, networkService, dialogService);

                    sheetVm.MapWidth = sheetData.MapWidth;
                    sheetVm.MapHeight = sheetData.MapHeight;
                    sheetVm.OffsetX = sheetData.OffsetX;
                    sheetVm.OffsetY = sheetData.OffsetY;
                    sheetVm.Scale = sheetData.Scale;

                    var nodeMap = new Dictionary<string, NodeViewModel>();

                    foreach (var nodeData in sheetData.Nodes)
                    {
                        var nodeVm = new NodeViewModel(containerService, volumeService, dialogService)
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
                        nodeMap[nodeVm.Id] = nodeVm;
                    }

                    foreach (var connData in sheetData.Connections)
                    {
                        if (nodeMap.TryGetValue(connData.SourceNodeId, out var source) &&
                            nodeMap.TryGetValue(connData.TargetNodeId, out var target))
                        {
                            var connVm = new ConnectorViewModel(source, target, connData.SourceDir, connData.TargetDir, dialogService);

                            connVm.RelationType = connData.RelationType;
                            connVm.MountPath = connData.MountPath;
                            connVm.IpAddress = connData.IpAddress;

                            sheetVm.Connectors.Add(connVm);
                        }
                    }

                    foreach (var groupData in sheetData.Groups)
                    {
                        var groupVm = new GroupViewModel(
                            groupData.X,
                            groupData.Y,
                            groupData.Width,
                            groupData.Height,
                            networkService,
                            dialogService,
                            groupData.Title
                        );

                        groupVm.Type = groupData.Type;
                        groupVm.ParentSheet = sheetVm;

                        foreach (var nodeId in groupData.ContainedNodeIds)
                        {
                            if (nodeMap.TryGetValue(nodeId, out var node))
                            {
                                groupVm.AddNode(node);
                            }
                        }
                        sheetVm.Groups.Add(groupVm);
                    }
                    mainVm.Sheets.Add(sheetVm);
                }

                if (mainVm.Sheets.Count > 0 && fileData.ActiveSheetIndex < mainVm.Sheets.Count)
                {
                    mainVm.ActiveSheet = mainVm.Sheets[fileData.ActiveSheetIndex];
                }

                return true; // ★ 성공 시 true 반환
            }
            catch (Exception ex)
            {
                dialogService.ShowMessage($"파일 불러오기 실패 ({filePath}):\n{ex.Message}");
                return false; // ★ 실패 시 false 반환
            }
        }

        // [신규 추가] 파일을 직접 로드하여 MainViewModel을 반환하는 메서드 (자동 로드용)
        // 주의: 이 메서드는 ViewModel을 생성하지 않고 데이터 구조체만 반환하거나,
        // 기존 LoadDiagramFromPathAsync를 재활용하는 것이 좋습니다.
        // MainViewModel에서 호출할 때는 LoadDiagramFromPathAsync를 쓰면 되므로 이 메서드는 필수 아님.

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