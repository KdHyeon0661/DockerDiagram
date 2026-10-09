using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.ApplicationServices
{
    public sealed class SwarmStackSheetSynchronizer
    {
        public async Task<SwarmStackRuntimeSnapshot> SynchronizeAsync(
            SheetViewModel sheet,
            SwarmStackSheetState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sheet);
            ArgumentNullException.ThrowIfNull(state);
            if (sheet.RuntimeKind != RuntimeKind.DockerSwarm || sheet.DockerService is not ISwarmService swarmService)
                throw new InvalidOperationException("활성 Swarm Manager 시트가 아닙니다.");

            IReadOnlyList<DockerContainer> allServices = await swarmService
                .GetSwarmServicesAsync()
                .WaitAsync(cancellationToken);
            IReadOnlyList<DockerContainer> stackServices = SwarmStackServiceSelector.Select(allServices, state.StackName);
            ReconcileNodes(sheet, state.StackName, stackServices);

            ulong desired = stackServices.Aggregate(0UL, (sum, service) => sum + service.SwarmDesiredReplicas);
            ulong running = stackServices.Aggregate(0UL, (sum, service) => sum + service.SwarmRunningReplicas);
            var snapshot = new SwarmStackRuntimeSnapshot(state.StackName, stackServices, desired, running);
            state.RuntimeSummary = snapshot.Summary;
            if (stackServices.Count == 0 && state.DeployState == SwarmStackDeployState.Deployed)
                state.DeployState = SwarmStackDeployState.Missing;
            else if (stackServices.Count > 0 && state.DeployState is not SwarmStackDeployState.Deploying)
                state.DeployState = SwarmStackDeployState.Deployed;
            return snapshot;
        }

        public async Task<SwarmStackRuntimeSnapshot> WaitForDeploymentAsync(
            SheetViewModel sheet,
            SwarmStackSheetState state,
            CancellationToken cancellationToken = default)
        {
            SwarmStackRuntimeSnapshot snapshot = new(state.StackName, Array.Empty<DockerContainer>(), 0, 0);
            for (int attempt = 0; attempt < 9; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot = await SynchronizeAsync(sheet, state, cancellationToken);
                if (snapshot.Services.Count > 0) return snapshot;
                if (attempt < 8) await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            }
            return snapshot;
        }

        public static void MarkRemoved(SheetViewModel sheet, SwarmStackSheetState state)
        {
            foreach (NodeViewModel node in StackNodes(sheet, state.StackName))
            {
                node.BindingState = RuntimeBindingState.Missing;
                node.IsDockerConnected = false;
                node.IsRunning = false;
                node.StatusColor = "#808080";
                node.DetailStatus = "Stack removed";
            }
            state.DeployState = SwarmStackDeployState.Missing;
            state.RuntimeSummary = "Stack removed";
        }

        private static void ReconcileNodes(
            SheetViewModel sheet,
            string stackName,
            IReadOnlyList<DockerContainer> services)
        {
            List<NodeViewModel> existing = StackNodes(sheet, stackName).ToList();
            var matched = new HashSet<NodeViewModel>();

            for (int index = 0; index < services.Count; index++)
            {
                DockerContainer service = services[index];
                NodeViewModel? node = existing.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.ContainerId) &&
                    candidate.ContainerId.Equals(service.Id, StringComparison.OrdinalIgnoreCase));
                if (node == null)
                {
                    List<NodeViewModel> nameMatches = existing
                        .Where(candidate => candidate.Name.Equals(service.Name, StringComparison.OrdinalIgnoreCase))
                        .Take(2)
                        .ToList();
                    node = nameMatches.Count == 1 ? nameMatches[0] : null;
                }

                if (node == null)
                {
                    double x = 140 + (index % 4) * 210;
                    double y = 120 + (index / 4) * 130;
                    sheet.CreateNodeAt(service, x, y);
                    node = sheet.Nodes[^1];
                    node.RuntimeKind = RuntimeKind.DockerSwarm;
                    node.ResourceKind = RuntimeResourceKind.SwarmService;
                }

                ApplyService(node, service, stackName);
                matched.Add(node);
            }

            foreach (NodeViewModel stale in existing.Where(node => !matched.Contains(node)))
            {
                stale.BindingState = RuntimeBindingState.Missing;
                stale.IsDockerConnected = false;
                stale.IsRunning = false;
                stale.StatusColor = "#808080";
                stale.DetailStatus = "Service missing from stack namespace";
            }
        }

        private static IEnumerable<NodeViewModel> StackNodes(SheetViewModel sheet, string stackName) =>
            sheet.Nodes.Where(node =>
                node.IsSwarmService &&
                string.Equals(node.ComposeProjectName, stackName, StringComparison.OrdinalIgnoreCase));

        private static void ApplyService(NodeViewModel node, DockerContainer service, string stackName)
        {
            node.Name = service.Name;
            node.ContainerId = service.Id;
            node.ImageName = service.Image;
            node.ComposeProjectName = stackName;
            node.ComposeServiceName = service.ComposeServiceName;
            node.IsSwarmService = true;
            node.RuntimeKind = RuntimeKind.DockerSwarm;
            node.ResourceKind = RuntimeResourceKind.SwarmService;
            node.BindingState = RuntimeBindingState.Bound;
            node.IsDockerConnected = true;
            node.IsRunning = service.State.Equals("running", StringComparison.OrdinalIgnoreCase);
            node.StatusColor = service.StateColor;
            node.DetailStatus = service.Ports;
            node.PortInfo = service.Ports;
            node.SwarmMode = service.SwarmMode;
            node.SwarmDesiredReplicas = service.SwarmDesiredReplicas;
            node.SwarmRunningReplicas = service.SwarmRunningReplicas;
            node.TargetSwarmReplicas = service.SwarmDesiredReplicas;
        }
    }
}
