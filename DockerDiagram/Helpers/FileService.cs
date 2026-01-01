using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using System.Diagnostics;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Helpers
{
    public static class FileService
    {
        public static string? SaveDiagramAs(MainViewModel mainVm)
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
                    MessageBox.Show($"저장되었습니다.\n{dlg.FileName}", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
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
                        var gData = new GroupData { Title = group.Title, X = group.X, Y = group.Y, Width = group.Width, Height = group.Height };
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

        // [불러오기 기능 1] 다이얼로그
        public static async Task<string?> LoadDiagramWithDialogAsync(MainViewModel mainVm, IDockerService dockerService, IDialogService dialogService)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Docker Diagram (*.vdm)|*.vdm|JSON File (*.json)|*.json"
            };

            if (dlg.ShowDialog() == true)
            {
                await LoadDiagramFromPathAsync(mainVm, dlg.FileName, dockerService, dialogService);
                SaveLastFilePath(dlg.FileName);
                return dlg.FileName;
            }
            return null;
        }

        // [불러오기 기능 2] 경로 로드
        public static async Task LoadDiagramFromPathAsync(MainViewModel mainVm, string filePath, IDockerService dockerService, IDialogService dialogService)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                string json = await File.ReadAllTextAsync(filePath);
                var fileData = JsonSerializer.Deserialize<DiagramFile>(json);

                if (fileData == null) return;

                mainVm.Sheets.Clear();

                foreach (var sheetData in fileData.Sheets)
                {
                    var sheetVm = new SheetViewModel(sheetData.Title, dockerService, dialogService);

                    sheetVm.MapWidth = sheetData.MapWidth;
                    sheetVm.MapHeight = sheetData.MapHeight;
                    sheetVm.OffsetX = sheetData.OffsetX;
                    sheetVm.OffsetY = sheetData.OffsetY;
                    sheetVm.Scale = sheetData.Scale;

                    var nodeMap = new Dictionary<string, NodeViewModel>();

                    foreach (var nodeData in sheetData.Nodes)
                    {
                        var nodeVm = new NodeViewModel(dockerService, dialogService)
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
                            var connVm = new ConnectorViewModel(source, target, connData.SourceDir, connData.TargetDir, dockerService, dialogService);

                            connVm.RelationType = connData.RelationType;
                            connVm.MountPath = connData.MountPath;
                            connVm.IpAddress = connData.IpAddress;

                            sheetVm.Connectors.Add(connVm);
                        }
                    }

                    foreach (var groupData in sheetData.Groups)
                    {
                        // ★ [수정 완료] 생성자 매개변수 6개 (x, y, w, h, dialogService, title)
                        var groupVm = new GroupViewModel(
                            groupData.X,
                            groupData.Y,
                            groupData.Width,
                            groupData.Height,
                            dialogService, // <--- 여기에 dialogService 주입!
                            groupData.Title
                        );

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
            }
            catch (Exception ex)
            {
                // ★ [추가 수정] MessageBox 대신 주입받은 dialogService 사용
                dialogService.ShowMessage($"파일 불러오기 실패 ({filePath}):\n{ex.Message}");
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