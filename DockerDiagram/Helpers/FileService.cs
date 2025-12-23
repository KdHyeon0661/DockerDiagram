using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Helpers
{
    public static class FileService
    {
        // 다이어그램 저장.
        public static void SaveDiagram(MainViewModel mainVm)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Docker Diagram (*.vdm)|*.vdm",
                DefaultExt = ".vdm",
                FileName = "MyDockerLayout"
            };

            if (dlg.ShowDialog() == true)
            {
                if (InternalSave(mainVm, dlg.FileName))
                {
                    MessageBox.Show($"저장되었습니다.\n{dlg.FileName}", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        // 경로만 주면 저장
        public static bool QuickSave(MainViewModel mainVm, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return InternalSave(mainVm, path);
        }

        // 3. 실제 저장
        private static bool InternalSave(MainViewModel mainVm, string filePath)
        {
            try
            {
                var fileData = new DiagramFile(); // 저장용 객체
                if (mainVm.ActiveSheet != null)
                    fileData.ActiveSheetIndex = mainVm.Sheets.IndexOf(mainVm.ActiveSheet);

                foreach (var sheetVm in mainVm.Sheets) // 뷰모델에 있는 시트 수만큼 반복
                {
                    var sheetData = new SheetData // 시트의 정보
                    {
                        Title = sheetVm.Title, // 시트 제목
                        MapWidth = sheetVm.MapWidth, // 시트 맵 너비
                        MapHeight = sheetVm.MapHeight, // 시트 맵 높이
                        OffsetX = sheetVm.OffsetX, // 시트 오프셋 X(내 시점 좌표)
                        OffsetY = sheetVm.OffsetY, // 시트 오프셋 Y(내 시점 좌표)
                        Scale = sheetVm.Scale // 시트 스케일(내가 몇 배율 확대, 축소를 했는가)
                    };

                    foreach (var node in sheetVm.Nodes) // 시트에 있는 노드들
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

                    foreach (var conn in sheetVm.Connectors) // 시트에 있는 노드간 연결선들
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

                    foreach (var group in sheetVm.Groups) // 시트에 있는 그룹들 grouping 기능
                    {
                        var gData = new GroupData { Title = group.Title, X = group.X, Y = group.Y, Width = group.Width, Height = group.Height };
                        gData.ContainedNodeIds = group.ContainedNodes.Select(n => n.Id).ToList();
                        sheetData.Groups.Add(gData);
                    }
                    fileData.Sheets.Add(sheetData);
                }

                var options = new JsonSerializerOptions { WriteIndented = true }; // json 직렬화
                string jsonString = JsonSerializer.Serialize(fileData, options);
                File.WriteAllText(filePath, jsonString); // 쓰기

                // 마지막 경로 기억
                Properties.Settings.Default.LastFilePath = filePath;
                Properties.Settings.Default.Save();

                mainVm.IsModified = false;

                return true;
            }
            catch (Exception ex)
            {
                // 자동 저장 중 에러
                System.Diagnostics.Debug.WriteLine($"AutoSave Error: {ex.Message}");
                return false;
            }
        }

        // [불러오기 기능 1] 사용자가 버튼 눌렀을 때 (다이얼로그 띄움) 아직 쓰지 않음. 까먹었네
        public static async Task LoadDiagramWithDialogAsync(MainViewModel mainVm)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Docker Diagram (*.vdm)|*.vdm|JSON File (*.json)|*.json"
            };

            if (dlg.ShowDialog() == true)
            {
                // 아래의 '경로로 불러오기' 메서드를 재사용합니다.
                await LoadDiagramFromPathAsync(mainVm, dlg.FileName);

                // 불러온 경로를 다시 기억 (최근 파일 갱신)
                try
                {
                    Properties.Settings.Default.LastFilePath = dlg.FileName;
                    Properties.Settings.Default.Save();
                }
                catch { }
            }
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
    }
}