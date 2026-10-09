using DockerDiagram.Diagram;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Collections.ObjectModel;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// Swarm 동기화의 부분 실패를 리소스 단위로 격리하고, 재연결 시 저장된 요소를 새 엔진 ID에 재결합합니다.
    /// 일반 Docker/Kubernetes 시트에는 관여하지 않습니다.
    /// </summary>
    public sealed class SwarmRuntimeRecoveryService
    {
        private readonly ResourceExplorerViewModel _explorer;

        public SwarmRuntimeRecoveryService(ResourceExplorerViewModel explorer)
        {
            _explorer = explorer ?? throw new ArgumentNullException(nameof(explorer));
        }

        public SwarmSyncCheckpoint? Capture(SheetViewModel? sheet)
        {
            if (sheet?.RuntimeKind != RuntimeKind.DockerSwarm) return null;

            return new SwarmSyncCheckpoint(
                sheet,
                _explorer.ExistingContainers.ToList(),
                _explorer.ExistingVolumes.ToList(),
                _explorer.ExistingNetworks.ToList(),
                _explorer.SwarmNodes.ToList(),
                _explorer.SwarmSecrets.ToList(),
                _explorer.SwarmConfigs.ToList(),
                sheet.Nodes
                    .Where(IsSwarmRuntimeNode)
                    .Select(NodeRuntimeSnapshot.Capture)
                    .ToList(),
                sheet.Groups
                    .Where(group => group.ResourceKind == RuntimeResourceKind.SwarmOverlayNetwork)
                    .Select(GroupRuntimeSnapshot.Capture)
                    .ToList());
        }

        public Task ReconcileAsync(
            SwarmSyncCheckpoint? checkpoint,
            SheetViewModel? activeSheet)
        {
            if (checkpoint == null ||
                !ReferenceEquals(checkpoint.Sheet, activeSheet) ||
                activeSheet?.RuntimeKind != RuntimeKind.DockerSwarm)
            {
                return Task.CompletedTask;
            }

            List<string> failures = SwarmPartialSyncState.Parse(_explorer.LastSyncTime).ToList();
            RestoreFailedSnapshots(checkpoint, failures);

            if (activeSheet.IsRuntimeUnavailable)
                return Task.CompletedTask;

            bool servicesAvailable = !Contains(failures, "Services");
            bool networksAvailable = !Contains(failures, "Overlay Networks");

            if (servicesAvailable && activeSheet.Nodes.Any(IsSwarmServiceNode))
            {
                ReconcileServices(activeSheet, _explorer.GetSwarmServicesSnapshot());
            }

            if (networksAvailable && activeSheet.Groups.Any(IsSwarmOverlayGroup))
            {
                ReconcileNetworks(activeSheet, _explorer.GetSwarmNetworksSnapshot());
            }

            ReconcileDataResources(activeSheet, SwarmDataResourceKind.Secret, failures);
            ReconcileDataResources(activeSheet, SwarmDataResourceKind.Config, failures);

            if (failures.Count > 0)
            {
                _explorer.LastSyncTime = SwarmPartialSyncState.Format(failures);
                activeSheet.RuntimeStatusMessage =
                    $"Swarm partial sync. Preserved snapshot: {string.Join(", ", failures.Distinct(StringComparer.OrdinalIgnoreCase))}.";
            }

            return Task.CompletedTask;
        }

        private void RestoreFailedSnapshots(SwarmSyncCheckpoint checkpoint, IReadOnlyCollection<string> failures)
        {
            if (Contains(failures, "Services"))
            {
                Replace(_explorer.ExistingContainers, checkpoint.Containers);
                foreach (NodeRuntimeSnapshot state in checkpoint.NodeStates.Where(state => IsSwarmServiceNode(state.Node)))
                    state.Restore();
            }

            if (Contains(failures, "Nodes"))
                Replace(_explorer.SwarmNodes, checkpoint.Nodes);

            if (Contains(failures, "Overlay Networks"))
            {
                Replace(_explorer.ExistingNetworks, checkpoint.Networks);
                foreach (GroupRuntimeSnapshot state in checkpoint.GroupStates)
                    state.Restore();
            }

            if (Contains(failures, "Manager Host Volumes"))
            {
                Replace(_explorer.ExistingVolumes, checkpoint.Volumes);
                foreach (NodeRuntimeSnapshot state in checkpoint.NodeStates.Where(
                             state => state.Node.ResourceKind == RuntimeResourceKind.SwarmVolume))
                    state.Restore();
            }

            if (Contains(failures, "Secrets") || Contains(failures, "Secrets/Configs"))
            {
                Replace(_explorer.SwarmSecrets, checkpoint.Secrets);
                foreach (NodeRuntimeSnapshot state in checkpoint.NodeStates.Where(
                             state => state.Node.ResourceKind == RuntimeResourceKind.SwarmSecret))
                    state.Restore();
            }

            if (Contains(failures, "Configs") || Contains(failures, "Secrets/Configs"))
            {
                Replace(_explorer.SwarmConfigs, checkpoint.Configs);
                foreach (NodeRuntimeSnapshot state in checkpoint.NodeStates.Where(
                             state => state.Node.ResourceKind == RuntimeResourceKind.SwarmConfig))
                    state.Restore();
            }
        }

        private static void ReconcileServices(
            SheetViewModel sheet,
            IReadOnlyList<DockerContainer> services)
        {
            foreach (NodeViewModel node in sheet.Nodes.Where(IsSwarmServiceNode))
            {
                if (node.BindingState is RuntimeBindingState.Draft or RuntimeBindingState.Applying or RuntimeBindingState.Error)
                    continue;

                SwarmIdentityMatch<DockerContainer> match = SwarmRuntimeIdentityMatcher.Match(
                    services,
                    node.ContainerId,
                    node.Name,
                    service => service.Id,
                    service => service.Name);

                if (match.Resource == null)
                {
                    MarkMissing(node, "Service missing from manager snapshot");
                    continue;
                }

                DockerContainer service = match.Resource;
                node.ContainerId = service.Id;
                node.BindingState = RuntimeBindingState.Bound;
                node.IsDockerConnected = true;
                node.IsRunning = service.State.Equals("running", StringComparison.OrdinalIgnoreCase);
                node.StatusColor = service.StateColor;
                node.DetailStatus = service.Ports;
                node.SwarmMode = service.SwarmMode;
                node.SwarmDesiredReplicas = service.SwarmDesiredReplicas;
                node.SwarmRunningReplicas = service.SwarmRunningReplicas;
                node.TargetSwarmReplicas = service.SwarmDesiredReplicas;
            }
        }

        private static void ReconcileNetworks(
            SheetViewModel sheet,
            IReadOnlyList<DockerNetworkGroup> networks)
        {
            foreach (GroupViewModel group in sheet.Groups.Where(IsSwarmOverlayGroup))
            {
                if (group.BindingState is RuntimeBindingState.Draft or RuntimeBindingState.Applying or RuntimeBindingState.Error)
                    continue;

                SwarmIdentityMatch<DockerNetworkGroup> match = SwarmRuntimeIdentityMatcher.Match(
                    networks,
                    group.Id,
                    group.DockerNetworkName,
                    network => network.Id,
                    network => network.Name);

                if (match.Resource == null)
                {
                    group.BindingState = RuntimeBindingState.Missing;
                    group.IsDockerConnected = false;
                    continue;
                }

                group.Id = match.Resource.Id;
                group.BindingState = RuntimeBindingState.Bound;
                group.IsDockerConnected = true;
                group.Driver = match.Resource.Driver;
            }
        }

        private void ReconcileDataResources(
            SheetViewModel sheet,
            SwarmDataResourceKind kind,
            ICollection<string> failures)
        {
            RuntimeResourceKind resourceKind = kind == SwarmDataResourceKind.Secret
                ? RuntimeResourceKind.SwarmSecret
                : RuntimeResourceKind.SwarmConfig;
            List<NodeViewModel> nodes = sheet.Nodes
                .Where(node => node.ResourceKind == resourceKind)
                .ToList();
            if (nodes.Count == 0) return;

            string failureKey = kind == SwarmDataResourceKind.Secret ? "Secrets" : "Configs";
            if (Contains(failures, failureKey) || Contains(failures, "Secrets/Configs")) return;
            IReadOnlyList<SwarmDataResourceSnapshot> resources = _explorer.GetSwarmDataResources(kind);

            foreach (NodeViewModel node in nodes)
            {
                if (node.BindingState is RuntimeBindingState.Draft or RuntimeBindingState.Applying or RuntimeBindingState.Error)
                    continue;

                SwarmIdentityMatch<SwarmDataResourceSnapshot> match = SwarmRuntimeIdentityMatcher.Match(
                    resources,
                    node.ContainerId,
                    node.Name,
                    resource => resource.Id,
                    resource => resource.Name);

                if (match.Resource == null)
                {
                    MarkMissing(node, $"Swarm {kind} missing from manager snapshot");
                    continue;
                }

                node.ContainerId = match.Resource.Id;
                node.BindingState = RuntimeBindingState.Bound;
                node.IsDockerConnected = true;
                node.StatusColor = kind == SwarmDataResourceKind.Secret ? "#C08A00" : "#4D7C6F";
                node.DetailStatus = $"Bound · {ShortId(match.Resource.Id)} · v{match.Resource.Version}";
            }
        }

        private static void MarkMissing(NodeViewModel node, string detail)
        {
            node.BindingState = RuntimeBindingState.Missing;
            node.IsDockerConnected = false;
            node.IsRunning = false;
            node.StatusColor = "#808080";
            node.DetailStatus = detail;
        }

        private static bool IsSwarmRuntimeNode(NodeViewModel node) =>
            node.RuntimeKind == RuntimeKind.DockerSwarm ||
            node.ResourceKind is RuntimeResourceKind.SwarmService or
                RuntimeResourceKind.SwarmVolume or
                RuntimeResourceKind.SwarmExternalTraffic or
                RuntimeResourceKind.SwarmSecret or
                RuntimeResourceKind.SwarmConfig;

        private static bool IsSwarmServiceNode(NodeViewModel node) =>
            node.IsSwarmService || node.ResourceKind == RuntimeResourceKind.SwarmService;

        private static bool IsSwarmOverlayGroup(GroupViewModel group) =>
            group.ResourceKind == RuntimeResourceKind.SwarmOverlayNetwork;

        private static bool Contains(IEnumerable<string> values, string expected) =>
            values.Contains(expected, StringComparer.OrdinalIgnoreCase);

        private static string ShortId(string id) => id.Length <= 12 ? id : id[..12];

        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
        {
            target.Clear();
            foreach (T item in source) target.Add(item);
        }

        public sealed record SwarmSyncCheckpoint(
            SheetViewModel Sheet,
            IReadOnlyList<DockerContainer> Containers,
            IReadOnlyList<DockerVolume> Volumes,
            IReadOnlyList<DockerNetworkGroup> Networks,
            IReadOnlyList<DockerSwarmNode> Nodes,
            IReadOnlyList<SwarmDataResourceSnapshot> Secrets,
            IReadOnlyList<SwarmDataResourceSnapshot> Configs,
            IReadOnlyList<NodeRuntimeSnapshot> NodeStates,
            IReadOnlyList<GroupRuntimeSnapshot> GroupStates);

        public sealed record NodeRuntimeSnapshot(
            NodeViewModel Node,
            string ContainerId,
            RuntimeBindingState BindingState,
            bool IsConnected,
            bool IsRunning,
            string StatusColor,
            string DetailStatus)
        {
            public static NodeRuntimeSnapshot Capture(NodeViewModel node) =>
                new(node, node.ContainerId, node.BindingState, node.IsDockerConnected,
                    node.IsRunning, node.StatusColor, node.DetailStatus);

            public void Restore()
            {
                Node.ContainerId = ContainerId;
                Node.BindingState = BindingState;
                Node.IsDockerConnected = IsConnected;
                Node.IsRunning = IsRunning;
                Node.StatusColor = StatusColor;
                Node.DetailStatus = DetailStatus;
            }
        }

        public sealed record GroupRuntimeSnapshot(
            GroupViewModel Group,
            string Id,
            RuntimeBindingState BindingState,
            bool IsConnected)
        {
            public static GroupRuntimeSnapshot Capture(GroupViewModel group) =>
                new(group, group.Id, group.BindingState, group.IsDockerConnected);

            public void Restore()
            {
                Group.Id = Id;
                Group.BindingState = BindingState;
                Group.IsDockerConnected = IsConnected;
            }
        }
    }
}
