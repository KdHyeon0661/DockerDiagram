using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Diagram
{
    public sealed record ComposeNetworkLayoutNode(
        string Id,
        double X,
        double Y,
        double Width,
        double Height);

    public sealed record ComposeNetworkLayoutGroup(
        string Id,
        string Name,
        IReadOnlyList<string> MemberIds);

    public sealed record ComposeNetworkLayoutRect(
        double X,
        double Y,
        double Width,
        double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;

        public bool Intersects(ComposeNetworkLayoutRect other) =>
            X < other.Right &&
            Right > other.X &&
            Y < other.Bottom &&
            Bottom > other.Y;

        public bool Contains(ComposeNetworkLayoutRect other, double tolerance = 0.001) =>
            X <= other.X + tolerance &&
            Y <= other.Y + tolerance &&
            Right + tolerance >= other.Right &&
            Bottom + tolerance >= other.Bottom;
    }

    public sealed record ComposeNetworkLayoutRelation(
        string LeftNetworkId,
        string RightNetworkId,
        ComposeNetworkRelationKind Kind,
        IReadOnlyList<string> SharedMemberIds);

    public sealed class ComposeNetworkLayoutResult
    {
        internal ComposeNetworkLayoutResult(
            IReadOnlyDictionary<string, ComposeNetworkLayoutRect> boundsByNetwork,
            IReadOnlyDictionary<string, int> ringIndexByNetwork,
            IReadOnlyDictionary<string, string?> parentNetworkByNetwork,
            IReadOnlyList<ComposeNetworkLayoutRelation> relations)
        {
            BoundsByNetwork = boundsByNetwork;
            RingIndexByNetwork = ringIndexByNetwork;
            ParentNetworkByNetwork = parentNetworkByNetwork;
            Relations = relations;
        }

        public IReadOnlyDictionary<string, ComposeNetworkLayoutRect> BoundsByNetwork { get; }
        public IReadOnlyDictionary<string, int> RingIndexByNetwork { get; }
        public IReadOnlyDictionary<string, string?> ParentNetworkByNetwork { get; }
        public IReadOnlyList<ComposeNetworkLayoutRelation> Relations { get; }
    }

    /// <summary>
    /// 네트워크를 노드를 움직이는 hard constraint가 아닌 겹칠 수 있는 soft group으로 배치합니다.
    /// 동일 집합은 동심 링, strict superset은 부모 박스, partial overlap은 공유 노드 영역을
    /// 실제 교집합으로 유지합니다.
    /// </summary>
    public static class ComposeNetworkLayoutEngine
    {
        public static ComposeNetworkLayoutResult Arrange(
            IEnumerable<ComposeNetworkLayoutNode> inputNodes,
            IEnumerable<ComposeNetworkLayoutGroup> inputGroups,
            double sidePadding,
            double topPadding,
            double bottomPadding,
            double coincidentRingGap,
            double headerHeight,
            double headerGap,
            double minimumWidth,
            double minimumHeight)
        {
            ArgumentNullException.ThrowIfNull(inputNodes);
            ArgumentNullException.ThrowIfNull(inputGroups);

            var nodes = inputNodes
                .Where(node => !string.IsNullOrWhiteSpace(node.Id))
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(
                    node => node.Id,
                    node => new ComposeNetworkLayoutNode(
                        node.Id,
                        node.X,
                        node.Y,
                        Math.Max(1, node.Width),
                        Math.Max(1, node.Height)),
                    StringComparer.OrdinalIgnoreCase);
            var groups = inputGroups
                .Where(group => !string.IsNullOrWhiteSpace(group.Id))
                .GroupBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(group => new ComposeNetworkLayoutGroup(
                    group.Id,
                    group.Name,
                    group.MemberIds
                        .Where(nodes.ContainsKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .ToArray();
            var activeGroups = groups
                .Where(group => group.MemberIds.Count > 0)
                .ToArray();
            if (activeGroups.Length == 0)
            {
                return new ComposeNetworkLayoutResult(
                    new Dictionary<string, ComposeNetworkLayoutRect>(
                        StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                    BuildRelations(groups));
            }

            var groupOrder = groups
                .Select((group, index) => (group.Id, index))
                .ToDictionary(
                    item => item.Id,
                    item => item.index,
                    StringComparer.OrdinalIgnoreCase);
            var clusters = BuildIdenticalClusters(activeGroups, groupOrder);
            foreach (NetworkCluster cluster in clusters)
            {
                cluster.Parent = clusters
                    .Where(candidate =>
                        !ReferenceEquals(candidate, cluster) &&
                        candidate.Members.Count > cluster.Members.Count &&
                        candidate.Members.IsSupersetOf(cluster.Members))
                    .OrderBy(candidate => candidate.Members.Count)
                    .ThenBy(candidate => candidate.Order)
                    .FirstOrDefault();
                cluster.Parent?.Children.Add(cluster);
            }

            double safeSidePadding = Math.Max(0, sidePadding);
            double safeTopPadding = Math.Max(0, topPadding);
            double safeBottomPadding = Math.Max(0, bottomPadding);
            double safeRingGap = Math.Max(0, coincidentRingGap);
            double safeMinimumWidth = Math.Max(1, minimumWidth);
            double safeMinimumHeight = Math.Max(1, minimumHeight);
            var boundsByNetwork =
                new Dictionary<string, ComposeNetworkLayoutRect>(StringComparer.OrdinalIgnoreCase);
            var ringIndexByNetwork =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            ComposeNetworkLayoutRect LayoutCluster(NetworkCluster cluster)
            {
                var contentBounds = cluster.Members
                    .Select(memberId => ToRect(nodes[memberId]))
                    .ToList();
                foreach (NetworkCluster child in cluster.Children
                             .OrderBy(child => child.Order))
                {
                    contentBounds.Add(LayoutCluster(child));
                }

                ComposeNetworkLayoutRect content = Union(contentBounds);
                ComposeNetworkLayoutRect baseBounds = ExpandContent(
                    content,
                    safeSidePadding,
                    safeTopPadding,
                    safeSidePadding,
                    safeBottomPadding,
                    safeMinimumWidth,
                    safeMinimumHeight);
                ComposeNetworkLayoutRect? outerBounds = null;
                for (int ringIndex = 0; ringIndex < cluster.Groups.Count; ringIndex++)
                {
                    ComposeNetworkLayoutGroup group = cluster.Groups[ringIndex];
                    double ringPadding = ringIndex * safeRingGap;
                    ComposeNetworkLayoutRect bounds = ringIndex == 0
                        ? baseBounds
                        : new ComposeNetworkLayoutRect(
                            baseBounds.X - ringPadding,
                            baseBounds.Y - ringPadding,
                            baseBounds.Width + (ringPadding * 2),
                            baseBounds.Height + (ringPadding * 2));
                    boundsByNetwork[group.Id] = bounds;
                    ringIndexByNetwork[group.Id] = ringIndex;
                    outerBounds = outerBounds is null
                        ? bounds
                        : Union(new[] { outerBounds, bounds });
                }

                cluster.OuterBounds = outerBounds ?? content;
                return cluster.OuterBounds;
            }

            foreach (NetworkCluster root in clusters
                         .Where(cluster => cluster.Parent is null)
                         .OrderBy(cluster => cluster.Order))
            {
                LayoutCluster(root);
            }

            ResolveHeaderOverlaps(
                clusters,
                boundsByNetwork,
                Math.Max(1, headerHeight),
                Math.Max(0, headerGap),
                safeSidePadding,
                safeTopPadding,
                safeBottomPadding,
                safeRingGap);
            var parentNetworkByNetwork =
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkCluster cluster in clusters)
            {
                string? parentNetworkId = cluster.Parent?.Groups[0].Id;
                foreach (ComposeNetworkLayoutGroup group in cluster.Groups)
                    parentNetworkByNetwork[group.Id] = parentNetworkId;
            }

            return new ComposeNetworkLayoutResult(
                boundsByNetwork,
                ringIndexByNetwork,
                parentNetworkByNetwork,
                BuildRelations(groups));
        }

        private static List<NetworkCluster> BuildIdenticalClusters(
            IEnumerable<ComposeNetworkLayoutGroup> groups,
            IReadOnlyDictionary<string, int> groupOrder)
        {
            var clusters = new List<NetworkCluster>();
            foreach (ComposeNetworkLayoutGroup group in groups)
            {
                var members = new HashSet<string>(
                    group.MemberIds,
                    StringComparer.OrdinalIgnoreCase);
                NetworkCluster? cluster = clusters.FirstOrDefault(
                    candidate => candidate.Members.SetEquals(members));
                if (cluster is null)
                {
                    cluster = new NetworkCluster(
                        members,
                        groupOrder[group.Id]);
                    clusters.Add(cluster);
                }

                cluster.Groups.Add(group);
            }

            foreach (NetworkCluster cluster in clusters)
            {
                cluster.Groups.Sort((left, right) =>
                {
                    int nameComparison = StringComparer.OrdinalIgnoreCase.Compare(
                        left.Name,
                        right.Name);
                    return nameComparison != 0
                        ? nameComparison
                        : groupOrder[left.Id].CompareTo(groupOrder[right.Id]);
                });
            }

            return clusters;
        }

        private static IReadOnlyList<ComposeNetworkLayoutRelation> BuildRelations(
            IReadOnlyList<ComposeNetworkLayoutGroup> groups)
        {
            var relations = new List<ComposeNetworkLayoutRelation>();
            for (int leftIndex = 0; leftIndex < groups.Count; leftIndex++)
            {
                var leftMembers = new HashSet<string>(
                    groups[leftIndex].MemberIds,
                    StringComparer.OrdinalIgnoreCase);
                for (int rightIndex = leftIndex + 1; rightIndex < groups.Count; rightIndex++)
                {
                    var rightMembers = new HashSet<string>(
                        groups[rightIndex].MemberIds,
                        StringComparer.OrdinalIgnoreCase);
                    relations.Add(new ComposeNetworkLayoutRelation(
                        groups[leftIndex].Id,
                        groups[rightIndex].Id,
                        Classify(leftMembers, rightMembers),
                        leftMembers
                            .Intersect(rightMembers, StringComparer.OrdinalIgnoreCase)
                            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                            .ToArray()));
                }
            }

            return relations;
        }

        private static ComposeNetworkRelationKind Classify(
            HashSet<string> left,
            HashSet<string> right)
        {
            if (left.SetEquals(right)) return ComposeNetworkRelationKind.Identical;
            if (left.IsSupersetOf(right)) return ComposeNetworkRelationKind.LeftContainsRight;
            if (right.IsSupersetOf(left)) return ComposeNetworkRelationKind.RightContainsLeft;
            if (left.Overlaps(right)) return ComposeNetworkRelationKind.PartialOverlap;
            return ComposeNetworkRelationKind.Disjoint;
        }

        private static void ResolveHeaderOverlaps(
            IReadOnlyList<NetworkCluster> clusters,
            IDictionary<string, ComposeNetworkLayoutRect> boundsByNetwork,
            double headerHeight,
            double headerGap,
            double sidePadding,
            double topPadding,
            double bottomPadding,
            double ringGap)
        {
            double headerStep = headerHeight + headerGap;
            for (int pass = 0; pass < 4; pass++)
            {
                var occupiedHeaders = new List<ComposeNetworkLayoutRect>();
                foreach (NetworkCluster cluster in clusters
                             .OrderByDescending(item => item.Members.Count)
                             .ThenBy(item => item.Order))
                {
                    foreach (ComposeNetworkLayoutGroup group in cluster.Groups
                                 .OrderByDescending(group => boundsByNetwork[group.Id].Width *
                                                              boundsByNetwork[group.Id].Height))
                    {
                        ComposeNetworkLayoutRect bounds = boundsByNetwork[group.Id];
                        ComposeNetworkLayoutRect header = HeaderRect(bounds, headerHeight);
                        int attempts = 0;
                        while (occupiedHeaders.Any(existing => existing.Intersects(header)) &&
                               attempts++ < 100)
                        {
                            bounds = bounds with
                            {
                                Y = bounds.Y - headerStep,
                                Height = bounds.Height + headerStep
                            };
                            header = HeaderRect(bounds, headerHeight);
                        }

                        boundsByNetwork[group.Id] = bounds;
                        occupiedHeaders.Add(header);
                    }
                }

                PropagateContainment(
                    clusters,
                    boundsByNetwork,
                    sidePadding,
                    topPadding,
                    bottomPadding,
                    ringGap);
            }
        }

        private static void PropagateContainment(
            IReadOnlyList<NetworkCluster> clusters,
            IDictionary<string, ComposeNetworkLayoutRect> boundsByNetwork,
            double sidePadding,
            double topPadding,
            double bottomPadding,
            double ringGap)
        {
            int Depth(NetworkCluster cluster)
            {
                int depth = 0;
                for (NetworkCluster? current = cluster.Parent;
                     current is not null;
                     current = current.Parent)
                {
                    depth++;
                }
                return depth;
            }

            foreach (NetworkCluster cluster in clusters
                         .OrderByDescending(Depth)
                         .ThenBy(cluster => cluster.Order))
            {
                if (cluster.Parent is null) continue;
                ComposeNetworkLayoutRect childOuter = cluster.Groups
                    .Select(group => boundsByNetwork[group.Id])
                    .Aggregate((left, right) => Union(new[] { left, right }));
                for (int ringIndex = 0;
                     ringIndex < cluster.Parent.Groups.Count;
                     ringIndex++)
                {
                    ComposeNetworkLayoutGroup parentGroup =
                        cluster.Parent.Groups[ringIndex];
                    ComposeNetworkLayoutRect parentBounds =
                        boundsByNetwork[parentGroup.Id];
                    double ringPadding = ringIndex * ringGap;
                    double requiredLeft =
                        childOuter.X - sidePadding - ringPadding;
                    double requiredTop =
                        childOuter.Y - topPadding - ringPadding;
                    double requiredRight =
                        childOuter.Right + sidePadding + ringPadding;
                    double requiredBottom =
                        childOuter.Bottom + bottomPadding + ringPadding;
                    double left = Math.Min(parentBounds.X, requiredLeft);
                    double top = Math.Min(parentBounds.Y, requiredTop);
                    double right = Math.Max(parentBounds.Right, requiredRight);
                    double bottom = Math.Max(parentBounds.Bottom, requiredBottom);
                    boundsByNetwork[parentGroup.Id] = new ComposeNetworkLayoutRect(
                        left,
                        top,
                        right - left,
                        bottom - top);
                }
            }
        }

        private static ComposeNetworkLayoutRect ExpandContent(
            ComposeNetworkLayoutRect content,
            double leftPadding,
            double topPadding,
            double rightPadding,
            double bottomPadding,
            double minimumWidth,
            double minimumHeight)
        {
            double desiredWidth = content.Width + leftPadding + rightPadding;
            double desiredHeight = content.Height + topPadding + bottomPadding;
            double width = Math.Max(minimumWidth, desiredWidth);
            double height = Math.Max(minimumHeight, desiredHeight);
            double extraWidth = width - desiredWidth;
            double extraHeight = height - desiredHeight;
            return new ComposeNetworkLayoutRect(
                content.X - leftPadding - (extraWidth / 2.0),
                content.Y - topPadding - (extraHeight / 2.0),
                width,
                height);
        }

        private static ComposeNetworkLayoutRect HeaderRect(
            ComposeNetworkLayoutRect bounds,
            double headerHeight) =>
            new(bounds.X, bounds.Y, Math.Min(bounds.Width, 190), headerHeight);

        private static ComposeNetworkLayoutRect ToRect(ComposeNetworkLayoutNode node) =>
            new(node.X, node.Y, node.Width, node.Height);

        private static ComposeNetworkLayoutRect Union(
            IEnumerable<ComposeNetworkLayoutRect> inputBounds)
        {
            ComposeNetworkLayoutRect[] bounds = inputBounds.ToArray();
            if (bounds.Length == 0)
                return new ComposeNetworkLayoutRect(0, 0, 1, 1);
            double left = bounds.Min(item => item.X);
            double top = bounds.Min(item => item.Y);
            double right = bounds.Max(item => item.Right);
            double bottom = bounds.Max(item => item.Bottom);
            return new ComposeNetworkLayoutRect(
                left,
                top,
                right - left,
                bottom - top);
        }

        private sealed class NetworkCluster
        {
            public NetworkCluster(HashSet<string> members, int order)
            {
                Members = members;
                Order = order;
            }

            public HashSet<string> Members { get; }
            public int Order { get; }
            public List<ComposeNetworkLayoutGroup> Groups { get; } = new();
            public List<NetworkCluster> Children { get; } = new();
            public NetworkCluster? Parent { get; set; }
            public ComposeNetworkLayoutRect? OuterBounds { get; set; }
        }
    }
}
