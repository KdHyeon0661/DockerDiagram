using DockerDiagram.Contracts;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// 다이어그램 변경 기록과 Docker 리소스를 포함한 Undo/Redo 작업을 구성합니다.
    /// </summary>
    public sealed class DiagramHistoryService
    {
        public sealed record DiagramState(
            HashSet<NodeViewModel> Nodes,
            HashSet<GroupViewModel> Groups,
            HashSet<ConnectorViewModel> Connectors);

        private readonly Func<SheetViewModel?> _getActiveSheet;
        private readonly Func<Task> _requestSync;
        private readonly Action _markModified;
        private readonly UndoRedoManagerViewModel _history;
        private readonly IDialogService _dialogService;
        private readonly Dictionary<string, string> _volumeUndoBackups = new();
        private readonly Dictionary<string, ContainerUndoSnapshot> _containerUndoSnapshots = new();

        private sealed record ContainerUndoSnapshot(ContainerInspectResponse Inspect, bool WasRunning);

        public DiagramHistoryService(
            Func<SheetViewModel?> getActiveSheet,
            Func<Task> requestSync,
            Action markModified,
            UndoRedoManagerViewModel history,
            IDialogService dialogService)
        {
            _getActiveSheet = getActiveSheet;
            _requestSync = requestSync;
            _markModified = markModified;
            _history = history;
            _dialogService = dialogService;
        }

        public DiagramState CaptureState(SheetViewModel sheet)
        {
            return new DiagramState(
                new HashSet<NodeViewModel>(sheet.Nodes, ReferenceEqualityComparer.Instance),
                new HashSet<GroupViewModel>(sheet.Groups, ReferenceEqualityComparer.Instance),
                sheet.Connectors.ToHashSet());
        }

        public void RecordAdditions(
            SheetViewModel sheet,
            DiagramState before,
            string description,
            bool affectsDocker)
        {
            if (_history.IsReplaying) return;

            var addedNodes = sheet.Nodes.Where(n => !before.Nodes.Contains(n)).ToList();
            var addedGroups = sheet.Groups.Where(g => !before.Groups.Contains(g)).ToList();
            var addedConnectors = sheet.Connectors.Where(c => !before.Connectors.Contains(c)).ToList();

            if (addedNodes.Count == 0 && addedGroups.Count == 0 && addedConnectors.Count == 0) return;

            IDockerService targetService = sheet.DockerService;

            _history.RecordExecuted(new DelegateHistoryCommand(
                description,
                affectsDocker,
                undo: async () =>
                {
                    if (affectsDocker) await DeleteDockerObjectsAsync(sheet, targetService, addedNodes, addedGroups);
                    await RemoveDiagramBatchAsync(sheet, addedNodes, addedGroups, addedConnectors);
                    _markModified();
                },
                redo: async () =>
                {
                    if (affectsDocker) await RecreateDockerObjectsAsync(sheet, targetService, addedNodes, addedGroups);
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
            SheetViewModel? sheet = node.ParentSheet;

            _history.RecordExecuted(new DelegateHistoryCommand(
                description,
                affectsDocker: false,
                undo: async () =>
                {
                    ApplyNodeRect(node, before);
                    if (sheet != null) await RefreshGroupContainmentForNodeAsync(sheet, node);
                    _markModified();
                },
                redo: async () =>
                {
                    ApplyNodeRect(node, after);
                    if (sheet != null) await RefreshGroupContainmentForNodeAsync(sheet, node);
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

        public void RecordLayoutChange(
            SheetViewModel sheet,
            IReadOnlyDictionary<NodeViewModel, Rect> beforeNodes,
            IReadOnlyDictionary<NodeViewModel, Rect> afterNodes,
            IReadOnlyDictionary<GroupViewModel, Rect> beforeGroups,
            IReadOnlyDictionary<GroupViewModel, Rect> afterGroups,
            string description)
        {
            if (_history.IsReplaying) return;

            bool nodeChanged = beforeNodes.Any(pair => afterNodes.TryGetValue(pair.Key, out Rect after) && !RectEquals(pair.Value, after));
            bool groupChanged = beforeGroups.Any(pair => afterGroups.TryGetValue(pair.Key, out Rect after) && !RectEquals(pair.Value, after));
            if (!nodeChanged && !groupChanged) return;

            void Apply(
                IReadOnlyDictionary<NodeViewModel, Rect> nodeRects,
                IReadOnlyDictionary<GroupViewModel, Rect> groupRects)
            {
                using IDisposable layoutUpdate = sheet.BeginLayoutUpdate();
                foreach (var pair in nodeRects) ApplyNodeRect(pair.Key, pair.Value);
                foreach (var pair in groupRects) ApplyGroupRect(pair.Key, pair.Value);
                sheet.UpdateGroupLayering();
                _markModified();
            }

            _history.RecordExecuted(new DelegateHistoryCommand(
                description,
                affectsDocker: false,
                undo: () =>
                {
                    Apply(beforeNodes, beforeGroups);
                    return Task.CompletedTask;
                },
                redo: () =>
                {
                    Apply(afterNodes, afterGroups);
                    return Task.CompletedTask;
                }));
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
            var diagramNodes = new List<NodeViewModel> { node };
            diagramNodes.AddRange(DiagramDeletionSupport.FindExclusiveAutoGeneratedDependents(sheet, node));
            var diagramNodeSet = diagramNodes.ToHashSet();
            var relatedConnectors = sheet.Connectors.Where(connector =>
                    (connector.Source is NodeViewModel source && diagramNodeSet.Contains(source)) ||
                    (connector.Target is NodeViewModel target && diagramNodeSet.Contains(target)))
                .ToList();
            var containingGroups = diagramNodes.ToDictionary(
                diagramNode => diagramNode,
                diagramNode => sheet.Groups.Where(group => group.ContainedNodes.Contains(diagramNode)).ToList());
            bool affectsDocker = deleteDocker && IsDockerEngineOwnedNode(node);
            IDockerService targetService = sheet.DockerService;

            return new DelegateHistoryCommand(
                affectsDocker ? $"Delete Docker {node.Type}: {node.Name}" : $"Delete diagram node: {node.Name}",
                affectsDocker,
                undo: async () =>
                {
                    if (affectsDocker) await RecreateDockerObjectsAsync(sheet, targetService, new[] { node }, Array.Empty<GroupViewModel>());
                    await RestoreNodesDiagramAsync(sheet, diagramNodes, relatedConnectors, containingGroups);
                    _markModified();
                },
                redo: async () =>
                {
                    if (affectsDocker)
                    {
                        await DeleteDockerObjectsAsync(
                            sheet,
                            targetService,
                            new[] { node },
                            Array.Empty<GroupViewModel>(),
                            forceVolumeDelete);
                    }

                    await RemoveNodesFromDiagramOnlyAsync(sheet, diagramNodes, relatedConnectors, containingGroups);
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
            bool affectsDocker = deleteDocker && IsDockerEngineOwnedNetwork(group);
            IDockerService targetService = sheet.DockerService;

            return new DelegateHistoryCommand(
                affectsDocker ? $"Delete Docker network: {group.Title}" : $"Delete diagram group: {group.Title}",
                affectsDocker,
                undo: async () =>
                {
                    if (affectsDocker) await RecreateDockerObjectsAsync(sheet, targetService, Array.Empty<NodeViewModel>(), new[] { group });
                    await RestoreGroupDiagramAsync(sheet, group, relatedConnectors, containedNodes);
                    _markModified();
                },
                redo: async () =>
                {
                    if (affectsDocker) await DeleteDockerObjectsAsync(sheet, targetService, Array.Empty<NodeViewModel>(), new[] { group });
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

            return result == DialogChoice.Yes ? (true, true) : (false, false);
        }

        private async Task DeleteDockerObjectsAsync(
            SheetViewModel sheet,
            IDockerService targetService,
            IEnumerable<NodeViewModel> nodes,
            IEnumerable<GroupViewModel> groups,
            bool forceVolumeDelete = false)
        {
            foreach (var node in nodes)
            {
                if (node.Type == NodeType.Container && !string.IsNullOrWhiteSpace(node.ContainerId))
                {
                    ContainerInspectResponse inspect = await targetService.InspectContainerAsync(node.ContainerId);
                    _containerUndoSnapshots[node.Id] = new ContainerUndoSnapshot(
                        inspect,
                        inspect.State?.Running == true);
                    await targetService.RemoveContainerAsync(node.ContainerId);
                    node.IsDockerConnected = false;
                }
                else if (node.Type == NodeType.Volume)
                {
                    if (node.VolumeExternal) continue;

                    var decision = forceVolumeDelete
                        ? (ShouldDelete: true, Force: true)
                        : await ConfirmVolumeDockerDeleteAsync(
                            targetService,
                            node.EffectiveVolumeName,
                            allowForceAttempt: false);
                    if (!decision.ShouldDelete)
                        throw new InvalidOperationException($"볼륨 '{node.EffectiveVolumeName}'이 사용 중이어서 삭제하지 않았습니다.");

                    if (_history.IncludeVolumeBackupForUndo)
                        await BackupVolumeForUndoAsync(targetService, node);

                    await targetService.RemoveVolumeAsync(node.EffectiveVolumeName, decision.Force);
                    node.IsDockerConnected = false;
                }
            }

            foreach (var group in groups.Where(g => g.Type == GroupType.Network))
            {
                if (group.External) continue;

                await targetService.RemoveNetworkAsync(
                    !string.IsNullOrWhiteSpace(group.Id) ? group.Id : group.DockerNetworkName);
                group.IsDockerConnected = false;
            }

            if (ReferenceEquals(_getActiveSheet(), sheet))
                await _requestSync();
        }

        private static bool IsDockerEngineOwnedNode(NodeViewModel node)
        {
            if (node.IsDraft || node.Type == NodeType.Internet)
                return false;
            if (node.ResourceKind is RuntimeResourceKind.SwarmService or
                RuntimeResourceKind.SwarmVolume or RuntimeResourceKind.SwarmExternalTraffic or
                RuntimeResourceKind.SwarmSecret or RuntimeResourceKind.SwarmConfig)
                return false;
            if (node.Type == NodeType.Volume && node.VolumeExternal)
                return false;
            return node.ResourceKind is RuntimeResourceKind.Unspecified or
                RuntimeResourceKind.DockerContainer or RuntimeResourceKind.DockerVolume;
        }

        private static bool IsDockerEngineOwnedNetwork(GroupViewModel group) =>
            !group.IsDraft &&
            group.Type == GroupType.Network &&
            !group.External &&
            group.ResourceKind is RuntimeResourceKind.Unspecified or RuntimeResourceKind.DockerNetwork;

        private async Task RecreateDockerObjectsAsync(
            SheetViewModel sheet,
            IDockerService targetService,
            IEnumerable<NodeViewModel> nodes,
            IEnumerable<GroupViewModel> groups)
        {
            foreach (var group in groups.Where(g => g.Type == GroupType.Network))
            {
                try
                {
                    if (group.External)
                    {
                        var networks = await targetService.GetNetworksAsync();
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
                        group.Id = await targetService.CreateNetworkAsync(group.ToNetworkCreateOptions());
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
                            await targetService.InspectVolumeAsync(node.EffectiveVolumeName);
                        }
                        else
                        {
                            await targetService.CreateVolumeAsync(new VolumeCreateOptions
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
                        await RestoreVolumeFromUndoBackupAsync(targetService, node);
                    }
                    catch (Exception ex)
                    {
                        if (!ex.Message.Contains("already exists") && !ex.Message.Contains("409")) throw;
                        node.IsDockerConnected = true;
                        await RestoreVolumeFromUndoBackupAsync(targetService, node);
                    }
                }
                else if (node.Type == NodeType.Container)
                {
                    if (!_containerUndoSnapshots.TryGetValue(node.Id, out ContainerUndoSnapshot? snapshot))
                        throw new InvalidOperationException($"'{node.Name}' 컨테이너의 Undo 스냅샷이 없습니다.");

                    string newId = await targetService.RecreateContainerFromInspectAsync(
                        node.Name,
                        snapshot.Inspect,
                        snapshot.Inspect.HostConfig?.Binds?.ToList() ?? new List<string>(),
                        snapshot.WasRunning);

                    node.ContainerId = newId;
                    node.IsDockerConnected = true;
                    await node.RefreshDetailsAsync();
                }
            }

            if (ReferenceEquals(_getActiveSheet(), sheet))
                await _requestSync();
        }

        private async Task BackupVolumeForUndoAsync(IVolumeService volumeService, NodeViewModel node)
        {
            if (node.Type != NodeType.Volume || node.VolumeExternal) return;

            if (_volumeUndoBackups.TryGetValue(node.Id, out var previousPath))
            {
                VolumeUndoBackupStore.DeleteFile(previousPath);
            }

            string backupPath = VolumeUndoBackupStore.CreateBackupPath(node.EffectiveVolumeName);
            await volumeService.BackupVolumeAsync(node.EffectiveVolumeName, backupPath);
            _volumeUndoBackups[node.Id] = backupPath;
        }

        private async Task RestoreVolumeFromUndoBackupAsync(IVolumeService volumeService, NodeViewModel node)
        {
            if (node.Type != NodeType.Volume || node.VolumeExternal) return;
            if (!_volumeUndoBackups.TryGetValue(node.Id, out var backupPath)) return;
            if (!File.Exists(backupPath)) return;

            await volumeService.RestoreVolumeAsync(node.EffectiveVolumeName, backupPath);
            node.IsDockerConnected = true;
            await node.RefreshDetailsAsync();
        }

        private static async Task RefreshGroupContainmentForNodeAsync(SheetViewModel sheet, NodeViewModel node)
        {
            var targetGroups = sheet.FindGroupsAt(node.X, node.Y, node.Width, node.Height);
            foreach (var group in sheet.Groups)
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
            DiagramDeletionSupport.RemoveGroupFromDiagram(sheet, group, connectors);
            return Task.CompletedTask;
        }

        private static async Task RemoveNodesFromDiagramOnlyAsync(
            SheetViewModel sheet,
            IReadOnlyCollection<NodeViewModel> nodes,
            IReadOnlyCollection<ConnectorViewModel> connectors,
            IReadOnlyDictionary<NodeViewModel, List<GroupViewModel>> containingGroups)
        {
            foreach (ConnectorViewModel connector in connectors)
                sheet.Connectors.Remove(connector);

            foreach (NodeViewModel node in nodes)
            {
                foreach (GroupViewModel group in containingGroups[node])
                    await group.RemoveNodeAsync(node, isRestoring: true);

                sheet.Nodes.Remove(node);
            }
        }

        private static async Task RestoreNodesDiagramAsync(
            SheetViewModel sheet,
            IReadOnlyCollection<NodeViewModel> nodes,
            IReadOnlyCollection<ConnectorViewModel> connectors,
            IReadOnlyDictionary<NodeViewModel, List<GroupViewModel>> containingGroups)
        {
            foreach (NodeViewModel node in nodes)
            {
                if (!sheet.Nodes.Contains(node)) sheet.Nodes.Add(node);
            }

            foreach (NodeViewModel node in nodes)
            {
                foreach (GroupViewModel group in containingGroups[node])
                {
                    if (sheet.Groups.Contains(group))
                        await group.AddNodeAsync(node, isRestoring: true);
                }
            }

            foreach (ConnectorViewModel connector in connectors)
            {
                if (!sheet.Connectors.Contains(connector)) sheet.Connectors.Add(connector);
            }
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

            if (group.ResourceKind == RuntimeResourceKind.SwarmOverlayNetwork)
            {
                foreach (NodeViewModel service in containedNodes.Where(node =>
                             node.ResourceKind == RuntimeResourceKind.SwarmService))
                {
                    service.IsSwarmDiagramDirty = true;
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
