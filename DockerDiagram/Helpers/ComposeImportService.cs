using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DockerDiagram.Helpers
{
    public static class ComposeImportService
    {
        public static async Task ImportFromCompose(
            MainViewModel mainVm,
            IContainerService containerService,
            IVolumeService volumeService,
            INetworkService networkService,
            IDialogService dialogService)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Docker Compose File (*.yml;*.yaml)|*.yml;*.yaml|All Files (*.*)|*.*",
                Title = "Import from Docker Compose"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                string yamlContent = File.ReadAllText(dlg.FileName);

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var composeData = deserializer.Deserialize<ComposeFileModel>(yamlContent);

                if (composeData == null || composeData.Services == null || composeData.Services.Count == 0)
                {
                    dialogService.ShowMessage("유효한 Compose 파일이 아니거나 Service가 없습니다.");
                    return;
                }

                string sheetName = Path.GetFileNameWithoutExtension(dlg.FileName);

                // =================================================================
                // ★ [핵심 수정] 새 시트를 만들 때 기본 로컬 프로필을 달아줍니다!
                // =================================================================
                var defaultProfile = new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
                var newSheet = new SheetViewModel(sheetName, defaultProfile, (IDockerService)containerService, dialogService);
                // =================================================================

                var nodeMap = new Dictionary<string, NodeViewModel>();
                var groupMap = new Dictionary<string, GroupViewModel>();

                // 1단계: 노드 객체 생성
                if (composeData.Volumes != null)
                {
                    foreach (var vol in composeData.Volumes)
                    {
                        nodeMap[vol.Key] = new NodeViewModel(containerService, volumeService, dialogService)
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = vol.Key,
                            Type = NodeType.Volume,
                            Width = 120,
                            Height = 60
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
                        Width = 150,
                        Height = 80,
                        RestartPolicy = svc.Value.Restart ?? "no",
                        PortBindings = svc.Value.Ports != null ? new List<string>(svc.Value.Ports) : new List<string>(),
                        EnvironmentVariables = svc.Value.Environment != null ? new List<string>(svc.Value.Environment) : new List<string>()
                    };
                    newSheet.Nodes.Add(nodeMap[svc.Key]);
                }

                // 2단계: 네트워크 분류 및 선 긋기
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

                                if (primaryNet != null && !volumeToNetMap.ContainsKey(targetVolume))
                                {
                                    volumeToNetMap[targetVolume] = primaryNet;
                                    visualGroupMap[primaryNet].Add(targetVolume);
                                }
                            }
                        }
                    }

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

                foreach (var volNode in nodeMap.Values.Where(n => n.Type == NodeType.Volume))
                {
                    if (!volumeToNetMap.ContainsKey(volNode)) unassignedNodes.Add(volNode);
                }

                // 3단계: 2열(2xN) 자동 배치
                double currentGroupX = 50;
                double currentGroupY = 50;
                double maxGroupHeightInRow = 0;

                foreach (var kvp in visualGroupMap)
                {
                    string netName = kvp.Key;
                    var nodesInGroup = kvp.Value;

                    int cols = 2;
                    int rows = Math.Max(1, (int)Math.Ceiling(nodesInGroup.Count / (double)cols));
                    double cellWidth = 180, cellHeight = 110;
                    double gWidth = (cols * cellWidth) + 40;
                    double gHeight = (rows * cellHeight) + 60;

                    var groupVm = new GroupViewModel(currentGroupX, currentGroupY, gWidth, gHeight, networkService, dialogService, netName)
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = GroupType.Network
                    };
                    newSheet.Groups.Add(groupVm);
                    groupMap[netName] = groupVm;

                    for (int i = 0; i < nodesInGroup.Count; i++)
                    {
                        var node = nodesInGroup[i];
                        int r = i / cols;
                        int c = i % cols;

                        node.X = currentGroupX + 20 + (c * cellWidth);
                        node.Y = currentGroupY + 50 + (r * cellHeight);

                        groupVm.AddNode(node, true);
                    }

                    currentGroupX += gWidth + 50;
                    if (gHeight > maxGroupHeightInRow) maxGroupHeightInRow = gHeight;

                    if (currentGroupX > 1200)
                    {
                        currentGroupX = 50;
                        currentGroupY += maxGroupHeightInRow + 50;
                        maxGroupHeightInRow = 0;
                    }
                }

                if (unassignedNodes.Count > 0)
                {
                    currentGroupY += maxGroupHeightInRow + 50;
                    currentGroupX = 50;

                    for (int i = 0; i < unassignedNodes.Count; i++)
                    {
                        var node = unassignedNodes[i];
                        node.X = currentGroupX + ((i % 4) * 180);
                        node.Y = currentGroupY + ((i / 4) * 110);
                    }
                }

                mainVm.Sheets.Add(newSheet);
                mainVm.ActiveSheet = newSheet;

                // 4단계: 도커 배포 프로세스
                var result = System.Windows.MessageBox.Show(
                    $"[{sheetName}] 화면 구성을 완료했습니다.\n이 구성대로 실제 도커 엔진에 컨테이너들을 배포(실행)하시겠습니까?",
                    "실제 도커 배포",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    var containerNodes = newSheet.Nodes.Where(n => n.Type == NodeType.Container).ToList();

                    foreach (var node in containerNodes) node.IsCreating = true;

                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "docker",
                            Arguments = $"compose -f \"{dlg.FileName}\" up -d",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(dlg.FileName)
                        };

                        using var process = new System.Diagnostics.Process { StartInfo = psi };
                        var errorBuilder = new System.Text.StringBuilder();

                        process.OutputDataReceived += (sender, e) => { };
                        process.ErrorDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data)) errorBuilder.AppendLine(e.Data);
                        };

                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        await Task.Run(() => process.WaitForExit());

                        foreach (var node in containerNodes) node.IsCreating = false;

                        if (process.ExitCode == 0)
                        {
                            var allContainers = await containerService.GetContainersAsync();

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
                                    await node.RefreshDetailsAsync();
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