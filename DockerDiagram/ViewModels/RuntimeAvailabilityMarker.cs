using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public static class RuntimeAvailabilityMarker
    {
        public static void MarkUnavailable(SheetViewModel? sheet, string message)
        {
            if (sheet == null) return;

            sheet.RuntimeStatusMessage = message;
            sheet.IsRuntimeUnavailable = true;

            IEnumerable<NodeViewModel> runtimeNodes = sheet.RuntimeKind == RuntimeKind.DockerSwarm
                ? sheet.Nodes.Where(IsSwarmNode)
                : sheet.Nodes.Where(node => node.IsSwarmService || node.IsKubernetesResource);

            foreach (NodeViewModel node in runtimeNodes)
            {
                node.IsDockerConnected = false;
                node.IsRunning = false;
                node.StatusColor = "#808080";
                node.NotifyRuntimeAvailabilityChanged();
            }

            if (sheet.RuntimeKind != RuntimeKind.DockerSwarm) return;

            foreach (GroupViewModel group in sheet.Groups.Where(IsSwarmGroup))
                group.IsDockerConnected = false;
        }

        private static bool IsSwarmNode(NodeViewModel node) =>
            node.RuntimeKind == RuntimeKind.DockerSwarm ||
            node.ResourceKind is RuntimeResourceKind.SwarmService
                or RuntimeResourceKind.SwarmVolume
                or RuntimeResourceKind.SwarmExternalTraffic
                or RuntimeResourceKind.SwarmSecret
                or RuntimeResourceKind.SwarmConfig;

        private static bool IsSwarmGroup(GroupViewModel group) =>
            group.RuntimeKind == RuntimeKind.DockerSwarm ||
            group.ResourceKind is RuntimeResourceKind.SwarmOverlayNetwork
                or RuntimeResourceKind.SwarmVisualGroup;
    }
}
