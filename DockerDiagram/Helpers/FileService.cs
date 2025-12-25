using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using System.Diagnostics;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Helpers
{
    public static class FileService
    {
        // 다이어그램 저장.
        public static string? SaveDiagramAs(MainViewModel mainVm)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Docker Diagram (*.vdm)|*.vdm",
                DefaultExt = ".vdm",
                // 기존 경로가 있다면 파일명 칸에 미리 채워주기 (사용자 편의)
                FileName = !string.IsNullOrEmpty(mainVm.CurrentFilePath)
                           ? Path.GetFileName(mainVm.CurrentFilePath)
                           : "MyDockerLayout"
            };

            if (dlg.ShowDialog() == true)
            {
                if (InternalSave(mainVm, dlg.FileName))
                {
                    // Properties.Settings 업데이트 (최근 파일 갱신)
                    SaveLastFilePath(dlg.FileName);

                    MessageBox.Show($"저장되었습니다.\n{dlg.FileName}", "완료", MessageBoxButton.OK, MessageBoxImage.Information);

                    return dlg.FileName;
                }
            }
            // 취소하거나 실패하면 null 리턴
            return null;
        }

        // 경로만 주면 저장
        public static bool QuickSave(MainViewModel mainVm, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            bool result = InternalSave(mainVm, path);
            if (result) SaveLastFilePath(path); // 최근 파일 갱신
            return result;
        }

        // 3. 실제 저장
        private static bool InternalSave(MainViewModel mainVm, string filePath)
        {
            try
            {
                var fileData = new DiagramFile();

                // 1. 현재 활성화된 시트 번호 저장
                if (mainVm.ActiveSheet != null)
                    fileData.ActiveSheetIndex = mainVm.Sheets.IndexOf(mainVm.ActiveSheet);

                // 2. 뷰모델(화면)에 있는 내용을 저장용 객체로 옮겨 담기
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

                    // 노드들 옮기기
                    foreach (var nodeVm in sheetVm.Nodes)
                    {
                        sheetData.Nodes.Add(new NodeData
                        {
                            Id = nodeVm.Id,
                            DockerId = nodeVm.ContainerId, // 매핑 주의: ContainerId -> DockerId
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

                    // 연결선들 옮기기
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

                    // 그룹들 옮기기
                    foreach (var group in sheetVm.Groups)
                    {
                        var gData = new GroupData { Title = group.Title, X = group.X, Y = group.Y, Width = group.Width, Height = group.Height };
                        gData.ContainedNodeIds = group.ContainedNodes.Select(n => n.Id).ToList();
                        sheetData.Groups.Add(gData);
                    }

                    // 다 채운 시트 데이터를 파일 객체에 추가
                    fileData.Sheets.Add(sheetData);
                }

                // 3. 파일로 쓰기 (덮어쓰기 모드)
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(fileData, options);
                File.WriteAllText(filePath, jsonString); // Append가 아니라 WriteAllText여야 함

                // 4. 최근 파일 경로 갱신
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

        // [불러오기 기능 1] 사용자가 버튼 눌렀을 때 (다이얼로그 띄움) 아직 쓰지 않음. 까먹었네
        public static async Task<string?> LoadDiagramWithDialogAsync(MainViewModel mainVm)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Docker Diagram (*.vdm)|*.vdm|JSON File (*.json)|*.json"
            };

            if (dlg.ShowDialog() == true)
            {
                await LoadDiagramFromPathAsync(mainVm, dlg.FileName);
                SaveLastFilePath(dlg.FileName); // 최근 파일 갱신
                return dlg.FileName;
            }
            return null;
        }

        // [불러오기 기능 2] 경로를 알 때 바로 로드
        public static async Task LoadDiagramFromPathAsync(MainViewModel mainVm, string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                // 1. 비동기로 파일 읽기
                string json = await File.ReadAllTextAsync(filePath);

                // 2. JSON 역직렬화
                var fileData = JsonSerializer.Deserialize<DiagramFile>(json);

                if (fileData == null) return;

                // 3. 기존 데이터 초기화 (새로 불러오기)
                mainVm.Sheets.Clear();

                foreach (var sheetData in fileData.Sheets)
                {
                    var sheetVm = new SheetViewModel(sheetData.Title);

                    // 맵 뷰포트 상태 복원
                    sheetVm.MapWidth = sheetData.MapWidth;
                    sheetVm.MapHeight = sheetData.MapHeight;
                    sheetVm.OffsetX = sheetData.OffsetX;
                    sheetVm.OffsetY = sheetData.OffsetY;
                    sheetVm.Scale = sheetData.Scale;

                    // ID 매핑용 딕셔너리 (파일에 저장된 ID -> 새로 만든 객체)
                    // 연결선이나 그룹을 복원할 때 필요합니다.
                    var nodeMap = new Dictionary<string, NodeViewModel>();

                    // 4. 노드 복원
                    foreach (var nodeData in sheetData.Nodes)
                    {
                        var nodeVm = new NodeViewModel
                        {
                            // [중요] 저장된 ID를 그대로 복원해야 연결선이 끊기지 않습니다.
                            // (NodeViewModel.cs의 Id 프로퍼티에 'set;' 접근자가 필요합니다)
                            Id = nodeData.Id,

                            ContainerId = nodeData.DockerId, // Docker ID 복원
                            Name = nodeData.Name,
                            ImageName = nodeData.ImageName,
                            Type = nodeData.Type,
                            X = nodeData.X,
                            Y = nodeData.Y,
                            Width = nodeData.Width,
                            Height = nodeData.Height,

                            // [추가됨] 오프라인 설정 복원 (Compose 내보내기용 핵심 데이터)
                            PortBindings = nodeData.PortBindings ?? new List<string>(),
                            EnvironmentVariables = nodeData.EnvironmentVariables ?? new List<string>(),
                            RestartPolicy = nodeData.RestartPolicy ?? "no",

                            // 로드 직후에는 '오프라인' 상태로 간주 (회색)
                            // (실제 Docker와 동기화되면 MainViewModel의 로직에 의해 초록/빨강으로 바뀜)
                            StatusColor = "#808080"
                        };

                        sheetVm.Nodes.Add(nodeVm);
                        nodeMap[nodeVm.Id] = nodeVm;
                    }

                    // 5. 연결선 복원
                    foreach (var connData in sheetData.Connections)
                    {
                        // Source와 Target 노드가 모두 존재할 때만 연결
                        if (nodeMap.TryGetValue(connData.SourceNodeId, out var source) &&
                            nodeMap.TryGetValue(connData.TargetNodeId, out var target))
                        {
                            var connVm = new ConnectorViewModel(source, target, connData.SourceDir, connData.TargetDir);

                            connVm.RelationType = connData.RelationType;

                            connVm.MountPath = connData.MountPath; // 볼륨 마운트 경로
                            connVm.IpAddress = connData.IpAddress; // 네트워크 고정 IP

                            sheetVm.Connectors.Add(connVm);
                        }
                    }

                    // 6. 그룹 복원
                    foreach (var groupData in sheetData.Groups)
                    {
                        var groupVm = new GroupViewModel(groupData.X, groupData.Y, groupData.Width, groupData.Height, groupData.Title);
                        groupVm.ParentSheet = sheetVm; // 부모 시트 연결 필수

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

                // 7. 활성 시트 설정
                if (mainVm.Sheets.Count > 0 && fileData.ActiveSheetIndex < mainVm.Sheets.Count)
                {
                    mainVm.ActiveSheet = mainVm.Sheets[fileData.ActiveSheetIndex];
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"파일 불러오기 실패 ({filePath}):\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
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