using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public readonly record struct SwarmManagerQuorumState(
        int ManagerCount,
        int ReachableManagerCount,
        bool TargetManagerReachable);

    public static class SwarmNodeSafetyPolicy
    {
        public static void ValidateUpdate(
            SwarmNodeEditSnapshot current,
            SwarmNodeUpdateOptions desired,
            int managerCount)
        {
            ValidateUpdate(
                current,
                desired,
                new SwarmManagerQuorumState(
                    managerCount,
                    managerCount,
                    current.Role.Equals("manager", StringComparison.OrdinalIgnoreCase)));
        }

        public static void ValidateUpdate(
            SwarmNodeEditSnapshot current,
            SwarmNodeUpdateOptions desired,
            SwarmManagerQuorumState quorum)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(desired);
            desired.Validate();

            bool demotingManager = current.Role.Equals("manager", StringComparison.OrdinalIgnoreCase) &&
                                   desired.Role.Equals("worker", StringComparison.OrdinalIgnoreCase);
            if (!demotingManager) return;

            int remainingManagers = quorum.ManagerCount - 1;
            if (remainingManagers < 1)
            {
                throw new InvalidOperationException(
                    "마지막 Swarm Manager는 Worker로 강등할 수 없습니다. 먼저 다른 Manager를 승격해 주세요.");
            }

            int remainingReachable = quorum.ReachableManagerCount - (quorum.TargetManagerReachable ? 1 : 0);
            int requiredQuorum = remainingManagers / 2 + 1;
            if (remainingReachable < requiredQuorum)
            {
                throw new InvalidOperationException(
                    $"Manager 강등 후 quorum을 유지할 수 없습니다. " +
                    $"남는 Manager {remainingManagers}개 중 reachable {remainingReachable}개이며 {requiredQuorum}개가 필요합니다.");
            }
        }

        public static void ValidateRemoval(SwarmNodeEditSnapshot current, bool force)
        {
            ArgumentNullException.ThrowIfNull(current);
            if (current.IsLocalNode)
                throw new InvalidOperationException(
                    "현재 연결 중인 자기 자신 노드는 Node Remove로 제거할 수 없습니다. Swarm Leave 흐름을 사용해 주세요.");
            if (current.Role.Equals("manager", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Manager 노드는 직접 제거할 수 없습니다. 먼저 Worker로 안전하게 강등해 주세요.");
            if (current.Status.Equals("ready", StringComparison.OrdinalIgnoreCase) && !force)
                throw new InvalidOperationException(
                    "Ready 상태의 Worker는 일반 제거할 수 없습니다. 먼저 Drain 후 노드를 종료하거나 명시적으로 강제 제거해 주세요.");
        }

        public static IReadOnlyList<string> GetUpdateWarnings(
            SwarmNodeEditSnapshot current,
            SwarmNodeUpdateOptions desired,
            int managerCount)
        {
            var warnings = new List<string>();
            bool roleChanged = !current.Role.Equals(desired.Role, StringComparison.OrdinalIgnoreCase);
            if (roleChanged && desired.Role.Equals("manager", StringComparison.OrdinalIgnoreCase))
                warnings.Add("이 Worker를 Manager로 승격하면 Raft 제어 평면 구성원이 늘어납니다.");
            if (roleChanged && desired.Role.Equals("worker", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(current.IsLeader
                    ? "현재 Leader를 강등하면 Swarm이 새 Leader를 선출합니다."
                    : "Manager를 강등하면 Swarm quorum 구성이 변경됩니다.");
                if (managerCount % 2 == 1)
                    warnings.Add($"Manager 수가 {managerCount}개에서 {managerCount - 1}개로 바뀌어 짝수가 됩니다.");
            }
            if (!current.Availability.Equals("drain", StringComparison.OrdinalIgnoreCase) &&
                desired.Availability.Equals("drain", StringComparison.OrdinalIgnoreCase))
                warnings.Add("Drain 전환 시 이 노드의 Swarm service task가 다른 노드로 재배치됩니다.");
            if (current.IsLocalNode && roleChanged)
                warnings.Add("현재 연결 중인 로컬 노드의 역할을 변경합니다.");
            return warnings;
        }
    }
}
