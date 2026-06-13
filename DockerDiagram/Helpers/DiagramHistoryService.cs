using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 다이어그램 변경 기록과 Docker 리소스를 포함한 Undo/Redo 작업을 구성합니다.
    /// </summary>
    public sealed class DiagramHistoryService
    {
        public sealed record DiagramState(
            HashSet<string> NodeIds,
            HashSet<string> GroupIds,
            HashSet<ConnectorViewModel> Connectors);

        private readonly Func<IDockerService> _getActiveService;
        private readonly Func<SheetViewModel?> _getActiveSheet;
        private readonly Action _markModified;
        private readonly UndoRedoManagerViewModel _history;
        private readonly ResourceExplorerViewModel _explorer;
        private readonly IDialogService _dialogService;
        private readonly Dictionary<string, string> _volumeUndoBackups = new();

        public DiagramHistoryService(
            Func<IDockerService> getActiveService,
            Func<SheetViewModel?> getActiveSheet,
            Action markModified,
            UndoRedoManagerViewModel history,
            ResourceExplorerViewModel explorer,
            IDialogService dialogService)
        {
            _getActiveService = getActiveService;
            _getActiveSheet = getActiveSheet;
            _markModified = markModified;
            _history = history;
            _explorer = explorer;
            _dialogService = dialogService;
        }

        private IContainerService ContainerService => _getActiveService();
        private IVolumeService VolumeService => _getActiveService();
        private INetworkService NetworkService => _getActiveService();

        public DiagramState CaptureState(SheetViewModel sheet)
        {
            return new DiagramState(
                sheet.Nodes.Select(n => n.Id).ToHashSet(),
                sheet.Groups.Select(g => g.Id).ToHashSet(),
                sheet.Connectors.ToHashSet());
        }

        public void RecordAdditions(
            SheetViewModel sheet,
            DiagramState before,
            string description,
            bool affectsDocker)
        {
            if (_history.IsReplaying) return;

            var addedNodes = sheet.Nodes.Where(n => !before.NodeIds.Contains(n.Id)).ToList();
            var addedGroups = sheet.Groups.Where(g => !before.GroupIds.Contains(g.Id)).ToList();
            var addedConnectors = sheet.Connectors.Where(c => !before.Connectors.Contains(c)).ToList();

            if (addedNodes.Count == 0 && addedGroups.Count == 0 && addedConnectors.Count == 0) return;

            _history.RecordExecuted(new DelegateHistoryCommand(
                description,
                affectsDocker,
                undo: async () =>
                {
                    if (affectsDocker) await DeleteDockerObjectsAsync(addedNodes, addedGroups);
                    await RemoveDiagramBatchAsync(sheet, addedNodes, addedGroups, addedConnectors);
                    _markModified();
                },
                redo: async () =>
                {
                    if (affectsDocker) await RecreateDockerObjectsAsync(addedNodes, addedGroups);
                    await RestoreDiagramBatchAsync(sheet, addedNodes, addedGroups, addedConnectors);
                    _markModified();
                }));
        }

        public void RecordConnectorAdd(SheetViewModel sheet, ConnectorViewModel connector)
        {
            if (_history.IsReplaying) return;

            _history.RecordExecuted(new DelegateHistoryCommand(
                "Add connector",
                affectsDocker: false,
                undo: () =>
                {
                    sheet.Connectors.Remove(connector);
                    _markModified();
                    return Task.CompletedTask;
                },
                redo: () =>
                {
                    if (!sheet.Connectors.Contains(connector)) sheet.Connectors.Add(connector);
                    _markModified();
                    return Task.CompletedTask;
                }));
        }

        public void RecordNodeRectChange(NodeViewModel node, Rect before, Rect after, string description)
        {
            if (_history.IsReplaying || RectEquals(before, after)) return;

            _history.RecordExecuted(new DelegateHistoryCommand(
                description,
                affectsDocker: false,
                undo: async () =>
                {
                    ApplyNodeRect(node, before);
                    await RefreshGroupContainmentForNodeAsync(node);
                    _markModified();
                },
                redo: async () =>
                {
                    ApplyNodeRect(node, after);
                    await RefreshGroupContainmentForNodeAsync(node);
                    _markModified();
                },
                mergeKey: $"{node.Id}:{description}"));
        }

        public void RecordGroupRectChange(GroupViewModel group, Rect before, Rect after, string description)
        {
            if (_history.IsReplaying || RectEquals(before, after)) return;

            _history.RecordExecuted(new DelegateHistoryCommand(
                description,
                affectsDocker: false,
                undo: () =>
                {
                    ApplyGroupRect(group, before);
                    _markModified();
                    return Task.CompletedTask;
                },
                redo: () =>
                {
                    ApplyGroupRect(group, after);
                    _markModified();
                    return Task.CompletedTask;
                },
                mergeKey: $"{group.Id}:{description}"));
        }

        public IHistoryCommand CreateConnectorDeleteCommand(SheetViewModel sheet, ConnectorViewModel connector)
        {
            return new DelegateHistoryCommand(
                "Delete connector",
                affectsDocker: false,
                undo: () =>
                {
                    if (!sheet.Connectors.Contains(connector)) sheet.Connectors.Add(connector);
                    _markModified();
                    return Task.CompletedTask;
                },
                redo: () =>
                {
                    sheet.Connectors.Remove(connector);
                    _markModified();
                    return Task.CompletedTask;
                });
        }

        public IHistoryCommand CreateNodeDeleteCommand(
            SheetViewModel sheet,
            NodeViewModel node,
            bool deleteDocker,
            bool forceVolumeDelete = false)
        {
            var relatedConnectors = sheet.Connectors.Where(c => c.Source == node || c.Target == node).ToList();
            var containingGroups = sheet.Groups.Where(g => g.ContainedNodes.Contains(node)).ToList();
            bool affectsDocker = deleteDocker &&
                                 node.Type != NodeType.Internet &&
                                 !(node.Type == NodeType.Volume && node.VolumeExternal);

            return new DelegateHistoryCommand(
                affectsDocker ? $"Delete Docker {node.Type}: {node.Name}" : $"Delete diagram node: {node.Name}",
                affectsDocker,
                undo: async () =>
                {
                    if (affectsDocker) await RecreateDockerObjectsAsync(new[] { node }, Array.Empty<GroupViewModel>());
                    await RestoreNodeDiagramAsync(sheet, node, relatedConnectors, containingGroups);
                    _markModified();
                },
                redo: async () =>
                {
                    if (affectsDocker)
                    {
                        await DeleteDockerObjectsAsync(
                            new[] { node },
                            Array.Empty<GroupViewModel>(),
                            forceVolumeDelete);
                    }

                    await RemoveNodeFromDiagramOnlyAsync(sheet, node, relatedConnectors, containingGroups);
                    _markModified();
                });
        }

        public IHistoryCommand CreateGroupDeleteCommand(
            SheetViewModel sheet,
            GroupViewModel group,
            bool deleteDocker)
        {
            var relatedConnectors = sheet.Connectors
                .Where(c => c.Source == (IConnectableItem)group || c.Target == (IConnectableItem)group)
                .ToList();
            var containedNodes = group.ContainedNodes.ToList();
            bool affectsDocker = deleteDocker && group.Type == GroupType.Network && !group.External;

            return new DelegateHistoryCommand(
                affectsDocker ? $"Delete Docker network: {group.Title}" : $"Delete diagram group: {group.Title}",
                affectsDocker,
                undo: async () =>
                {
                    if (affectsDocker) await RecreateDockerObjectsAsync(Array.Empty<NodeViewModel>(), new[] { group });
                    await RestoreGroupDiagramAsync(sheet, group, relatedConnectors, containedNodes);
                    _markModified();
                },
                redo: async () =>
                {
                    if (affectsDocker) await DeleteDockerObjectsAsync(Array.Empty<NodeViewModel>(), new[] { group });
                    await RemoveGroupFromDiagramOnlyAsync(sheet, group, relatedConnectors);
                    _markModified();
                });
        }

        public async Task<(bool ShouldDelete, bool Force)> ConfirmVolumeDockerDeleteAsync(
            IVolumeService volumeService,
            string volumeName,
            bool allowForceAttempt)
        {
            var usedBy = await volumeService.GetContainersUsingVolumeAsync(volumeName);
            if (usedBy.Count == 0) return (true, false);

            string containerList = string.Join("\n", usedBy.Select(name => $"- {name}"));

            if (!allowForceAttempt)
            {
                _dialogService.ShowInfo(
                    $"볼륨 '{volumeName}'은(는) 현재 컨테이너에서 사용 중이라 Docker 삭제를 보호했습니다.\n\n사용 중인 컨테이너:\n{containerList}",
                    "Volume Delete Protection");
                return (false, false);
            }

            var result = _dialogService.ShowYesNoCancel(
                $"볼륨 '{volumeName}'은(는) 현재 컨테이너에서 사용 중입니다.\n\n" +
                $"사용 중인 컨테이너:\n{containerList}\n\n" +
                "[예(Yes)] : 강제 삭제를 시도\n" +
                "[아니요(No)] : 보호하고 취소\n" +
                "[취소(Cancel)] : 취소",
                "Volume Delete Protection");

            return result == MessageBoxResult.Yes ? (true, true) : (false, false);
        }

        private async Task DeleteDockerObjectsAsync(
            IEnumerable<NodeViewModel> nodes,
            IEnumerable<GroupViewModel> groups,
            bool forceVolumeDelete = false)
        {
            foreach (var node in nodes)
            {
                try
                {
                    if (node.Type == NodeType.Container && !string.IsNullOrWhiteSpace(node.ContainerId))
                    {
                        await ContainerService.RemoveContainerAsync(node.ContainerId);
                        node.IsDockerConnected = false;
                    }
                    else if (node.Type == NodeType.Volume)
                    {
                        if (node.VolumeExternal) continue;

                        var decision = forceVolumeDelete
                            ? (ShouldDelete: true, Force: true)
                            : await ConfirmVolumeDockerDeleteAsync(
                                VolumeService,
                                node.EffectiveVolumeName,
                                allowForceAttempt: false);
                        if (!decision.ShouldDelete) continue;

                        if (_history.IncludeVolumeBackupForUndo)
                        {
                            await BackupVolumeForUndoAsync(node);
                        }

                        await VolumeService.RemoveVolumeAsync(node.EffectiveVolumeName, decision.Force);
                        node.IsDockerConnected = false;
                    }
                }
                catch (Exception ex)
                {
                    if (node.Type == NodeType.Volume)
                    {
                        _dialogService.ShowError(
                            $"볼륨 '{node.EffectiveVolumeName}' 삭제 실패:\n{ex.Message}",
                            "Volume Delete");
                    }

                    Debug.WriteLine($"[History] Docker delete skipped: {ex.Message}");
                }
            }

            foreach (var group in groups.Where(g => g.Type == GroupType.Network))
            {
                if (group.External) continue;

                try
                {
                    await NetworkService.RemoveNetworkAsync(
                        !string.IsNullOrWhiteSpace(group.Id) ? group.Id : group.DockerNetworkName);
                    group.IsDockerConnected = false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[History] Docker network delete skipped: {ex.Message}");
                }
            }

            await _explorer.SyncWithDockerEngineAsync();
        }

        private async Task RecreateDockerObjectsAsync(
            IEnumerable<NodeViewModel> nodes,
            IEnumerable<GroupViewModel> groups)
        {
            foreach (var group in groups.Where(g => g.Type == GroupType.Network))
            {
                try
                {
                    if (group.External)
                    {
                        var networks = await NetworkService.GetNetworksAsync();
                        var existingNetwork = networks.FirstOrDefault(n =>
                            string.Equals(n.Name, group.DockerNetworkName, StringComparison.OrdinalIgnoreCase));
                        if (existingNetwork == null)
                        {
                            throw new InvalidOperationException(
                                $"External network '{group.DockerNetworkName}' was not found.");
                        }

                        group.Id = existingNetwork.Id;
                    }
                    else
                    {
                        group.Id = await NetworkService.CreateNetworkAsync(group.ToNetworkCreateOptions());
                    }

                    group.IsDockerConnected = true;
                }
                catch (Exception ex)
                {
                    if (!ex.Message.Contains("already exists") && !ex.Message.Contains("409")) throw;
                    group.IsDockerConnected = true;
                }
            }

            foreach (var node in nodes)
            {
                if (node.Type == NodeType.Volume)
                {
                    try
                    {
                        if (node.VolumeExternal)
                        {
                            await VolumeService.InspectVolumeAsync(node.EffectiveVolumeName);
                        }
                        else
                        {
                            await VolumeService.CreateVolumeAsync(new VolumeCreateOptions
                            {
                                Name = node.Name,
                                DockerVolumeName = node.DockerVolumeName,
                                Driver = string.IsNullOrWhiteSpace(node.Driver) || node.Driver == "-"
                                    ? "local"
                                    : node.Driver,
                                Labels = new Dictionary<string, string>(node.VolumeLabels),
                                DriverOptions = new Dictionary<string, string>(node.VolumeDriverOptions)
                            });
                        }

                        node.IsDockerConnected = true;
                        await RestoreVolumeFromUndoBackupAsync(node);
                    }
                    catch (Exception ex)
                    {
                        if (!ex.Message.Contains("already exists") && !ex.Message.Contains("409")) throw;
                        node.IsDockerConnected = true;
                        await RestoreVolumeFromUndoBackupAsync(node);
                    }
                }
                else if (node.Type == NodeType.Container)
                {
                    var (image, tag) = SplitImageTag(node.ImageName);
                    string newId = await ContainerService.CreateAndStartContainerAsync(
                        node.Name,
                        image,
                        tag,
                        node.PortBindings?.ToList() ?? new List<string>(),
                        node.EnvironmentVariables?.ToList() ?? new List<string>(),
                        new List<string>(),
                        string.IsNullOrWhiteSpace(node.RestartPolicy) ? "no" : node.RestartPolicy,
                        0,
                        0);

                    node.ContainerId = newId;
                    node.IsDockerConnected = true;
                    await node.RefreshDetailsAsync();
                }
            }

            await _explorer.SyncWithDockerEngineAsync();
        }

        private async Task BackupVolumeForUndoAsync(NodeViewModel node)
        {
            if (node.Type != NodeType.Volume || node.VolumeExternal) return;

            if (_volumeUndoBackups.TryGetValue(node.Id, out var previousPath))
            {
                VolumeUndoBackupStore.DeleteFile(previousPath);
            }

            string backupPath = VolumeUndoBackupStore.CreateBackupPath(node.EffectiveVolumeName);
            await VolumeService.BackupVolumeAsync(node.EffectiveVolumeName, backupPath);
            _volumeUndoBackups[node.Id] = backupPath;
            node.DetailStatus = "Ghost backup";
            node.IsDockerConnected = false;
        }

        private async Task RestoreVolumeFromUndoBackupAsync(NodeViewModel node)
        {
            if (node.Type != NodeType.Volume || node.VolumeExternal) return;
            if (!_volumeUndoBackups.TryGetValue(node.Id, out var backupPath)) return;
            if (!File.Exists(backupPath)) return;

            await VolumeService.RestoreVolumeAsync(node.EffectiveVolumeName, backupPath);
            node.IsDockerConnected = true;
            await node.RefreshDetailsAsync();
        }

        private async Task RefreshGroupContainmentForNodeAsync(NodeViewModel node)
        {
            var activeSheet = _getActiveSheet();
            if (activeSheet == null) return;

            var targetGroups = activeSheet.FindGroupsAt(node.X, node.Y, node.Width, node.Height);
            foreach (var group in activeSheet.Groups)
            {
                if (targetGroups.Contains(group))
                {
                    await group.AddNodeAsync(node, isRestoring: true);
                }
                else
                {
                    await group.RemoveNodeAsync(node, isRestoring: true);
                }
            }
        }

        private static async Task RemoveDiagramBatchAsync(
            SheetViewModel sheet,
            List<NodeViewModel> nodes,
            List<GroupViewModel> groups,
            List<ConnectorViewModel> connectors)
        {
            foreach (var connector in connectors.ToList())
            {
                sheet.Connectors.Remove(connector);
            }

            foreach (var group in groups.ToList())
            {
                await RemoveGroupFromDiagramOnlyAsync(
                    sheet,
                    group,
                    sheet.Connectors
                        .Where(c => c.Source == (IConnectableItem)group || c.Target == (IConnectableItem)group)
                        .ToList());
            }

            foreach (var node in nodes.ToList())
            {
                await RemoveNodeFromDiagramOnlyAsync(
                    sheet,
                    node,
                    sheet.Connectors.Where(c => c.Source == node || c.Target == node).ToList(),
                    sheet.Groups.Where(g => g.ContainedNodes.Contains(node)).ToList());
            }
        }

        private static async Task RestoreDiagramBatchAsync(
            SheetViewModel sheet,
            List<NodeViewModel> nodes,
            List<GroupViewModel> groups,
            List<ConnectorViewModel> connectors)
        {
            foreach (var node in nodes)
            {
                if (!sheet.Nodes.Contains(node)) sheet.Nodes.Add(node);
            }

            foreach (var group in groups)
            {
                if (!sheet.Groups.Contains(group)) sheet.AddGroup(group);
            }

            foreach (var group in groups)
            {
                foreach (var node in nodes.Where(n => IsNodeInsideGroup(n, group)))
                {
                    await group.AddNodeAsync(node, isRestoring: true);
                }
            }

            foreach (var connector in connectors)
            {
                if (!sheet.Connectors.Contains(connector)) sheet.Connectors.Add(connector);
            }

            sheet.UpdateGroupLayering();
        }

        private static async Task RemoveNodeFromDiagramOnlyAsync(
            SheetViewModel sheet,
            NodeViewModel node,
            List<ConnectorViewModel> connectors,
            List<GroupViewModel> containingGroups)
        {
            foreach (var connector in connectors.ToList())
            {
                sheet.Connectors.Remove(connector);
            }

            foreach (var group in containingGroups.ToList())
            {
                await group.RemoveNodeAsync(node, isRestoring: true);
            }

            sheet.Nodes.Remove(node);
        }

        private static async Task RestoreNodeDiagramAsync(
            SheetViewModel sheet,
            NodeViewModel node,
            List<ConnectorViewModel> connectors,
            List<GroupViewModel> containingGroups)
        {
            if (!sheet.Nodes.Contains(node)) sheet.Nodes.Add(node);

            foreach (var group in containingGroups)
            {
                if (sheet.Groups.Contains(group))
                {
                    await group.AddNodeAsync(node, isRestoring: true);
                }
            }

            foreach (var connector in connectors)
            {
                if (!sheet.Connectors.Contains(connector)) sheet.Connectors.Add(connector);
            }
        }

        private static Task RemoveGroupFromDiagramOnlyAsync(
            SheetViewModel sheet,
            GroupViewModel group,
            List<ConnectorViewModel> connectors)
        {
            foreach (var connector in connectors.ToList())
            {
                sheet.Connectors.Remove(connector);
            }

            group.ContainedNodes.Clear();
            sheet.Groups.Remove(group);
            sheet.UpdateGroupLayering();
            return Task.CompletedTask;
        }

        private static async Task RestoreGroupDiagramAsync(
            SheetViewModel sheet,
            GroupViewModel group,
            List<ConnectorViewModel> connectors,
            List<NodeViewModel> containedNodes)
        {
            if (!sheet.Groups.Contains(group)) sheet.AddGroup(group);

            foreach (var node in containedNodes)
            {
                if (sheet.Nodes.Contains(node))
                {
                    await group.AddNodeAsync(node, isRestoring: true);
                }
            }

            foreach (var connector in connectors)
            {
                if (!sheet.Connectors.Contains(connector)) sheet.Connectors.Add(connector);
            }

            sheet.UpdateGroupLayering();
        }

        private static void ApplyNodeRect(NodeViewModel node, Rect rect)
        {
            node.X = rect.X;
            node.Y = rect.Y;
            node.Width = rect.Width;
            node.Height = rect.Height;
        }

        private static void ApplyGroupRect(GroupViewModel group, Rect rect)
        {
            group.X = rect.X;
            group.Y = rect.Y;
            group.Width = rect.Width;
            group.Height = rect.Height;
        }

        private static bool RectEquals(Rect a, Rect b)
        {
            return Math.Abs(a.X - b.X) < 0.1 &&
                   Math.Abs(a.Y - b.Y) < 0.1 &&
                   Math.Abs(a.Width - b.Width) < 0.1 &&
                   Math.Abs(a.Height - b.Height) < 0.1;
        }

        private static bool IsNodeInsideGroup(NodeViewModel node, GroupViewModel group)
        {
            var centerX = node.X + node.Width / 2;
            var centerY = node.Y + node.Height / 2;
            return centerX >= group.X &&
                   centerX <= group.X + group.Width &&
                   centerY >= group.Y &&
                   centerY <= group.Y + group.Height;
        }

        private static (string Image, string Tag) SplitImageTag(string imageName)
        {
            if (string.IsNullOrWhiteSpace(imageName)) return ("ubuntu", "latest");

            int lastColon = imageName.LastIndexOf(':');
            if (lastColon > 0 && lastColon < imageName.Length - 1)
            {
                return (imageName[..lastColon], imageName[(lastColon + 1)..]);
            }

            return (imageName, "latest");
        }
    }
}
