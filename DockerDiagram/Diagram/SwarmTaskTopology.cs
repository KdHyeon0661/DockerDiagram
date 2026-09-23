using DockerDiagram.Models;

namespace DockerDiagram.Diagram
{
    /// <summary>
    /// Service task 목록을 실제 Swarm node별 배치 구조로 투영합니다.
    /// 런타임 조회와 UI 표현 사이의 정책을 분리해 테스트 가능한 상태로 유지합니다.
    /// </summary>
    public static class SwarmTaskTopology
    {
        private const string UnassignedKey = "__unassigned__";

        public static IReadOnlyList<SwarmTaskPlacement> Build(
            IEnumerable<DockerSwarmTask> tasks,
            IEnumerable<DockerSwarmNode> nodes)
        {
            Dictionary<string, DockerSwarmNode> nodesById = nodes
                .Where(node => !string.IsNullOrWhiteSpace(node.Id))
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            return tasks
                .GroupBy(
                    task => string.IsNullOrWhiteSpace(task.NodeId) ? UnassignedKey : task.NodeId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => CreatePlacement(group.Key, group, nodesById))
                .OrderBy(placement => placement.IsAssigned ? 0 : 1)
                .ThenBy(placement => placement.NodeName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static SwarmTaskPlacement CreatePlacement(
            string nodeKey,
            IEnumerable<DockerSwarmTask> groupedTasks,
            IReadOnlyDictionary<string, DockerSwarmNode> nodesById)
        {
            DockerSwarmTask[] orderedTasks = groupedTasks
                .OrderBy(task => task.Slot)
                .ThenBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            bool isAssigned = nodeKey != UnassignedKey;
            nodesById.TryGetValue(nodeKey, out DockerSwarmNode? node);
            string taskNodeName = orderedTasks
                .Select(task => task.NodeName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && name != "-")
                ?? string.Empty;

            return new SwarmTaskPlacement
            {
                NodeId = isAssigned ? nodeKey : string.Empty,
                NodeName = !string.IsNullOrWhiteSpace(node?.Hostname)
                    ? node.Hostname
                    : !string.IsNullOrWhiteSpace(taskNodeName) ? taskNodeName : "Unassigned",
                NodeRole = node?.RoleLabel ?? (isAssigned ? "node" : "unassigned"),
                NodeStatus = node?.Status ?? (isAssigned ? "unknown" : "pending"),
                IsAssigned = isAssigned,
                Tasks = orderedTasks
            };
        }
    }
}
