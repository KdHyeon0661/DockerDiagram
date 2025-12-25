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
                // ★ [가장 중요] 저장할 때마다 반드시 '새 상자(new)'를 꺼내야 합니다!
                // 만약 이 변수가 메서드 밖(static)에 있거나, 여기서 new를 안 하면 내용이 계속 쌓입니다.
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
                    foreach (var node in sheetVm.Nodes)
                    {
                        sheetData.Nodes.Add(new NodeData
                        {
                            Id = node.Id,
                            DockerId = node.ContainerId,
                            Name = node.Name,
                            ImageName = node.ImageName,
                            Type = node.Type,
                            X = node.X,
                            Y = node.Y,
                            Width = node.Width,
                            Height = node.Height
                        });
                    }

                    // 연결선들 옮기기
                    foreach (var conn in sheetVm.Connectors)
                    {
                        sheetData.Connections.Add(new ConnectionData
                        {
                            SourceNodeId = conn.Source.Id,
                            TargetNodeId = conn.Target.Id,
                            SourceDir = conn.SourceDir,
                            TargetDir = conn.TargetDir,
                            RelationType = (DockerDiagram.Models.RelationType)conn.RelationType
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
                if (!File.Exists(filePath)) return; // 프로퍼티에 저장되어 있는 경로가 없을 수도 있으니 체크

                string jsonString = await File.ReadAllTextAsync(filePath);
                var fileData = JsonSerializer.Deserialize<DiagramFile>(jsonString); // json 역직렬화

                if (fileData == null) throw new Exception("파일 형식이 올바르지 않습니다.");

                // 기존 시트 클리어()
                mainVm.Sheets.Clear();

                foreach (var sheetData in fileData.Sheets) // 파일에 있는 시트 수만큼 반복
                {
                    var sheetVm = new SheetViewModel(sheetData.Title) // 시트 뷰모델 생성
                    {
                        MapWidth = sheetData.MapWidth,
                        MapHeight = sheetData.MapHeight,
                        OffsetX = sheetData.OffsetX,
                        OffsetY = sheetData.OffsetY,
                        Scale = sheetData.Scale
                    };

                    var nodeMap = new Dictionary<string, NodeViewModel>();

                    // 노드 복구
                    foreach (var nData in sheetData.Nodes)
                    {
                        var nodeVm = new NodeViewModel
                        {
                            ContainerId = nData.DockerId, // Docker ID 복구
                            Name = nData.Name,
                            ImageName = nData.ImageName,
                            Type = nData.Type,
                            X = nData.X,
                            Y = nData.Y,
                            Width = nData.Width,
                            Height = nData.Height
                        };

                        // Docker 상태 동기화
                        await nodeVm.RefreshDetailsAsync();

                        sheetVm.Nodes.Add(nodeVm);
                        nodeMap[nData.Id] = nodeVm;
                    }

                    // 연결선 복구
                    foreach (var cData in sheetData.Connections)
                    {
                        if (nodeMap.TryGetValue(cData.SourceNodeId, out var source) &&
                            nodeMap.TryGetValue(cData.TargetNodeId, out var target))
                        {
                            var connVm = new ConnectorViewModel(source, target, cData.SourceDir, cData.TargetDir)
                            {
                                RelationType = (DockerDiagram.ViewModels.RelationType)cData.RelationType
                            };
                            sheetVm.Connectors.Add(connVm);
                        }
                    }

                    // 그룹 복구
                    foreach (var gData in sheetData.Groups)
                    {
                        var groupVm = new GroupViewModel(gData.X, gData.Y, gData.Width, gData.Height, gData.Title);
                        groupVm.ParentSheet = sheetVm;
                        foreach (var nodeId in gData.ContainedNodeIds)
                        {
                            if (nodeMap.TryGetValue(nodeId, out var node)) groupVm.AddNode(node);
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