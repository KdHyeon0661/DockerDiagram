using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DockerDiagram.Diagram
{
    public enum ComposeNetworkRelationKind
    {
        Disjoint,
        Identical,
        LeftContainsRight,
        RightContainsLeft,
        PartialOverlap
    }

    public sealed class ComposeLayoutVertex
    {
        internal ComposeLayoutVertex(
            string id,
            NodeViewModel node,
            IReadOnlyList<string> parentIds,
            IReadOnlyList<string> childIds,
            int rank,
            int connectedComponentIndex,
            int stronglyConnectedComponentIndex,
            bool isInCycle,
            IReadOnlyList<string> ownedVolumeIds,
            IReadOnlyList<string> networkIds)
        {
            Id = id;
            Node = node;
            ParentIds = parentIds;
            ChildIds = childIds;
            Rank = rank;
            ConnectedComponentIndex = connectedComponentIndex;
            StronglyConnectedComponentIndex = stronglyConnectedComponentIndex;
            IsInCycle = isInCycle;
            OwnedVolumeIds = ownedVolumeIds;
            NetworkIds = networkIds;
        }

        public string Id { get; }
        public NodeViewModel Node { get; }
        public IReadOnlyList<string> ParentIds { get; }
        public IReadOnlyList<string> ChildIds { get; }
        public int Rank { get; }
        public int InDegree => ParentIds.Count;
        public int OutDegree => ChildIds.Count;
        public int ConnectedComponentIndex { get; }
        public int StronglyConnectedComponentIndex { get; }
        public bool IsInCycle { get; }
        public IReadOnlyList<string> OwnedVolumeIds { get; }
        public IReadOnlyList<string> NetworkIds { get; }
    }

    public sealed class ComposeLayoutVolume
    {
        internal ComposeLayoutVolume(
            string id,
            NodeViewModel node,
            IReadOnlyList<string> ownerIds)
        {
            Id = id;
            Node = node;
            OwnerIds = ownerIds;
        }

        public string Id { get; }
        public NodeViewModel Node { get; }
        public IReadOnlyList<string> OwnerIds { get; }
        public bool IsShared => OwnerIds.Count > 1;
        public bool IsOrphan => OwnerIds.Count == 0;
    }

    public sealed class ComposeLayoutNetwork
    {
        internal ComposeLayoutNetwork(
            string id,
            GroupViewModel group,
            IReadOnlyList<string> memberIds)
        {
            Id = id;
            Group = group;
            MemberIds = memberIds;
        }

        public string Id { get; }
        public string Name => Group.Title;
        public GroupViewModel Group { get; }
        public IReadOnlyList<string> MemberIds { get; }
    }

    public sealed class ComposeLayoutNetworkRelation
    {
        internal ComposeLayoutNetworkRelation(
            string leftNetworkId,
            string rightNetworkId,
            ComposeNetworkRelationKind kind,
            IReadOnlyList<string> sharedMemberIds)
        {
            LeftNetworkId = leftNetworkId;
            RightNetworkId = rightNetworkId;
            Kind = kind;
            SharedMemberIds = sharedMemberIds;
        }

        public string LeftNetworkId { get; }
        public string RightNetworkId { get; }
        public ComposeNetworkRelationKind Kind { get; }
        public IReadOnlyList<string> SharedMemberIds { get; }
        public bool Overlaps => Kind != ComposeNetworkRelationKind.Disjoint;
    }

    public sealed class ComposeLayoutGraph
    {
        internal ComposeLayoutGraph(
            ComposeGraphTopology topology,
            IReadOnlyDictionary<string, ComposeLayoutVertex> vertices,
            IReadOnlyList<ComposeLayoutVolume> volumes,
            IReadOnlyList<ComposeLayoutNetwork> networks,
            IReadOnlyList<ComposeLayoutNetworkRelation> networkRelations,
            IReadOnlyList<string> diagnostics)
        {
            Topology = topology;
            Vertices = vertices;
            Volumes = volumes;
            Networks = networks;
            NetworkRelations = networkRelations;
            Diagnostics = diagnostics;
        }

        public ComposeGraphTopology Topology { get; }
        public ComposeLayoutGraphKind Kind => Topology.Kind;
        public IReadOnlyDictionary<string, ComposeLayoutVertex> Vertices { get; }
        public IReadOnlyList<ComposeLayoutVolume> Volumes { get; }
        public IReadOnlyList<ComposeLayoutNetwork> Networks { get; }
        public IReadOnlyList<ComposeLayoutNetworkRelation> NetworkRelations { get; }
        public IReadOnlyList<string> Diagnostics { get; }
        public bool HasOverlappingNetworks =>
            NetworkRelations.Any(relation => relation.Kind != ComposeNetworkRelationKind.Disjoint);
    }

    /// <summary>
    /// Compose 화면 객체를 후속 배치 알고리즘이 소비할 수 있는 불변 분석 모델로 변환합니다.
    /// 이 단계에서는 화면 좌표를 변경하지 않습니다.
    /// </summary>
    public static class ComposeLayoutGraphAnalyzer
    {
        public static ComposeLayoutGraph Analyze(
            SheetViewModel sheet,
            IReadOnlyDictionary<string, NodeViewModel> serviceNodes,
            IReadOnlyDictionary<string, List<string>> dependsOnByService,
            IReadOnlyCollection<NodeViewModel>? scopedVolumes = null,
            IReadOnlyCollection<GroupViewModel>? scopedGroups = null)
        {
            ArgumentNullException.ThrowIfNull(sheet);
            ArgumentNullException.ThrowIfNull(serviceNodes);
            ArgumentNullException.ThrowIfNull(dependsOnByService);

            var diagnostics = new List<string>();
            var orderedServiceIds = serviceNodes.Keys
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var serviceIdSet = new HashSet<string>(orderedServiceIds, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<ComposeLayoutEdge> dependencyEdges = BuildDisplayDependencyEdges(
                orderedServiceIds,
                dependsOnByService,
                diagnostics);

            ComposeGraphTopology topology = ComposeGraphTopologyAnalyzer.Analyze(
                orderedServiceIds,
                dependencyEdges);
            var serviceIdByNode = serviceNodes
                .Where(pair => serviceIdSet.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Value,
                    pair => pair.Key,
                    ReferenceComparer<NodeViewModel>.Instance);
            var serviceOrder = orderedServiceIds
                .Select((id, index) => (id, index))
                .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);

            IReadOnlyCollection<NodeViewModel> volumeNodes =
                scopedVolumes ?? sheet.Nodes.Where(node => node.Type == NodeType.Volume).ToList();
            var volumeIdByNode = BuildUniqueIds(
                volumeNodes.Where(node => node.Type == NodeType.Volume),
                node => node.Id,
                "volume");
            var volumeOwners = volumeIdByNode.Values.ToDictionary(
                id => id,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            foreach (ConnectorViewModel connector in sheet.Connectors
                         .Where(item => item.RelationType == RelationType.VolumeMount))
            {
                if (connector.Source is not NodeViewModel sourceNode ||
                    connector.Target is not NodeViewModel targetNode)
                {
                    continue;
                }

                NodeViewModel? volumeNode = null;
                NodeViewModel? ownerNode = null;
                if (volumeIdByNode.ContainsKey(sourceNode) && serviceIdByNode.ContainsKey(targetNode))
                {
                    volumeNode = sourceNode;
                    ownerNode = targetNode;
                }
                else if (volumeIdByNode.ContainsKey(targetNode) && serviceIdByNode.ContainsKey(sourceNode))
                {
                    volumeNode = targetNode;
                    ownerNode = sourceNode;
                }

                if (volumeNode is null || ownerNode is null) continue;
                volumeOwners[volumeIdByNode[volumeNode]].Add(serviceIdByNode[ownerNode]);
            }

            var volumes = volumeIdByNode
                .Select(pair => new ComposeLayoutVolume(
                    pair.Value,
                    pair.Key,
                    volumeOwners[pair.Value]
                        .OrderBy(id => serviceOrder[id])
                        .ToArray()))
                .ToArray();

            IReadOnlyCollection<GroupViewModel> groupNodes = scopedGroups ?? sheet.Groups.ToList();
            var networkGroups = groupNodes.Where(group => group.Type == GroupType.Network).ToArray();
            var networkIdByGroup = BuildUniqueIds(networkGroups, group => group.Id, "network");
            var networks = networkIdByGroup
                .Select(pair => new ComposeLayoutNetwork(
                    pair.Value,
                    pair.Key,
                    pair.Key.ContainedNodes
                        .Where(serviceIdByNode.ContainsKey)
                        .Select(node => serviceIdByNode[node])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(id => serviceOrder[id])
                        .ToArray()))
                .ToArray();

            var networkRelations = new List<ComposeLayoutNetworkRelation>();
            for (int leftIndex = 0; leftIndex < networks.Length; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < networks.Length; rightIndex++)
                {
                    ComposeLayoutNetwork left = networks[leftIndex];
                    ComposeLayoutNetwork right = networks[rightIndex];
                    var leftMembers = new HashSet<string>(left.MemberIds, StringComparer.OrdinalIgnoreCase);
                    var rightMembers = new HashSet<string>(right.MemberIds, StringComparer.OrdinalIgnoreCase);
                    string[] sharedMembers = leftMembers
                        .Intersect(rightMembers, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(id => serviceOrder[id])
                        .ToArray();
                    networkRelations.Add(new ComposeLayoutNetworkRelation(
                        left.Id,
                        right.Id,
                        ClassifyNetworkRelation(leftMembers, rightMembers),
                        sharedMembers));
                }
            }

            var ownedVolumesByService = orderedServiceIds.ToDictionary(
                id => id,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (ComposeLayoutVolume volume in volumes)
            {
                foreach (string ownerId in volume.OwnerIds)
                    ownedVolumesByService[ownerId].Add(volume.Id);
            }

            var networksByService = orderedServiceIds.ToDictionary(
                id => id,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (ComposeLayoutNetwork network in networks)
            {
                foreach (string memberId in network.MemberIds)
                    networksByService[memberId].Add(network.Id);
            }

            var vertices = orderedServiceIds.ToDictionary(
                id => id,
                id => new ComposeLayoutVertex(
                    id,
                    serviceNodes[id],
                    topology.ParentsByNode[id],
                    topology.ChildrenByNode[id],
                    topology.RankByNode[id],
                    topology.ConnectedComponentByNode[id],
                    topology.StronglyConnectedComponentByNode[id],
                    topology.CycleNodeIds.Contains(id),
                    ownedVolumesByService[id].ToArray(),
                    networksByService[id].ToArray()),
                StringComparer.OrdinalIgnoreCase);

            return new ComposeLayoutGraph(
                topology,
                vertices,
                volumes,
                networks,
                networkRelations.ToArray(),
                diagnostics.ToArray());
        }

        public static ComposeNetworkRelationKind ClassifyNetworkRelation(
            IEnumerable<string> leftMemberIds,
            IEnumerable<string> rightMemberIds)
        {
            ArgumentNullException.ThrowIfNull(leftMemberIds);
            ArgumentNullException.ThrowIfNull(rightMemberIds);

            var left = new HashSet<string>(leftMemberIds, StringComparer.OrdinalIgnoreCase);
            var right = new HashSet<string>(rightMemberIds, StringComparer.OrdinalIgnoreCase);

            if (left.SetEquals(right)) return ComposeNetworkRelationKind.Identical;
            if (left.IsSupersetOf(right)) return ComposeNetworkRelationKind.LeftContainsRight;
            if (right.IsSupersetOf(left)) return ComposeNetworkRelationKind.RightContainsLeft;
            if (left.Overlaps(right)) return ComposeNetworkRelationKind.PartialOverlap;
            return ComposeNetworkRelationKind.Disjoint;
        }

        public static IReadOnlyList<ComposeLayoutEdge> CreateDisplayDependencyEdges(
            IEnumerable<string> serviceIds,
            IReadOnlyDictionary<string, List<string>> dependsOnByService)
        {
            ArgumentNullException.ThrowIfNull(serviceIds);
            ArgumentNullException.ThrowIfNull(dependsOnByService);
            return BuildDisplayDependencyEdges(serviceIds, dependsOnByService, diagnostics: null);
        }

        private static IReadOnlyList<ComposeLayoutEdge> BuildDisplayDependencyEdges(
            IEnumerable<string> serviceIds,
            IReadOnlyDictionary<string, List<string>> dependsOnByService,
            ICollection<string>? diagnostics)
        {
            var serviceIdSet = new HashSet<string>(
                serviceIds.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            var edges = new List<ComposeLayoutEdge>();
            foreach ((string dependentId, List<string> dependencies) in dependsOnByService)
            {
                if (!serviceIdSet.Contains(dependentId))
                {
                    diagnostics?.Add($"알 수 없는 dependent 서비스: {dependentId}");
                    continue;
                }

                foreach (string dependencyId in dependencies ?? Enumerable.Empty<string>())
                {
                    if (!serviceIdSet.Contains(dependencyId))
                    {
                        diagnostics?.Add($"{dependentId}의 알 수 없는 dependency: {dependencyId}");
                        continue;
                    }

                    // 실행 의미는 dependent가 dependency를 기다리는 관계 그대로 유지합니다.
                    // 화면에서는 사용자가 읽는 요청 흐름인 dependent -> dependency로 배치합니다.
                    edges.Add(new ComposeLayoutEdge(dependentId, dependencyId));
                }
            }

            return edges;
        }

        private static Dictionary<T, string> BuildUniqueIds<T>(
            IEnumerable<T> items,
            Func<T, string> preferredId,
            string fallbackPrefix)
            where T : class
        {
            var result = new Dictionary<T, string>(ReferenceComparer<T>.Instance);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int fallbackIndex = 1;

            foreach (T item in items.Distinct(ReferenceComparer<T>.Instance))
            {
                string baseId = preferredId(item)?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(baseId)) baseId = $"{fallbackPrefix}-{fallbackIndex++}";

                string uniqueId = baseId;
                int suffix = 2;
                while (!usedIds.Add(uniqueId)) uniqueId = $"{baseId}#{suffix++}";
                result[item] = uniqueId;
            }

            return result;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static ReferenceComparer<T> Instance { get; } = new();

            public bool Equals(T? left, T? right) => ReferenceEquals(left, right);

            public int GetHashCode(T item) => RuntimeHelpers.GetHashCode(item);
        }
    }
}
