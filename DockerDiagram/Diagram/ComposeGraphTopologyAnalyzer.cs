using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Diagram
{
    public enum ComposeLayoutGraphKind
    {
        Empty,
        Tree,
        Forest,
        DirectedAcyclicGraph,
        Cyclic
    }

    public sealed record ComposeLayoutEdge(string SourceId, string TargetId);

    public sealed class ComposeGraphTopology
    {
        internal ComposeGraphTopology(
            ComposeLayoutGraphKind kind,
            IReadOnlyList<string> orderedNodeIds,
            IReadOnlyList<ComposeLayoutEdge> edges,
            IReadOnlyDictionary<string, IReadOnlyList<string>> parentsByNode,
            IReadOnlyDictionary<string, IReadOnlyList<string>> childrenByNode,
            IReadOnlyDictionary<string, int> rankByNode,
            IReadOnlyList<IReadOnlyList<string>> ranks,
            IReadOnlyList<IReadOnlyList<string>> connectedComponents,
            IReadOnlyList<IReadOnlyList<string>> stronglyConnectedComponents,
            IReadOnlyDictionary<string, int> connectedComponentByNode,
            IReadOnlyDictionary<string, int> stronglyConnectedComponentByNode,
            IReadOnlySet<string> cycleNodeIds)
        {
            Kind = kind;
            OrderedNodeIds = orderedNodeIds;
            Edges = edges;
            ParentsByNode = parentsByNode;
            ChildrenByNode = childrenByNode;
            RankByNode = rankByNode;
            Ranks = ranks;
            ConnectedComponents = connectedComponents;
            StronglyConnectedComponents = stronglyConnectedComponents;
            ConnectedComponentByNode = connectedComponentByNode;
            StronglyConnectedComponentByNode = stronglyConnectedComponentByNode;
            CycleNodeIds = cycleNodeIds;
        }

        public ComposeLayoutGraphKind Kind { get; }
        public IReadOnlyList<string> OrderedNodeIds { get; }
        public IReadOnlyList<ComposeLayoutEdge> Edges { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> ParentsByNode { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> ChildrenByNode { get; }
        public IReadOnlyDictionary<string, int> RankByNode { get; }
        public IReadOnlyList<IReadOnlyList<string>> Ranks { get; }
        public IReadOnlyList<IReadOnlyList<string>> ConnectedComponents { get; }
        public IReadOnlyList<IReadOnlyList<string>> StronglyConnectedComponents { get; }
        public IReadOnlyDictionary<string, int> ConnectedComponentByNode { get; }
        public IReadOnlyDictionary<string, int> StronglyConnectedComponentByNode { get; }
        public IReadOnlySet<string> CycleNodeIds { get; }
        public IReadOnlyList<string> RootIds =>
            OrderedNodeIds.Where(id => ParentsByNode[id].Count == 0).ToArray();
        public IReadOnlyList<string> LeafIds =>
            OrderedNodeIds.Where(id => ChildrenByNode[id].Count == 0).ToArray();
        public bool HasCycles => CycleNodeIds.Count > 0;
    }

    /// <summary>
    /// UI 객체와 무관한 Compose 서비스 그래프 분석기입니다.
    /// depends_on 간선은 dependency(Source) -> dependent(Target) 방향을 사용합니다.
    /// </summary>
    public static class ComposeGraphTopologyAnalyzer
    {
        public static ComposeGraphTopology Analyze(
            IEnumerable<string> nodeIds,
            IEnumerable<ComposeLayoutEdge> inputEdges)
        {
            ArgumentNullException.ThrowIfNull(nodeIds);
            ArgumentNullException.ThrowIfNull(inputEdges);

            var orderedNodeIds = nodeIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var nodeSet = new HashSet<string>(orderedNodeIds, StringComparer.OrdinalIgnoreCase);
            var orderIndex = orderedNodeIds
                .Select((id, index) => (id, index))
                .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);

            var edges = inputEdges
                .Where(edge =>
                    !string.IsNullOrWhiteSpace(edge.SourceId) &&
                    !string.IsNullOrWhiteSpace(edge.TargetId) &&
                    nodeSet.Contains(edge.SourceId) &&
                    nodeSet.Contains(edge.TargetId))
                .Distinct(ComposeLayoutEdgeComparer.Instance)
                .ToList();

            var parents = orderedNodeIds.ToDictionary(
                id => id,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var children = orderedNodeIds.ToDictionary(
                id => id,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (ComposeLayoutEdge edge in edges)
            {
                parents[edge.TargetId].Add(edge.SourceId);
                children[edge.SourceId].Add(edge.TargetId);
            }

            foreach (List<string> values in parents.Values)
                values.Sort((left, right) => orderIndex[left].CompareTo(orderIndex[right]));
            foreach (List<string> values in children.Values)
                values.Sort((left, right) => orderIndex[left].CompareTo(orderIndex[right]));

            var connectedComponents = FindConnectedComponents(orderedNodeIds, parents, children, orderIndex);
            var stronglyConnectedComponents = FindStronglyConnectedComponents(orderedNodeIds, children, orderIndex);
            var stronglyConnectedComponentByNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int componentIndex = 0; componentIndex < stronglyConnectedComponents.Count; componentIndex++)
            {
                foreach (string nodeId in stronglyConnectedComponents[componentIndex])
                    stronglyConnectedComponentByNode[nodeId] = componentIndex;
            }

            var cycleNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IReadOnlyList<string> component in stronglyConnectedComponents)
            {
                if (component.Count > 1)
                {
                    cycleNodeIds.UnionWith(component);
                    continue;
                }

                string nodeId = component[0];
                if (edges.Any(edge =>
                        string.Equals(edge.SourceId, nodeId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(edge.TargetId, nodeId, StringComparison.OrdinalIgnoreCase)))
                {
                    cycleNodeIds.Add(nodeId);
                }
            }

            var rankByNode = CalculateRanks(
                orderedNodeIds,
                edges,
                stronglyConnectedComponents,
                stronglyConnectedComponentByNode,
                orderIndex);
            var ranks = rankByNode
                .GroupBy(pair => pair.Value)
                .OrderBy(group => group.Key)
                .Select(group => (IReadOnlyList<string>)group
                    .Select(pair => pair.Key)
                    .OrderBy(id => orderIndex[id])
                    .ToArray())
                .ToArray();

            var connectedComponentByNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int componentIndex = 0; componentIndex < connectedComponents.Count; componentIndex++)
            {
                foreach (string nodeId in connectedComponents[componentIndex])
                    connectedComponentByNode[nodeId] = componentIndex;
            }

            ComposeLayoutGraphKind kind = Classify(
                orderedNodeIds.Count,
                connectedComponents.Count,
                parents,
                cycleNodeIds);

            return new ComposeGraphTopology(
                kind,
                orderedNodeIds.ToArray(),
                edges.ToArray(),
                parents.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                children.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(rankByNode, StringComparer.OrdinalIgnoreCase),
                ranks,
                connectedComponents,
                stronglyConnectedComponents,
                connectedComponentByNode,
                stronglyConnectedComponentByNode,
                cycleNodeIds);
        }

        private static ComposeLayoutGraphKind Classify(
            int nodeCount,
            int connectedComponentCount,
            IReadOnlyDictionary<string, List<string>> parents,
            IReadOnlySet<string> cycleNodeIds)
        {
            if (nodeCount == 0) return ComposeLayoutGraphKind.Empty;
            if (cycleNodeIds.Count > 0) return ComposeLayoutGraphKind.Cyclic;

            bool isForest = parents.Values.All(nodeParents => nodeParents.Count <= 1);
            if (!isForest) return ComposeLayoutGraphKind.DirectedAcyclicGraph;
            return connectedComponentCount == 1
                ? ComposeLayoutGraphKind.Tree
                : ComposeLayoutGraphKind.Forest;
        }

        private static IReadOnlyList<IReadOnlyList<string>> FindConnectedComponents(
            IReadOnlyList<string> orderedNodeIds,
            IReadOnlyDictionary<string, List<string>> parents,
            IReadOnlyDictionary<string, List<string>> children,
            IReadOnlyDictionary<string, int> orderIndex)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var components = new List<IReadOnlyList<string>>();

            foreach (string start in orderedNodeIds)
            {
                if (!visited.Add(start)) continue;

                var queue = new Queue<string>();
                var component = new List<string>();
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    string current = queue.Dequeue();
                    component.Add(current);

                    foreach (string adjacent in parents[current].Concat(children[current]))
                    {
                        if (visited.Add(adjacent)) queue.Enqueue(adjacent);
                    }
                }

                component.Sort((left, right) => orderIndex[left].CompareTo(orderIndex[right]));
                components.Add(component.ToArray());
            }

            return components;
        }

        private static IReadOnlyList<IReadOnlyList<string>> FindStronglyConnectedComponents(
            IReadOnlyList<string> orderedNodeIds,
            IReadOnlyDictionary<string, List<string>> children,
            IReadOnlyDictionary<string, int> orderIndex)
        {
            int nextIndex = 0;
            var indexByNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lowLinkByNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var components = new List<IReadOnlyList<string>>();

            void StrongConnect(string nodeId)
            {
                indexByNode[nodeId] = nextIndex;
                lowLinkByNode[nodeId] = nextIndex;
                nextIndex++;
                stack.Push(nodeId);
                onStack.Add(nodeId);

                foreach (string childId in children[nodeId])
                {
                    if (!indexByNode.ContainsKey(childId))
                    {
                        StrongConnect(childId);
                        lowLinkByNode[nodeId] = Math.Min(lowLinkByNode[nodeId], lowLinkByNode[childId]);
                    }
                    else if (onStack.Contains(childId))
                    {
                        lowLinkByNode[nodeId] = Math.Min(lowLinkByNode[nodeId], indexByNode[childId]);
                    }
                }

                if (lowLinkByNode[nodeId] != indexByNode[nodeId]) return;

                var component = new List<string>();
                string current;
                do
                {
                    current = stack.Pop();
                    onStack.Remove(current);
                    component.Add(current);
                }
                while (!string.Equals(current, nodeId, StringComparison.OrdinalIgnoreCase));

                component.Sort((left, right) => orderIndex[left].CompareTo(orderIndex[right]));
                components.Add(component.ToArray());
            }

            foreach (string nodeId in orderedNodeIds)
            {
                if (!indexByNode.ContainsKey(nodeId)) StrongConnect(nodeId);
            }

            return components
                .OrderBy(component => component.Min(nodeId => orderIndex[nodeId]))
                .ToArray();
        }

        private static Dictionary<string, int> CalculateRanks(
            IReadOnlyList<string> orderedNodeIds,
            IReadOnlyList<ComposeLayoutEdge> edges,
            IReadOnlyList<IReadOnlyList<string>> stronglyConnectedComponents,
            IReadOnlyDictionary<string, int> stronglyConnectedComponentByNode,
            IReadOnlyDictionary<string, int> orderIndex)
        {
            int componentCount = stronglyConnectedComponents.Count;
            var componentRanks = new int[componentCount];
            var componentInDegree = new int[componentCount];
            var componentChildren = Enumerable.Range(0, componentCount)
                .ToDictionary(index => index, _ => new HashSet<int>());
            var componentOrder = Enumerable.Range(0, componentCount)
                .ToDictionary(
                    index => index,
                    index => stronglyConnectedComponents[index].Min(nodeId => orderIndex[nodeId]));

            foreach (ComposeLayoutEdge edge in edges)
            {
                int sourceComponent = stronglyConnectedComponentByNode[edge.SourceId];
                int targetComponent = stronglyConnectedComponentByNode[edge.TargetId];
                if (sourceComponent == targetComponent) continue;
                if (componentChildren[sourceComponent].Add(targetComponent))
                    componentInDegree[targetComponent]++;
            }

            var ready = new SortedSet<(int Order, int Component)>();
            for (int index = 0; index < componentCount; index++)
            {
                if (componentInDegree[index] == 0)
                    ready.Add((componentOrder[index], index));
            }

            while (ready.Count > 0)
            {
                (int _, int component) = ready.Min;
                ready.Remove(ready.Min);

                foreach (int childComponent in componentChildren[component]
                             .OrderBy(index => componentOrder[index]))
                {
                    componentRanks[childComponent] = Math.Max(
                        componentRanks[childComponent],
                        componentRanks[component] + 1);
                    componentInDegree[childComponent]--;
                    if (componentInDegree[childComponent] == 0)
                        ready.Add((componentOrder[childComponent], childComponent));
                }
            }

            return orderedNodeIds.ToDictionary(
                nodeId => nodeId,
                nodeId => componentRanks[stronglyConnectedComponentByNode[nodeId]],
                StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ComposeLayoutEdgeComparer : IEqualityComparer<ComposeLayoutEdge>
        {
            public static ComposeLayoutEdgeComparer Instance { get; } = new();

            public bool Equals(ComposeLayoutEdge? left, ComposeLayoutEdge? right)
            {
                if (ReferenceEquals(left, right)) return true;
                if (left is null || right is null) return false;
                return string.Equals(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(left.TargetId, right.TargetId, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(ComposeLayoutEdge edge) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(edge.SourceId),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(edge.TargetId));
        }
    }
}
