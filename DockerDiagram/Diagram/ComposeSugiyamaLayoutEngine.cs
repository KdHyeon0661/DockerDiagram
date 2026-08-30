using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Diagram
{
    public sealed record ComposeSugiyamaNode(
        string Id,
        double DepthSize,
        double BreadthSize);

    public sealed record ComposeSugiyamaPosition(
        double Depth,
        double Breadth);

    public sealed class ComposeSugiyamaLayoutResult
    {
        internal ComposeSugiyamaLayoutResult(
            IReadOnlyDictionary<string, ComposeSugiyamaPosition> positions,
            IReadOnlyDictionary<string, int> layerByNode,
            IReadOnlyList<IReadOnlyList<string>> orderedLayers,
            IReadOnlyList<ComposeLayoutEdge> reversedEdges,
            IReadOnlyList<ComposeLayoutEdge> suppressedSelfLoops,
            int dummyVertexCount,
            long initialCrossingCount,
            long finalCrossingCount,
            double breadthStart,
            double breadthEnd)
        {
            Positions = positions;
            LayerByNode = layerByNode;
            OrderedLayers = orderedLayers;
            ReversedEdges = reversedEdges;
            SuppressedSelfLoops = suppressedSelfLoops;
            DummyVertexCount = dummyVertexCount;
            InitialCrossingCount = initialCrossingCount;
            FinalCrossingCount = finalCrossingCount;
            BreadthStart = breadthStart;
            BreadthEnd = breadthEnd;
        }

        public IReadOnlyDictionary<string, ComposeSugiyamaPosition> Positions { get; }
        public IReadOnlyDictionary<string, int> LayerByNode { get; }
        public IReadOnlyList<IReadOnlyList<string>> OrderedLayers { get; }
        public IReadOnlyList<ComposeLayoutEdge> ReversedEdges { get; }
        public IReadOnlyList<ComposeLayoutEdge> SuppressedSelfLoops { get; }
        public int DummyVertexCount { get; }
        public long InitialCrossingCount { get; }
        public long FinalCrossingCount { get; }
        public double BreadthStart { get; }
        public double BreadthEnd { get; }
    }

    /// <summary>
    /// DAG와 순환 Compose 그래프를 위한 Sugiyama 계층형 배치기입니다.
    /// Cycle 정규화, longest-path layering, dummy vertex 삽입,
    /// barycenter 교차 최소화, 가변 크기 좌표 배정을 순서대로 수행합니다.
    /// </summary>
    public static class ComposeSugiyamaLayoutEngine
    {
        private const int OrderingSweepCount = 8;
        private const int CoordinateSweepCount = 8;

        public static ComposeSugiyamaLayoutResult Arrange(
            ComposeGraphTopology topology,
            IEnumerable<ComposeSugiyamaNode> inputNodes,
            double depthOrigin,
            double breadthOrigin,
            double depthGap,
            double siblingGap)
        {
            ArgumentNullException.ThrowIfNull(topology);
            ArgumentNullException.ThrowIfNull(inputNodes);
            if (topology.Kind is not (
                    ComposeLayoutGraphKind.DirectedAcyclicGraph or
                    ComposeLayoutGraphKind.Cyclic))
            {
                throw new ArgumentException(
                    "Sugiyama layout requires a DAG or Cyclic topology.",
                    nameof(topology));
            }

            var nodes = inputNodes
                .Where(node => !string.IsNullOrWhiteSpace(node.Id))
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(
                    node => node.Id,
                    node => new ComposeSugiyamaNode(
                        node.Id,
                        Math.Max(1, node.DepthSize),
                        Math.Max(1, node.BreadthSize)),
                    StringComparer.OrdinalIgnoreCase);
            foreach (string nodeId in topology.OrderedNodeIds)
            {
                if (!nodes.ContainsKey(nodeId))
                    throw new ArgumentException($"Missing size for node '{nodeId}'.", nameof(inputNodes));
            }

            var orderIndex = topology.OrderedNodeIds
                .Select((nodeId, index) => (nodeId, index))
                .ToDictionary(
                    item => item.nodeId,
                    item => item.index,
                    StringComparer.OrdinalIgnoreCase);
            NormalizedGraph normalized = NormalizeCycles(topology, orderIndex);
            Dictionary<string, int> layerByNode = AssignLayers(
                topology.OrderedNodeIds,
                normalized.Edges,
                orderIndex);
            LayeredGraph layered = CreateLayeredGraph(
                topology.OrderedNodeIds,
                nodes,
                normalized.Edges,
                layerByNode,
                orderIndex);

            long initialCrossings = CountCrossings(layered.Layers, layered.Segments);
            List<List<LayerVertex>> bestLayers = CloneLayers(layered.Layers);
            long bestCrossings = initialCrossings;
            for (int sweep = 0; sweep < OrderingSweepCount; sweep++)
            {
                SweepLayerOrder(layered.Layers, layered.Segments, downward: true);
                SweepLayerOrder(layered.Layers, layered.Segments, downward: false);
                long crossings = CountCrossings(layered.Layers, layered.Segments);
                if (crossings < bestCrossings)
                {
                    bestCrossings = crossings;
                    bestLayers = CloneLayers(layered.Layers);
                }
            }
            layered.Layers = bestLayers;

            Dictionary<string, double> breadthCenters = AssignBreadthCoordinates(
                layered.Layers,
                layered.Segments,
                Math.Max(0, siblingGap));
            AlignLayerFirstRows(layered.Layers, breadthCenters);
            var levelSizes = layered.Layers
                .Select((layer, index) => new
                {
                    index,
                    size = layer
                        .Where(vertex => !vertex.IsDummy)
                        .Select(vertex => vertex.DepthSize)
                        .DefaultIfEmpty(1)
                        .Max()
                })
                .ToDictionary(item => item.index, item => item.size);
            var levelPositions = new Dictionary<int, double> { [0] = depthOrigin };
            for (int layer = 1; layer < layered.Layers.Count; layer++)
            {
                levelPositions[layer] =
                    levelPositions[layer - 1] +
                    levelSizes[layer - 1] +
                    Math.Max(0, depthGap);
            }

            double minimumBreadth = layered.Layers
                .SelectMany(layer => layer)
                .Where(vertex => !vertex.IsDummy)
                .Min(vertex => breadthCenters[vertex.Id] - (vertex.BreadthSize / 2.0));
            double breadthShift = breadthOrigin - minimumBreadth;
            var positions = new Dictionary<string, ComposeSugiyamaPosition>(
                StringComparer.OrdinalIgnoreCase);
            double breadthStart = double.PositiveInfinity;
            double breadthEnd = double.NegativeInfinity;
            foreach (string nodeId in topology.OrderedNodeIds)
            {
                ComposeSugiyamaNode node = nodes[nodeId];
                int layer = layerByNode[nodeId];
                double breadth =
                    breadthCenters[nodeId] +
                    breadthShift -
                    (node.BreadthSize / 2.0);
                positions[nodeId] = new ComposeSugiyamaPosition(
                    levelPositions[layer],
                    breadth);
                breadthStart = Math.Min(breadthStart, breadth);
                breadthEnd = Math.Max(breadthEnd, breadth + node.BreadthSize);
            }

            IReadOnlyList<IReadOnlyList<string>> orderedLayers = layered.Layers
                .Select(layer => (IReadOnlyList<string>)layer
                    .Where(vertex => !vertex.IsDummy)
                    .Select(vertex => vertex.Id)
                    .ToArray())
                .ToArray();
            return new ComposeSugiyamaLayoutResult(
                positions,
                new Dictionary<string, int>(layerByNode, StringComparer.OrdinalIgnoreCase),
                orderedLayers,
                normalized.ReversedEdges,
                normalized.SuppressedSelfLoops,
                layered.DummyVertexCount,
                initialCrossings,
                bestCrossings,
                breadthStart,
                breadthEnd);
        }

        private static NormalizedGraph NormalizeCycles(
            ComposeGraphTopology topology,
            IReadOnlyDictionary<string, int> orderIndex)
        {
            var normalizedEdges = new List<ComposeLayoutEdge>();
            var reversedEdges = new List<ComposeLayoutEdge>();
            var selfLoops = new List<ComposeLayoutEdge>();

            foreach (ComposeLayoutEdge edge in topology.Edges)
            {
                if (string.Equals(edge.SourceId, edge.TargetId, StringComparison.OrdinalIgnoreCase))
                {
                    selfLoops.Add(edge);
                    continue;
                }

                bool sameStrongComponent =
                    topology.StronglyConnectedComponentByNode[edge.SourceId] ==
                    topology.StronglyConnectedComponentByNode[edge.TargetId];
                if (sameStrongComponent && orderIndex[edge.SourceId] > orderIndex[edge.TargetId])
                {
                    normalizedEdges.Add(new ComposeLayoutEdge(edge.TargetId, edge.SourceId));
                    reversedEdges.Add(edge);
                }
                else
                {
                    normalizedEdges.Add(edge);
                }
            }

            return new NormalizedGraph(
                normalizedEdges.Distinct(LayoutEdgeComparer.Instance).ToArray(),
                reversedEdges.ToArray(),
                selfLoops.ToArray());
        }

        private static Dictionary<string, int> AssignLayers(
            IReadOnlyList<string> orderedNodeIds,
            IReadOnlyList<ComposeLayoutEdge> edges,
            IReadOnlyDictionary<string, int> orderIndex)
        {
            var children = orderedNodeIds.ToDictionary(
                nodeId => nodeId,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            var inDegree = orderedNodeIds.ToDictionary(
                nodeId => nodeId,
                _ => 0,
                StringComparer.OrdinalIgnoreCase);
            var layers = orderedNodeIds.ToDictionary(
                nodeId => nodeId,
                _ => 0,
                StringComparer.OrdinalIgnoreCase);

            foreach (ComposeLayoutEdge edge in edges)
            {
                if (children[edge.SourceId].Add(edge.TargetId))
                    inDegree[edge.TargetId]++;
            }

            var ready = new SortedSet<(int Order, string NodeId)>();
            foreach (string nodeId in orderedNodeIds)
            {
                if (inDegree[nodeId] == 0)
                    ready.Add((orderIndex[nodeId], nodeId));
            }

            int visited = 0;
            while (ready.Count > 0)
            {
                (int _, string nodeId) = ready.Min;
                ready.Remove(ready.Min);
                visited++;

                foreach (string childId in children[nodeId]
                             .OrderBy(id => orderIndex[id]))
                {
                    layers[childId] = Math.Max(layers[childId], layers[nodeId] + 1);
                    inDegree[childId]--;
                    if (inDegree[childId] == 0)
                        ready.Add((orderIndex[childId], childId));
                }
            }

            if (visited != orderedNodeIds.Count)
                throw new InvalidOperationException("Cycle normalization did not produce a DAG.");
            return layers;
        }

        private static LayeredGraph CreateLayeredGraph(
            IReadOnlyList<string> orderedNodeIds,
            IReadOnlyDictionary<string, ComposeSugiyamaNode> nodes,
            IReadOnlyList<ComposeLayoutEdge> edges,
            IReadOnlyDictionary<string, int> layerByNode,
            IReadOnlyDictionary<string, int> orderIndex)
        {
            int maximumLayer = layerByNode.Values.DefaultIfEmpty(0).Max();
            var layers = Enumerable.Range(0, maximumLayer + 1)
                .Select(_ => new List<LayerVertex>())
                .ToList();
            var vertices = new Dictionary<string, LayerVertex>(StringComparer.OrdinalIgnoreCase);
            int sequence = 0;
            foreach (string nodeId in orderedNodeIds)
            {
                ComposeSugiyamaNode node = nodes[nodeId];
                var vertex = new LayerVertex(
                    nodeId,
                    layerByNode[nodeId],
                    node.DepthSize,
                    node.BreadthSize,
                    isDummy: false,
                    stableOrder: orderIndex[nodeId],
                    sequence++);
                vertices[nodeId] = vertex;
                layers[vertex.Layer].Add(vertex);
            }

            var segments = new List<LayerSegment>();
            int dummyCount = 0;
            int edgeIndex = 0;
            foreach (ComposeLayoutEdge edge in edges)
            {
                LayerVertex previous = vertices[edge.SourceId];
                int sourceLayer = layerByNode[edge.SourceId];
                int targetLayer = layerByNode[edge.TargetId];
                int span = targetLayer - sourceLayer;
                for (int layer = sourceLayer + 1; layer < targetLayer; layer++)
                {
                    double fraction = (double)(layer - sourceLayer) / span;
                    string dummyId = $"__sugiyama_dummy_{edgeIndex}_{layer}";
                    var dummy = new LayerVertex(
                        dummyId,
                        layer,
                        depthSize: 1,
                        breadthSize: 1,
                        isDummy: true,
                        stableOrder:
                            orderIndex[edge.SourceId] +
                            ((orderIndex[edge.TargetId] - orderIndex[edge.SourceId]) * fraction),
                        sequence++);
                    vertices[dummyId] = dummy;
                    layers[layer].Add(dummy);
                    segments.Add(new LayerSegment(previous.Id, dummy.Id));
                    previous = dummy;
                    dummyCount++;
                }

                segments.Add(new LayerSegment(previous.Id, edge.TargetId));
                edgeIndex++;
            }

            foreach (List<LayerVertex> layer in layers)
            {
                layer.Sort((left, right) =>
                {
                    int stableComparison = left.StableOrder.CompareTo(right.StableOrder);
                    return stableComparison != 0
                        ? stableComparison
                        : left.Sequence.CompareTo(right.Sequence);
                });
            }

            return new LayeredGraph(layers, vertices, segments, dummyCount);
        }

        private static void SweepLayerOrder(
            IList<List<LayerVertex>> layers,
            IReadOnlyList<LayerSegment> segments,
            bool downward)
        {
            var incoming = segments
                .GroupBy(segment => segment.TargetId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(segment => segment.SourceId).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var outgoing = segments
                .GroupBy(segment => segment.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(segment => segment.TargetId).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            IEnumerable<int> layerIndexes = downward
                ? Enumerable.Range(1, Math.Max(0, layers.Count - 1))
                : Enumerable.Range(0, Math.Max(0, layers.Count - 1)).Reverse();

            foreach (int layerIndex in layerIndexes)
            {
                int adjacentIndex = downward ? layerIndex - 1 : layerIndex + 1;
                var adjacentPositions = layers[adjacentIndex]
                    .Select((vertex, index) => (vertex.Id, index))
                    .ToDictionary(
                        item => item.Id,
                        item => item.index,
                        StringComparer.OrdinalIgnoreCase);
                var oldPositions = layers[layerIndex]
                    .Select((vertex, index) => (vertex.Id, index))
                    .ToDictionary(
                        item => item.Id,
                        item => item.index,
                        StringComparer.OrdinalIgnoreCase);
                IReadOnlyDictionary<string, string[]> neighbors = downward ? incoming : outgoing;

                layers[layerIndex] = layers[layerIndex]
                    .Select(vertex =>
                    {
                        bool hasNeighbors =
                            neighbors.TryGetValue(vertex.Id, out string[]? neighborIds) &&
                            neighborIds.Length > 0;
                        double barycenter = hasNeighbors
                            ? neighborIds!.Average(id => adjacentPositions[id])
                            : oldPositions[vertex.Id];
                        return new
                        {
                            Vertex = vertex,
                            Barycenter = barycenter,
                            OldPosition = oldPositions[vertex.Id]
                        };
                    })
                    .OrderBy(item => item.Barycenter)
                    .ThenBy(item => item.OldPosition)
                    .Select(item => item.Vertex)
                    .ToList();
            }
        }

        private static long CountCrossings(
            IReadOnlyList<List<LayerVertex>> layers,
            IReadOnlyList<LayerSegment> segments)
        {
            long crossings = 0;
            for (int layerIndex = 0; layerIndex < layers.Count - 1; layerIndex++)
            {
                var sourcePositions = layers[layerIndex]
                    .Select((vertex, index) => (vertex.Id, index))
                    .ToDictionary(
                        item => item.Id,
                        item => item.index,
                        StringComparer.OrdinalIgnoreCase);
                var targetPositions = layers[layerIndex + 1]
                    .Select((vertex, index) => (vertex.Id, index))
                    .ToDictionary(
                        item => item.Id,
                        item => item.index,
                        StringComparer.OrdinalIgnoreCase);
                LayerSegment[] layerSegments = segments
                    .Where(segment =>
                        sourcePositions.ContainsKey(segment.SourceId) &&
                        targetPositions.ContainsKey(segment.TargetId))
                    .ToArray();

                for (int leftIndex = 0; leftIndex < layerSegments.Length; leftIndex++)
                {
                    LayerSegment left = layerSegments[leftIndex];
                    for (int rightIndex = leftIndex + 1; rightIndex < layerSegments.Length; rightIndex++)
                    {
                        LayerSegment right = layerSegments[rightIndex];
                        int sourceOrder =
                            sourcePositions[left.SourceId] -
                            sourcePositions[right.SourceId];
                        int targetOrder =
                            targetPositions[left.TargetId] -
                            targetPositions[right.TargetId];
                        if ((long)sourceOrder * targetOrder < 0)
                            crossings++;
                    }
                }
            }

            return crossings;
        }

        private static Dictionary<string, double> AssignBreadthCoordinates(
            IReadOnlyList<List<LayerVertex>> layers,
            IReadOnlyList<LayerSegment> segments,
            double siblingGap)
        {
            var centers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (List<LayerVertex> layer in layers)
            {
                double cursor = 0;
                for (int index = 0; index < layer.Count; index++)
                {
                    LayerVertex vertex = layer[index];
                    cursor = index == 0
                        ? vertex.BreadthSize / 2.0
                        : cursor +
                          (layer[index - 1].BreadthSize / 2.0) +
                          siblingGap +
                          (vertex.BreadthSize / 2.0);
                    centers[vertex.Id] = cursor;
                }

                if (layer.Count > 0)
                {
                    double midpoint =
                        (centers[layer[0].Id] + centers[layer[^1].Id]) / 2.0;
                    foreach (LayerVertex vertex in layer)
                        centers[vertex.Id] -= midpoint;
                }
            }

            var incoming = segments
                .GroupBy(segment => segment.TargetId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(segment => segment.SourceId).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var outgoing = segments
                .GroupBy(segment => segment.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(segment => segment.TargetId).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            for (int sweep = 0; sweep < CoordinateSweepCount; sweep++)
            {
                for (int layer = 1; layer < layers.Count; layer++)
                    PlaceLayer(layers[layer], incoming, centers, siblingGap);
                for (int layer = layers.Count - 2; layer >= 0; layer--)
                    PlaceLayer(layers[layer], outgoing, centers, siblingGap);
            }

            return centers;
        }

        private static void AlignLayerFirstRows(
            IReadOnlyList<List<LayerVertex>> layers,
            IDictionary<string, double> centers)
        {
            foreach (IReadOnlyList<LayerVertex> layer in layers)
            {
                LayerVertex[] realVertices = layer.Where(vertex => !vertex.IsDummy).ToArray();
                if (realVertices.Length == 0) continue;

                double firstRow = realVertices.Min(vertex =>
                    centers[vertex.Id] - (vertex.BreadthSize / 2.0));
                foreach (LayerVertex vertex in layer)
                    centers[vertex.Id] -= firstRow;
            }
        }

        private static void PlaceLayer(
            IReadOnlyList<LayerVertex> layer,
            IReadOnlyDictionary<string, string[]> neighbors,
            IDictionary<string, double> centers,
            double siblingGap)
        {
            if (layer.Count == 0) return;
            var desired = new double[layer.Count];
            for (int index = 0; index < layer.Count; index++)
            {
                LayerVertex vertex = layer[index];
                desired[index] =
                    neighbors.TryGetValue(vertex.Id, out string[]? neighborIds) &&
                    neighborIds.Length > 0
                        ? neighborIds.Average(id => centers[id])
                        : centers[vertex.Id];
            }

            var packed = new double[layer.Count];
            packed[0] = desired[0];
            for (int index = 1; index < layer.Count; index++)
            {
                double separation =
                    (layer[index - 1].BreadthSize / 2.0) +
                    siblingGap +
                    (layer[index].BreadthSize / 2.0);
                packed[index] = Math.Max(desired[index], packed[index - 1] + separation);
            }

            double shift = desired.Average() - packed.Average();
            for (int index = 0; index < layer.Count; index++)
                centers[layer[index].Id] = packed[index] + shift;
        }

        private static List<List<LayerVertex>> CloneLayers(
            IEnumerable<List<LayerVertex>> layers) =>
            layers.Select(layer => layer.ToList()).ToList();

        private sealed record NormalizedGraph(
            IReadOnlyList<ComposeLayoutEdge> Edges,
            IReadOnlyList<ComposeLayoutEdge> ReversedEdges,
            IReadOnlyList<ComposeLayoutEdge> SuppressedSelfLoops);

        private sealed record LayerSegment(string SourceId, string TargetId);

        private sealed class LayerVertex
        {
            public LayerVertex(
                string id,
                int layer,
                double depthSize,
                double breadthSize,
                bool isDummy,
                double stableOrder,
                int sequence)
            {
                Id = id;
                Layer = layer;
                DepthSize = depthSize;
                BreadthSize = breadthSize;
                IsDummy = isDummy;
                StableOrder = stableOrder;
                Sequence = sequence;
            }

            public string Id { get; }
            public int Layer { get; }
            public double DepthSize { get; }
            public double BreadthSize { get; }
            public bool IsDummy { get; }
            public double StableOrder { get; }
            public int Sequence { get; }
        }

        private sealed class LayeredGraph
        {
            public LayeredGraph(
                List<List<LayerVertex>> layers,
                IReadOnlyDictionary<string, LayerVertex> vertices,
                IReadOnlyList<LayerSegment> segments,
                int dummyVertexCount)
            {
                Layers = layers;
                Vertices = vertices;
                Segments = segments;
                DummyVertexCount = dummyVertexCount;
            }

            public List<List<LayerVertex>> Layers { get; set; }
            public IReadOnlyDictionary<string, LayerVertex> Vertices { get; }
            public IReadOnlyList<LayerSegment> Segments { get; }
            public int DummyVertexCount { get; }
        }

        private sealed class LayoutEdgeComparer : IEqualityComparer<ComposeLayoutEdge>
        {
            public static LayoutEdgeComparer Instance { get; } = new();

            public bool Equals(ComposeLayoutEdge? left, ComposeLayoutEdge? right)
            {
                if (ReferenceEquals(left, right)) return true;
                if (left is null || right is null) return false;
                return
                    string.Equals(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.TargetId, right.TargetId, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(ComposeLayoutEdge edge) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(edge.SourceId),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(edge.TargetId));
        }
    }
}
