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
                        // ★ [수정] 선이 그룹을 식별할 수 있도록 빈 ID가 있으면 고유 ID 발급
                        if (string.IsNullOrEmpty(group.Id)) group.Id = Guid.NewGuid().ToString();

                        var gData = new GroupData
                        {
                            Id = group.Id, // ★ [수정] 그룹 ID도 파일에 함께 저장해야 합니다!
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
                    var sheetVm = new SheetViewModel(sheetData.Title, containerService, volumeService, dialogService);

                    sheetVm.MapWidth = sheetData.MapWidth;
                    sheetVm.MapHeight = sheetData.MapHeight;
                    sheetVm.OffsetX = sheetData.OffsetX;
                    sheetVm.OffsetY = sheetData.OffsetY;
                    sheetVm.Scale = sheetData.Scale;

                    // ★ [수정] 노드뿐만 아니라 그룹도 찾아야 하므로 IConnectableItem 딕셔너리로 변경
                    var itemMap = new Dictionary<string, IConnectableItem>();

                    // 1. 노드 먼저 불러오기
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
                        itemMap[nodeVm.Id] = nodeVm;
                    }

                    // 2. 그룹을 두 번째로 불러오기 (선보다 먼저 생성되어야 함!)
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

                        // 파일에 ID가 없다면 새로 생성, 있다면 복원
                        groupVm.Id = string.IsNullOrEmpty(groupData.Id) ? Guid.NewGuid().ToString() : groupData.Id;
                        groupVm.Type = groupData.Type;
                        groupVm.ParentSheet = sheetVm;

                        foreach (var nodeId in groupData.ContainedNodeIds)
                        {
                            if (itemMap.TryGetValue(nodeId, out var item) && item is NodeViewModel node)
                            {
                                groupVm.AddNode(node);
                            }
                        }
                        sheetVm.Groups.Add(groupVm);
                        itemMap[groupVm.Id] = groupVm; // 그룹도 맵에 추가!
                    }

                    // 3. 선(Connection)을 가장 마지막에 불러오기
                    foreach (var connData in sheetData.Connections)
                    {
                        // 이제 노드와 그룹 모두 itemMap에서 안전하게 찾을 수 있습니다.
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