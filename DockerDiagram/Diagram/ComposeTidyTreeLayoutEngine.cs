using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Diagram
{
    public sealed record ComposeTidyTreeNode(
        string Id,
        double DepthSize,
        double BreadthSize);

    public sealed record ComposeTidyTreePosition(
        double Depth,
        double Breadth);

    public sealed class ComposeTidyTreeLayoutResult
    {
        internal ComposeTidyTreeLayoutResult(
            IReadOnlyDictionary<string, ComposeTidyTreePosition> positions,
            double breadthStart,
            double breadthEnd)
        {
            Positions = positions;
            BreadthStart = breadthStart;
            BreadthEnd = breadthEnd;
        }

        public IReadOnlyDictionary<string, ComposeTidyTreePosition> Positions { get; }
        public double BreadthStart { get; }
        public double BreadthEnd { get; }
    }

    /// <summary>
    /// Reingold-Tilford의 tidy-tree 원칙을 가변 노드 크기로 확장한 축 독립 배치기입니다.
    /// 형제 서브트리의 좌우 윤곽을 apportion하여 겹침을 제거하고,
    /// 부모와 첫 번째 자식의 위쪽 경계를 같은 첫 줄에 맞춥니다.
    /// </summary>
    public static class ComposeTidyTreeLayoutEngine
    {
        public static ComposeTidyTreeLayoutResult Arrange(
            ComposeGraphTopology topology,
            IEnumerable<ComposeTidyTreeNode> inputNodes,
            double depthOrigin,
            double breadthOrigin,
            double depthGap,
            double siblingGap,
            double forestGap)
        {
            ArgumentNullException.ThrowIfNull(topology);
            ArgumentNullException.ThrowIfNull(inputNodes);
            if (topology.Kind is not (ComposeLayoutGraphKind.Tree or ComposeLayoutGraphKind.Forest))
                throw new ArgumentException("Tidy-tree layout requires a Tree or Forest topology.", nameof(topology));

            var nodes = inputNodes
                .Where(node => !string.IsNullOrWhiteSpace(node.Id))
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(
                    node => node.Id,
                    node => new ComposeTidyTreeNode(
                        node.Id,
                        Math.Max(1, node.DepthSize),
                        Math.Max(1, node.BreadthSize)),
                    StringComparer.OrdinalIgnoreCase);

            foreach (string nodeId in topology.OrderedNodeIds)
            {
                if (!nodes.ContainsKey(nodeId))
                    throw new ArgumentException($"Missing size for node '{nodeId}'.", nameof(inputNodes));
            }

            if (topology.OrderedNodeIds.Count == 0)
            {
                return new ComposeTidyTreeLayoutResult(
                    new Dictionary<string, ComposeTidyTreePosition>(StringComparer.OrdinalIgnoreCase),
                    breadthOrigin,
                    breadthOrigin);
            }

            var levelSizes = topology.OrderedNodeIds
                .GroupBy(nodeId => topology.RankByNode[nodeId])
                .ToDictionary(
                    group => group.Key,
                    group => group.Max(nodeId => nodes[nodeId].DepthSize));
            int maximumRank = topology.RankByNode.Values.DefaultIfEmpty(0).Max();
            var levelPositions = new Dictionary<int, double> { [0] = depthOrigin };
            for (int rank = 1; rank <= maximumRank; rank++)
            {
                levelPositions[rank] =
                    levelPositions[rank - 1] +
                    levelSizes.GetValueOrDefault(rank - 1, 1) +
                    Math.Max(0, depthGap);
            }

            var absoluteBreadthCenters =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double forestCursor = breadthOrigin;
            bool hasTree = false;

            foreach (string rootId in topology.RootIds)
            {
                SubtreeLayout tree = MeasureSubtree(
                    rootId,
                    topology.ChildrenByNode,
                    nodes,
                    Math.Max(0, siblingGap));
                double treeMinimum = tree.LeftContour.Values.Min();
                double treeMaximum = tree.RightContour.Values.Max();
                double treeShift = forestCursor - treeMinimum;

                foreach ((string nodeId, double center) in tree.BreadthCenters)
                    absoluteBreadthCenters[nodeId] = center + treeShift;

                forestCursor = treeMaximum + treeShift + Math.Max(0, forestGap);
                hasTree = true;
            }

            if (absoluteBreadthCenters.Count != topology.OrderedNodeIds.Count)
                throw new InvalidOperationException("Tree layout did not visit every topology node.");

            var positions = new Dictionary<string, ComposeTidyTreePosition>(
                StringComparer.OrdinalIgnoreCase);
            double breadthStart = double.PositiveInfinity;
            double breadthEnd = double.NegativeInfinity;
            foreach (string nodeId in topology.OrderedNodeIds)
            {
                ComposeTidyTreeNode node = nodes[nodeId];
                double breadth = absoluteBreadthCenters[nodeId] - (node.BreadthSize / 2.0);
                double depth = levelPositions[topology.RankByNode[nodeId]];
                positions[nodeId] = new ComposeTidyTreePosition(depth, breadth);
                breadthStart = Math.Min(breadthStart, breadth);
                breadthEnd = Math.Max(breadthEnd, breadth + node.BreadthSize);
            }

            if (!hasTree)
            {
                breadthStart = breadthOrigin;
                breadthEnd = breadthOrigin;
            }

            return new ComposeTidyTreeLayoutResult(positions, breadthStart, breadthEnd);
        }

        private static SubtreeLayout MeasureSubtree(
            string nodeId,
            IReadOnlyDictionary<string, IReadOnlyList<string>> childrenByNode,
            IReadOnlyDictionary<string, ComposeTidyTreeNode> nodes,
            double siblingGap)
        {
            var placedChildren = new List<SubtreeLayout>();
            var combinedLeft = new Dictionary<int, double>();
            var combinedRight = new Dictionary<int, double>();

            foreach (string childId in childrenByNode[nodeId])
            {
                SubtreeLayout child = MeasureSubtree(
                    childId,
                    childrenByNode,
                    nodes,
                    siblingGap);
                double shift = placedChildren.Count == 0
                    ? 0
                    : CalculateApportionShift(combinedRight, child.LeftContour, siblingGap);
                SubtreeLayout shiftedChild = child.Shift(shift);
                placedChildren.Add(shiftedChild);
                MergeContours(combinedLeft, combinedRight, shiftedChild, depthOffset: 0);
            }

            ComposeTidyTreeNode node = nodes[nodeId];
            double parentCenter = 0;
            if (placedChildren.Count > 0)
            {
                string firstChildId = childrenByNode[nodeId][0];
                ComposeTidyTreeNode firstChild = nodes[firstChildId];
                double firstChildTop =
                    placedChildren[0].BreadthCenters[firstChildId] -
                    (firstChild.BreadthSize / 2.0);
                parentCenter = firstChildTop + (node.BreadthSize / 2.0);
            }
            var result = new SubtreeLayout();
            foreach (SubtreeLayout child in placedChildren)
            {
                foreach ((string descendantId, double center) in child.BreadthCenters)
                    result.BreadthCenters[descendantId] = center - parentCenter;

                MergeContours(
                    result.LeftContour,
                    result.RightContour,
                    child.Shift(-parentCenter),
                    depthOffset: 1);
            }

            result.BreadthCenters[nodeId] = 0;
            result.LeftContour[0] = -(node.BreadthSize / 2.0);
            result.RightContour[0] = node.BreadthSize / 2.0;
            return result;
        }

        private static double CalculateApportionShift(
            IReadOnlyDictionary<int, double> placedRightContour,
            IReadOnlyDictionary<int, double> candidateLeftContour,
            double siblingGap)
        {
            double requiredShift = 0;
            foreach (int depth in placedRightContour.Keys.Intersect(candidateLeftContour.Keys))
            {
                requiredShift = Math.Max(
                    requiredShift,
                    placedRightContour[depth] + siblingGap - candidateLeftContour[depth]);
            }

            return requiredShift;
        }

        private static void MergeContours(
            IDictionary<int, double> targetLeft,
            IDictionary<int, double> targetRight,
            SubtreeLayout source,
            int depthOffset)
        {
            foreach ((int depth, double left) in source.LeftContour)
            {
                int targetDepth = depth + depthOffset;
                targetLeft[targetDepth] = targetLeft.TryGetValue(targetDepth, out double existing)
                    ? Math.Min(existing, left)
                    : left;
            }

            foreach ((int depth, double right) in source.RightContour)
            {
                int targetDepth = depth + depthOffset;
                targetRight[targetDepth] = targetRight.TryGetValue(targetDepth, out double existing)
                    ? Math.Max(existing, right)
                    : right;
            }
        }

        private sealed class SubtreeLayout
        {
            public Dictionary<string, double> BreadthCenters { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<int, double> LeftContour { get; } = new();
            public Dictionary<int, double> RightContour { get; } = new();

            public SubtreeLayout Shift(double delta)
            {
                var shifted = new SubtreeLayout();
                foreach ((string nodeId, double center) in BreadthCenters)
                    shifted.BreadthCenters[nodeId] = center + delta;
                foreach ((int depth, double value) in LeftContour)
                    shifted.LeftContour[depth] = value + delta;
                foreach ((int depth, double value) in RightContour)
                    shifted.RightContour[depth] = value + delta;
                return shifted;
            }
        }
    }
}
