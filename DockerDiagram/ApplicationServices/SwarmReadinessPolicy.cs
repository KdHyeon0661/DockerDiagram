using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static class SwarmReadinessPolicy
    {
        public static IReadOnlyList<SwarmReadinessCheck> Evaluate(
            bool engineReachable,
            DockerCliProbeResult cli,
            SwarmClusterState? cluster,
            IReadOnlyList<DockerSwarmNode>? nodes,
            int? serviceCount,
            string nodeQueryError = "",
            string serviceQueryError = "")
        {
            var checks = new List<SwarmReadinessCheck>
            {
                cli.IsAvailable
                    ? Pass("Tooling", "Docker CLI", $"docker {cli.Version}")
                    : Warning("Tooling", "Docker CLI", $"Stack deploy/remove unavailable. {cli.Error}"),
                engineReachable
                    ? Pass("Connection", "Docker Engine", "Docker API ping succeeded.")
                    : Blocked("Connection", "Docker Engine", "Docker API is not reachable.")
            };

            if (!engineReachable || cluster == null)
                return checks;

            checks.Add(cluster.Membership switch
            {
                SwarmMembershipState.Manager => Pass("Cluster", "Local role", "Connected to a Swarm Manager."),
                SwarmMembershipState.Worker => Warning("Cluster", "Local role", "Connected to a Worker; Manager APIs are unavailable."),
                SwarmMembershipState.Inactive => Blocked("Cluster", "Membership", "Docker Engine is not part of a Swarm."),
                SwarmMembershipState.Locked => Blocked("Cluster", "Membership", "Swarm Manager is locked."),
                SwarmMembershipState.Pending => Warning("Cluster", "Membership", "Swarm membership transition is pending."),
                SwarmMembershipState.Error => Blocked("Cluster", "Membership", cluster.ErrorMessage),
                _ => Blocked("Cluster", "Membership", "Swarm membership could not be determined.")
            });

            checks.Add(string.IsNullOrWhiteSpace(cluster.NodeId)
                ? Warning("Cluster", "Local Node ID", "The local Node ID is empty.")
                : Pass("Cluster", "Local Node ID", ShortId(cluster.NodeId)));

            if (!cluster.IsManager)
                return checks;

            if (nodes == null)
            {
                checks.Add(Blocked("Managers", "Node inventory", string.IsNullOrWhiteSpace(nodeQueryError)
                    ? "Node inventory is unavailable."
                    : nodeQueryError));
            }
            else
            {
                AddNodeChecks(checks, nodes);
            }

            checks.Add(serviceCount.HasValue
                ? Pass("Services", "Service inventory", $"{serviceCount.Value} service(s) visible to this Manager.")
                : Warning("Services", "Service inventory", string.IsNullOrWhiteSpace(serviceQueryError)
                    ? "Service inventory is unavailable."
                    : serviceQueryError));
            return checks;
        }

        private static void AddNodeChecks(
            ICollection<SwarmReadinessCheck> checks,
            IReadOnlyList<DockerSwarmNode> nodes)
        {
            List<DockerSwarmNode> managers = nodes
                .Where(node => node.Role.Equals("manager", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int managerCount = managers.Count;
            checks.Add(managerCount == 0
                ? Blocked("Managers", "Manager count", "No Manager is visible.")
                : managerCount % 2 == 1
                    ? Pass("Managers", "Manager count", $"{managerCount} Manager(s); odd-sized quorum configuration.")
                    : Warning("Managers", "Manager count", $"{managerCount} Managers; an odd count is recommended."));

            int majority = managerCount / 2 + 1;
            int reachable = managers.Count(IsReachableManager);
            checks.Add(reachable >= majority
                ? Pass("Managers", "Quorum reachability", $"{reachable}/{managerCount} Managers reachable; majority {majority}.")
                : Blocked("Managers", "Quorum reachability", $"Only {reachable}/{managerCount} Managers reachable; majority {majority} required."));

            int leaders = managers.Count(node => node.ManagerStatus.Equals("leader", StringComparison.OrdinalIgnoreCase));
            checks.Add(leaders == 1
                ? Pass("Managers", "Leader", "Exactly one leader is visible.")
                : leaders == 0
                    ? Blocked("Managers", "Leader", "No leader is visible.")
                    : Warning("Managers", "Leader", $"{leaders} nodes report leader status; refresh the cluster snapshot."));

            List<string> versions = nodes
                .Select(node => node.EngineVersion)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            checks.Add(versions.Count switch
            {
                0 => Warning("Nodes", "Engine versions", "Engine versions were not reported."),
                1 => Pass("Nodes", "Engine versions", versions[0]),
                _ => Warning("Nodes", "Engine versions", $"Mixed versions: {string.Join(", ", versions)}")
            });

            int unavailable = nodes.Count(node =>
                !node.Status.Equals("ready", StringComparison.OrdinalIgnoreCase) ||
                !node.Availability.Equals("active", StringComparison.OrdinalIgnoreCase));
            checks.Add(nodes.Count == 0
                ? Blocked("Nodes", "Scheduling", "No nodes are visible; scheduling readiness cannot be verified.")
                : unavailable == 0
                    ? Pass("Nodes", "Scheduling", $"All {nodes.Count} node(s) are ready and schedulable.")
                    : Warning("Nodes", "Scheduling", $"{unavailable}/{nodes.Count} node(s) are not ready or are drained."));
        }

        private static bool IsReachableManager(DockerSwarmNode node) =>
            node.Status.Equals("ready", StringComparison.OrdinalIgnoreCase) &&
            (node.ManagerStatus.Equals("leader", StringComparison.OrdinalIgnoreCase) ||
             node.ManagerStatus.Equals("reachable", StringComparison.OrdinalIgnoreCase));

        private static SwarmReadinessCheck Pass(string category, string name, string detail) =>
            new(category, name, SwarmReadinessSeverity.Pass, detail);

        private static SwarmReadinessCheck Warning(string category, string name, string detail) =>
            new(category, name, SwarmReadinessSeverity.Warning, detail);

        private static SwarmReadinessCheck Blocked(string category, string name, string detail) =>
            new(category, name, SwarmReadinessSeverity.Blocked, detail);

        private static string ShortId(string id) => id.Length <= 12 ? id : id[..12];
    }
}



