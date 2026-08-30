using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DockerDiagram.Diagram
{
    public static class ComposeDiagramLayoutService
    {
        private const double HorizontalGap = 90;
        private const double VerticalGap = 35;
        private const double ForestGap = 70;
        private const double GroupSidePadding = 28;
        private const double GroupTopPadding = 48;
        private const double GroupBottomPadding = 28;
        private const double CoincidentGroupGap = 28;
        private const double GroupHeaderHeight = 24;

        public static void Arrange(
            SheetViewModel sheet,
            IReadOnlyDictionary<string, NodeViewModel> serviceNodes,
            IReadOnlyDictionary<string, List<string>> dependsOnByService,
            double originX,
            double originY,
            IReadOnlyCollection<NodeViewModel>? scopedVolumes = null,
            IReadOnlyCollection<GroupViewModel>? scopedGroups = null,
            ComposeLayoutOptions? layoutOptions = null)
        {
            using IDisposable layoutUpdate = sheet.BeginLayoutUpdate();
            NormalizeDisplayDependencyConnectors(sheet, serviceNodes, dependsOnByService);
            ComposeLayoutOptions options = layoutOptions ?? new ComposeLayoutOptions();
            ComposeLayoutGraph layoutGraph = ComposeLayoutGraphAnalyzer.Analyze(
                sheet,
                serviceNodes,
                dependsOnByService,
                scopedVolumes,
                scopedGroups);
            bool isHorizontal = options.Direction == ComposeLayoutDirection.LeftToRight;
            double horizontalGap = options.HorizontalGap;
            double verticalGap = options.VerticalGap;
            if (options.UseAdaptiveSpacing)
            {
                horizontalGap += Math.Min(55, Math.Sqrt(serviceNodes.Count) * 5);
                verticalGap += Math.Min(25, Math.Sqrt(serviceNodes.Count) * 2);
            }

            if (serviceNodes.Count == 0)
            {
                IReadOnlyCollection<NodeViewModel> volumesWithoutServices =
                    scopedVolumes ?? sheet.Nodes.Where(node => node.Type == NodeType.Volume).ToList();
                IReadOnlyCollection<GroupViewModel> groupsWithoutServices = scopedGroups ?? sheet.Groups.ToList();
                ArrangeVolumes(sheet, volumesWithoutServices, originX, originY, isHorizontal ? originY : originX, isHorizontal, horizontalGap, verticalGap);
                AvoidExistingNodeCollisions(sheet, volumesWithoutServices, isHorizontal);
                ResizeGroupsRecursively(groupsWithoutServices);
                AvoidVolumeGroupCollisions(
                    sheet,
                    volumesWithoutServices,
                    groupsWithoutServices,
                    isHorizontal,
                    horizontalGap,
                    verticalGap);
                ArrangeEmptyGroups(groupsWithoutServices, volumesWithoutServices, originX, originY, isHorizontal);
                FinalizePlacement(sheet, volumesWithoutServices, groupsWithoutServices, isHorizontal);
                sheet.UpdateGroupLayering();
                return;
            }

            var nodes = serviceNodes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            double depthGap = isHorizontal ? horizontalGap : verticalGap;
            double siblingGap = isHorizontal ? verticalGap : horizontalGap;
            double depthOrigin = isHorizontal ? originX : originY;
            double siblingOrigin = isHorizontal ? originY : originX;
            IReadOnlyDictionary<string, int> serviceRanks = GetServiceRanks(
                layoutGraph.Topology,
                nodes,
                depthOrigin,
                siblingOrigin,
                depthGap,
                siblingGap,
                isHorizontal);
            var serviceBreadthSizes = nodes.ToDictionary(
                pair => pair.Key,
                pair => Math.Max(1, isHorizontal ? pair.Value.Height : pair.Value.Width),
                StringComparer.OrdinalIgnoreCase);
            ComposeVolumeLayoutItem[] volumeItems = layoutGraph.Volumes
                .Select(volume => new ComposeVolumeLayoutItem(
                    volume.Id,
                    isHorizontal ? volume.Node.Width : volume.Node.Height,
                    isHorizontal ? volume.Node.Height : volume.Node.Width,
                    volume.OwnerIds))
                .ToArray();
            IReadOnlyDictionary<string, IReadOnlySet<string>> descendantsByService =
                BuildDescendantMap(layoutGraph.Topology);
            var inlineVolumeIds = new HashSet<string>(
                volumeItems.Where(volume => volume.OwnerIds.Count > 0).Select(volume => volume.Id),
                StringComparer.OrdinalIgnoreCase);

            (ComposeVolumeLayoutResult Layout, double ForestEnd)
                RunVolumePass(IReadOnlySet<string> passInlineVolumeIds)
            {
                ComposeVolumeLayoutPlan passPlan = ComposeVolumeLayoutEngine.CreatePlan(
                    layoutGraph.Topology.OrderedNodeIds,
                    serviceRanks,
                    serviceBreadthSizes,
                    volumeItems,
                    Math.Max(18, siblingGap * 0.45),
                    passInlineVolumeIds,
                    layoutGraph.Topology.ConnectedComponentByNode);
                double passForestEnd = layoutGraph.Kind switch
                {
                    ComposeLayoutGraphKind.Tree or ComposeLayoutGraphKind.Forest =>
                        ArrangeTidyTreeServices(
                            layoutGraph.Topology,
                            nodes,
                            depthOrigin,
                            siblingOrigin,
                            depthGap,
                            siblingGap,
                            passPlan.ReservedBreadthByService,
                            isHorizontal),
                    ComposeLayoutGraphKind.DirectedAcyclicGraph or ComposeLayoutGraphKind.Cyclic =>
                        ArrangeSugiyamaServices(
                            layoutGraph.Topology,
                            nodes,
                            depthOrigin,
                            siblingOrigin,
                            depthGap,
                            siblingGap,
                            passPlan.ReservedBreadthByService,
                            isHorizontal),
                    _ => ArrangeLegacyServices(
                        layoutGraph.Topology.OrderedNodeIds,
                        dependsOnByService,
                        nodes,
                        depthOrigin,
                        siblingOrigin,
                        depthGap,
                        siblingGap,
                        isHorizontal)
                };

                ComposeVolumeServiceSlot[] initialSlots = layoutGraph.Topology.OrderedNodeIds
                    .Select(nodeId =>
                    {
                        NodeViewModel node = nodes[nodeId];
                        return new ComposeVolumeServiceSlot(
                            nodeId,
                            serviceRanks[nodeId],
                            isHorizontal ? node.X : node.Y,
                            isHorizontal ? node.Y : node.X,
                            isHorizontal ? node.Width : node.Height,
                            isHorizontal ? node.Height : node.Width);
                    })
                    .ToArray();
                ComposeVolumeLayoutResult passLayout = ComposeVolumeLayoutEngine.Arrange(
                    passPlan,
                    initialSlots,
                    depthOrigin,
                    siblingOrigin,
                    Math.Max(28, depthGap * 0.30),
                    Math.Max(28, depthGap * 0.30),
                    Math.Max(30, Math.Min(55, depthGap * 0.35)),
                    ForestGap + siblingGap);
                if (!layoutGraph.Networks.Any(network => network.MemberIds.Count > 0) ||
                    layoutGraph.Volumes.Count == 0)
                {
                    return (passLayout, passForestEnd);
                }

                ComposeVolumeServiceSlot[] shiftedSlots = initialSlots
                    .Select(service =>
                    {
                        ComposeVolumeAxisPosition position = passLayout.ServicePositions[service.Id];
                        return service with { Depth = position.Depth, Breadth = position.Breadth };
                    })
                    .ToArray();
                ComposeNetworkLayoutResult previewNetworks = ComposeNetworkLayoutEngine.Arrange(
                    shiftedSlots.Select(service => new ComposeNetworkLayoutNode(
                        service.Id,
                        isHorizontal ? service.Depth : service.Breadth,
                        isHorizontal ? service.Breadth : service.Depth,
                        isHorizontal ? service.DepthSize : service.BreadthSize,
                        isHorizontal ? service.BreadthSize : service.DepthSize)),
                    layoutGraph.Networks.Select(network => new ComposeNetworkLayoutGroup(
                        network.Id,
                        network.Name,
                        network.MemberIds)),
                    GroupSidePadding,
                    GroupTopPadding,
                    GroupBottomPadding,
                    CoincidentGroupGap,
                    GroupHeaderHeight,
                    headerGap: 4,
                    GroupViewModel.MinimumWidth,
                    GroupViewModel.MinimumHeight);
                var networkById = layoutGraph.Networks.ToDictionary(
                    network => network.Id,
                    StringComparer.OrdinalIgnoreCase);
                ComposeVolumeNetworkRegion[] regions = previewNetworks.BoundsByNetwork
                    .Select(pair =>
                    {
                        ComposeNetworkLayoutRect bounds = pair.Value;
                        ComposeLayoutNetwork network = networkById[pair.Key];
                        return new ComposeVolumeNetworkRegion(
                            pair.Key,
                            isHorizontal ? bounds.X : bounds.Y,
                            isHorizontal ? bounds.Y : bounds.X,
                            isHorizontal ? bounds.Width : bounds.Height,
                            isHorizontal ? bounds.Height : bounds.Width,
                            network.MemberIds);
                    })
                    .ToArray();
                passLayout = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
                    passPlan,
                    shiftedSlots,
                    passLayout,
                    regions,
                    descendantsByService,
                    ForestGap + siblingGap);
                return (passLayout, passForestEnd);
            }

            var pass = RunVolumePass(inlineVolumeIds);

            ComposeVolumeLayoutResult volumeLayout = pass.Layout;
            double forestEnd = pass.ForestEnd;
            foreach ((string nodeId, ComposeVolumeAxisPosition position) in volumeLayout.ServicePositions)
            {
                NodeViewModel node = nodes[nodeId];
                node.X = isHorizontal ? position.Depth : position.Breadth;
                node.Y = isHorizontal ? position.Breadth : position.Depth;
            }

            var volumeById = layoutGraph.Volumes.ToDictionary(
                volume => volume.Id,
                volume => volume.Node,
                StringComparer.OrdinalIgnoreCase);
            foreach ((string volumeId, ComposeVolumeAxisPosition position) in volumeLayout.VolumePositions)
            {
                NodeViewModel volume = volumeById[volumeId];
                volume.X = isHorizontal ? position.Depth : position.Breadth;
                volume.Y = isHorizontal ? position.Breadth : position.Depth;
            }

            forestEnd = Math.Max(forestEnd, volumeLayout.BreadthEnd);
            IReadOnlyCollection<NodeViewModel> volumes = layoutGraph.Volumes
                .Select(volume => volume.Node)
                .ToList();
            AvoidExistingNodeCollisions(sheet, serviceNodes.Values.Concat(volumes).ToList(), isHorizontal);
            IReadOnlyCollection<GroupViewModel> groups = scopedGroups ?? sheet.Groups.ToList();
            ResizeComposeGroups(layoutGraph, groups);
            UpdateNetworkLayoutAttachments(layoutGraph, volumeLayout);
            IReadOnlyCollection<NodeViewModel> arrangedItems = serviceNodes.Values.Concat(volumes).Distinct().ToList();
            ArrangeEmptyGroups(groups, arrangedItems, originX, originY, isHorizontal);
            FinalizePlacement(sheet, arrangedItems, groups, isHorizontal);
            sheet.UpdateGroupLayering();
        }

        private static void NormalizeDisplayDependencyConnectors(
            SheetViewModel sheet,
            IReadOnlyDictionary<string, NodeViewModel> serviceNodes,
            IReadOnlyDictionary<string, List<string>> dependsOnByService)
        {
            foreach ((string dependentId, List<string> dependencyIds) in dependsOnByService)
            {
                if (!serviceNodes.TryGetValue(dependentId, out NodeViewModel? dependent)) continue;
                foreach (string dependencyId in dependencyIds)
                {
                    if (!serviceNodes.TryGetValue(dependencyId, out NodeViewModel? dependency)) continue;
                    ConnectorViewModel? connector = sheet.Connectors.FirstOrDefault(item =>
                        item.RelationType == RelationType.Dependency &&
                        ((ReferenceEquals(item.Source, dependent) && ReferenceEquals(item.Target, dependency)) ||
                         (ReferenceEquals(item.Source, dependency) && ReferenceEquals(item.Target, dependent))));
                    if (connector is null || ReferenceEquals(connector.Source, dependent)) continue;

                    connector.UpdateConnection(
                        dependent,
                        PortDirection.Right,
                        dependency,
                        PortDirection.Left);
                }
            }
        }

        private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildDescendantMap(
            ComposeGraphTopology topology)
        {
            var result = new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string nodeId in topology.OrderedNodeIds)
            {
                var descendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pending = new Stack<string>(topology.ChildrenByNode[nodeId].Reverse());
                while (pending.Count > 0)
                {
                    string childId = pending.Pop();
                    if (!descendants.Add(childId)) continue;
                    foreach (string grandchildId in topology.ChildrenByNode[childId].Reverse())
                        pending.Push(grandchildId);
                }

                descendants.Remove(nodeId);
                result[nodeId] = descendants;
            }

            return result;
        }

        private static void UpdateNetworkLayoutAttachments(
            ComposeLayoutGraph layoutGraph,
            ComposeVolumeLayoutResult volumeLayout)
        {
            var networkById = layoutGraph.Networks.ToDictionary(
                network => network.Id,
                StringComparer.OrdinalIgnoreCase);
            var attachedByGroup = new Dictionary<GroupViewModel, HashSet<NodeViewModel>>(
                ReferenceEqualityComparer.Instance);
            foreach (ComposeLayoutNetwork network in layoutGraph.Networks)
            {
                attachedByGroup.TryAdd(
                    network.Group,
                    new HashSet<NodeViewModel>(ReferenceEqualityComparer.Instance));
            }
            var volumeById = layoutGraph.Volumes.ToDictionary(
                volume => volume.Id,
                volume => volume.Node,
                StringComparer.OrdinalIgnoreCase);

            foreach ((string volumeId, IReadOnlyList<string> networkIds) in
                     volumeLayout.InternalNetworkIdsByVolume)
            {
                if (volumeLayout.ExternalVolumeIds.Contains(volumeId) ||
                    !volumeById.TryGetValue(volumeId, out NodeViewModel? volume))
                {
                    continue;
                }

                foreach (string networkId in networkIds)
                {
                    if (networkById.TryGetValue(networkId, out ComposeLayoutNetwork? network))
                        attachedByGroup[network.Group].Add(volume);
                }
            }

            foreach ((GroupViewModel group, HashSet<NodeViewModel> attachedNodes) in attachedByGroup)
                group.SetLayoutAttachedNodes(attachedNodes);
        }

        private static IReadOnlyDictionary<string, int> GetServiceRanks(
            ComposeGraphTopology topology,
            IReadOnlyDictionary<string, NodeViewModel> nodes,
            double depthOrigin,
            double siblingOrigin,
            double depthGap,
            double siblingGap,
            bool isHorizontal)
        {
            if (topology.Kind is ComposeLayoutGraphKind.Tree or ComposeLayoutGraphKind.Forest)
                return topology.RankByNode;
            if (topology.Kind is not (
                    ComposeLayoutGraphKind.DirectedAcyclicGraph or
                    ComposeLayoutGraphKind.Cyclic))
            {
                return topology.RankByNode;
            }

            ComposeSugiyamaLayoutResult probe = ComposeSugiyamaLayoutEngine.Arrange(
                topology,
                topology.OrderedNodeIds.Select(nodeId =>
                {
                    NodeViewModel node = nodes[nodeId];
                    return new ComposeSugiyamaNode(
                        nodeId,
                        isHorizontal ? node.Width : node.Height,
                        isHorizontal ? node.Height : node.Width);
                }),
                depthOrigin,
                siblingOrigin,
                depthGap,
                siblingGap);
            return probe.LayerByNode;
        }

        private static double ArrangeTidyTreeServices(
            ComposeGraphTopology topology,
            IReadOnlyDictionary<string, NodeViewModel> nodes,
            double depthOrigin,
            double siblingOrigin,
            double depthGap,
            double siblingGap,
            IReadOnlyDictionary<string, double> reservedBreadthByService,
            bool isHorizontal)
        {
            ComposeTidyTreeLayoutResult result = ComposeTidyTreeLayoutEngine.Arrange(
                topology,
                topology.OrderedNodeIds.Select(nodeId =>
                {
                    NodeViewModel node = nodes[nodeId];
                    return new ComposeTidyTreeNode(
                        nodeId,
                        isHorizontal ? node.Width : node.Height,
                        reservedBreadthByService[nodeId]);
                }),
                depthOrigin,
                siblingOrigin,
                depthGap,
                siblingGap,
                ForestGap + siblingGap);

            foreach ((string nodeId, ComposeTidyTreePosition position) in result.Positions)
            {
                NodeViewModel node = nodes[nodeId];
                node.X = isHorizontal ? position.Depth : position.Breadth;
                node.Y = isHorizontal ? position.Breadth : position.Depth;
            }

            return result.BreadthEnd;
        }

        private static double ArrangeSugiyamaServices(
            ComposeGraphTopology topology,
            IReadOnlyDictionary<string, NodeViewModel> nodes,
            double depthOrigin,
            double siblingOrigin,
            double depthGap,
            double siblingGap,
            IReadOnlyDictionary<string, double> reservedBreadthByService,
            bool isHorizontal)
        {
            ComposeSugiyamaLayoutResult result = ComposeSugiyamaLayoutEngine.Arrange(
                topology,
                topology.OrderedNodeIds.Select(nodeId =>
                {
                    NodeViewModel node = nodes[nodeId];
                    return new ComposeSugiyamaNode(
                        nodeId,
                        isHorizontal ? node.Width : node.Height,
                        reservedBreadthByService[nodeId]);
                }),
                depthOrigin,
                siblingOrigin,
                depthGap,
                siblingGap);

            foreach ((string nodeId, ComposeSugiyamaPosition position) in result.Positions)
            {
                NodeViewModel node = nodes[nodeId];
                node.X = isHorizontal ? position.Depth : position.Breadth;
                node.Y = isHorizontal ? position.Breadth : position.Depth;
            }

            return result.BreadthEnd;
        }

        private static double ArrangeLegacyServices(
            IReadOnlyList<string> orderedServices,
            IReadOnlyDictionary<string, List<string>> dependsOnByService,
            IReadOnlyDictionary<string, NodeViewModel> nodes,
            double depthOrigin,
            double siblingOrigin,
            double depthGap,
            double siblingGap,
            bool isHorizontal)
        {
            var orderIndex = orderedServices
                .Select((name, index) => (name, index))
                .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
            var primaryParent = BuildPrimaryParentMap(orderedServices, dependsOnByService, nodes);
            var children = orderedServices.ToDictionary(
                name => name,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var pair in primaryParent)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    children[pair.Value!].Add(pair.Key);
            }

            var depthCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int GetSubtreeDepth(string service)
            {
                if (depthCache.TryGetValue(service, out int cached)) return cached;
                int depth = children[service].Count == 0
                    ? 1
                    : 1 + children[service].Max(GetSubtreeDepth);
                depthCache[service] = depth;
                return depth;
            }

            foreach (List<string> childList in children.Values)
            {
                childList.Sort((left, right) =>
                {
                    int depthComparison = GetSubtreeDepth(right).CompareTo(GetSubtreeDepth(left));
                    return depthComparison != 0
                        ? depthComparison
                        : orderIndex[left].CompareTo(orderIndex[right]);
                });
            }

            var roots = orderedServices
                .Where(service =>
                    !primaryParent.TryGetValue(service, out string? parent) ||
                    string.IsNullOrWhiteSpace(parent))
                .OrderByDescending(GetSubtreeDepth)
                .ThenBy(service => orderIndex[service])
                .ToList();
            var levelSizes = new Dictionary<int, double>();

            void MeasureLevels(string service, int depth)
            {
                NodeViewModel node = nodes[service];
                double depthSize = Math.Max(1, isHorizontal ? node.Width : node.Height);
                levelSizes[depth] = Math.Max(levelSizes.GetValueOrDefault(depth), depthSize);
                foreach (string child in children[service]) MeasureLevels(child, depth + 1);
            }

            foreach (string root in roots) MeasureLevels(root, 0);

            var levelPositions = new Dictionary<int, double> { [0] = depthOrigin };
            for (int depth = 1; depth <= levelSizes.Keys.DefaultIfEmpty(0).Max(); depth++)
            {
                levelPositions[depth] =
                    levelPositions[depth - 1] +
                    levelSizes.GetValueOrDefault(depth - 1, 80) +
                    depthGap;
            }

            double LayoutTree(string service, int depth, double siblingStart)
            {
                NodeViewModel node = nodes[service];
                node.X = isHorizontal ? levelPositions[depth] : siblingStart;
                node.Y = isHorizontal ? siblingStart : levelPositions[depth];

                double siblingSize = Math.Max(1, isHorizontal ? node.Height : node.Width);
                double subtreeEnd = siblingStart + siblingSize;
                bool isFirstChild = true;
                foreach (string child in children[service])
                {
                    double childStart = isFirstChild ? siblingStart : subtreeEnd + siblingGap;
                    subtreeEnd = Math.Max(
                        subtreeEnd,
                        LayoutTree(child, depth + 1, childStart));
                    isFirstChild = false;
                }

                return subtreeEnd;
            }

            double forestEnd = siblingOrigin;
            bool isFirstRoot = true;
            foreach (string root in roots)
            {
                double rootStart = isFirstRoot
                    ? siblingOrigin
                    : forestEnd + ForestGap + siblingGap;
                forestEnd = Math.Max(forestEnd, LayoutTree(root, 0, rootStart));
                isFirstRoot = false;
            }

            return forestEnd;
        }

        private static void ResizeComposeGroups(
            ComposeLayoutGraph layoutGraph,
            IReadOnlyCollection<GroupViewModel> scopedGroups)
        {
            ComposeLayoutNetwork[] networkGroups = layoutGraph.Networks
                .Where(network => network.MemberIds.Count > 0)
                .ToArray();
            var handledGroups = new HashSet<GroupViewModel>(
                ReferenceEqualityComparer.Instance);
            if (networkGroups.Length > 0)
            {
                ComposeNetworkLayoutResult result = ComposeNetworkLayoutEngine.Arrange(
                    layoutGraph.Vertices.Values.Select(vertex => new ComposeNetworkLayoutNode(
                        vertex.Id,
                        vertex.Node.X,
                        vertex.Node.Y,
                        vertex.Node.Width,
                        vertex.Node.Height)),
                    networkGroups.Select(network => new ComposeNetworkLayoutGroup(
                        network.Id,
                        network.Name,
                        network.MemberIds)),
                    GroupSidePadding,
                    GroupTopPadding,
                    GroupBottomPadding,
                    CoincidentGroupGap,
                    GroupHeaderHeight,
                    headerGap: 4,
                    GroupViewModel.MinimumWidth,
                    GroupViewModel.MinimumHeight);
                var networkById = networkGroups.ToDictionary(
                    network => network.Id,
                    StringComparer.OrdinalIgnoreCase);
                foreach ((string networkId, ComposeNetworkLayoutRect bounds) in result.BoundsByNetwork)
                {
                    GroupViewModel group = networkById[networkId].Group;
                    group.X = bounds.X;
                    group.Y = bounds.Y;
                    group.Width = bounds.Width;
                    group.Height = bounds.Height;
                    handledGroups.Add(group);
                }
            }

            GroupViewModel[] legacyGroups = scopedGroups
                .Where(group => !handledGroups.Contains(group))
                .ToArray();
            ResizeGroupsRecursively(legacyGroups);
        }

        private static Dictionary<string, string?> BuildPrimaryParentMap(
            IReadOnlyList<string> orderedServices,
            IReadOnlyDictionary<string, List<string>> dependsOnByService,
            IReadOnlyDictionary<string, NodeViewModel> serviceNodes)
        {
            var parentMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var dependencyDepthCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int GetDependencyDepth(string service, HashSet<string> visiting)
            {
                if (dependencyDepthCache.TryGetValue(service, out int cached)) return cached;
                if (!visiting.Add(service)) return 0;

                int depth = 1;
                if (dependsOnByService.TryGetValue(service, out List<string>? dependencies))
                {
                    foreach (string dependency in dependencies.Where(serviceNodes.ContainsKey))
                        depth = Math.Max(depth, 1 + GetDependencyDepth(dependency, visiting));
                }

                visiting.Remove(service);
                dependencyDepthCache[service] = depth;
                return depth;
            }

            foreach (string service in orderedServices)
            {
                parentMap[service] = null;
                if (!dependsOnByService.TryGetValue(service, out List<string>? dependencies)) continue;

                var rankedDependencies = dependencies
                    .Where(serviceNodes.ContainsKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select((dependency, index) => new
                    {
                        Name = dependency,
                        Index = index,
                        Depth = GetDependencyDepth(dependency, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    })
                    .OrderByDescending(item => item.Depth)
                    .ThenBy(item => item.Index);

                foreach (var dependency in rankedDependencies)
                {
                    if (string.Equals(service, dependency.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (WouldCreateCycle(service, dependency.Name, parentMap)) continue;

                    parentMap[service] = dependency.Name;
                    break;
                }
            }

            return parentMap;
        }

        private static bool WouldCreateCycle(
            string service,
            string candidateParent,
            IReadOnlyDictionary<string, string?> parentMap)
        {
            string? current = candidateParent;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
            {
                if (string.Equals(current, service, StringComparison.OrdinalIgnoreCase)) return true;
                current = parentMap.TryGetValue(current, out string? parent) ? parent : null;
            }

            return false;
        }

        private static void ArrangeVolumes(
            SheetViewModel sheet,
            IReadOnlyCollection<NodeViewModel> volumesToArrange,
            double originX,
            double originY,
            double forestEnd,
            bool isHorizontal,
            double horizontalGap,
            double verticalGap)
        {
            var volumeSet = new HashSet<NodeViewModel>(volumesToArrange, ReferenceEqualityComparer.Instance);
            var occupied = sheet.Nodes
                .Where(node => !volumeSet.Contains(node))
                .Select(ToRect)
                .ToList();
            var volumes = volumesToArrange
                .Where(node => node.Type == NodeType.Volume)
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            double unownedX = isHorizontal ? originX : forestEnd + ForestGap;
            double unownedY = isHorizontal ? forestEnd + ForestGap : originY;
            foreach (NodeViewModel volume in volumes)
            {
                var owners = sheet.Connectors
                    .Where(connector => connector.RelationType == RelationType.VolumeMount)
                    .Select(connector => GetOtherNode(connector, volume))
                    .Where(node => node?.Type == NodeType.Container)
                    .Cast<NodeViewModel>()
                    .Distinct()
                    .ToList();

                double desiredX;
                double desiredY;
                if (owners.Count > 0)
                {
                    desiredX = owners.Max(owner => owner.X + owner.Width) + horizontalGap;
                    desiredY = owners.Max(owner => owner.Y + owner.Height) + verticalGap;
                }
                else
                {
                    desiredX = unownedX;
                    desiredY = unownedY;
                    if (isHorizontal)
                        unownedY += Math.Max(1, volume.Height) + verticalGap;
                    else
                        unownedX += Math.Max(1, volume.Width) + horizontalGap;
                }

                Rect candidate = new(Math.Max(originX, desiredX), Math.Max(originY, desiredY), Math.Max(1, volume.Width), Math.Max(1, volume.Height));
                while (occupied.Any(rect => IntersectsWithSpacing(rect, candidate, 18)))
                {
                    if (isHorizontal)
                        candidate.Y += Math.Max(1, volume.Height) + verticalGap;
                    else
                        candidate.X += Math.Max(1, volume.Width) + horizontalGap;
                }

                volume.X = candidate.X;
                volume.Y = candidate.Y;
                occupied.Add(candidate);
            }
        }

        private static void AvoidVolumeGroupCollisions(
            SheetViewModel sheet,
            IReadOnlyCollection<NodeViewModel> volumesToArrange,
            IReadOnlyCollection<GroupViewModel> groups,
            bool isHorizontal,
            double horizontalGap,
            double verticalGap)
        {
            var volumes = volumesToArrange
                .Where(node => node.Type == NodeType.Volume)
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (volumes.Count == 0) return;

            var volumeSet = new HashSet<NodeViewModel>(volumes, ReferenceEqualityComparer.Instance);
            var fixedNodeBounds = sheet.Nodes
                .Where(node => !volumeSet.Contains(node))
                .Select(ToRect)
                .ToList();
            var groupBounds = groups
                .Where(group => group.ContainedNodes.Count > 0)
                .Select(group => new Rect(
                    group.X,
                    group.Y,
                    Math.Max(1, group.Width),
                    Math.Max(1, group.Height)))
                .ToList();
            var placedVolumeBounds = new List<Rect>();

            foreach (NodeViewModel volume in volumes)
            {
                Rect candidate = ToRect(volume);
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    var collisions = fixedNodeBounds
                        .Concat(groupBounds)
                        .Concat(placedVolumeBounds)
                        .Where(obstacle => IntersectsWithSpacing(obstacle, candidate, 18))
                        .ToList();
                    if (collisions.Count == 0) break;

                    if (isHorizontal)
                        candidate.Y = collisions.Max(rect => rect.Bottom) + verticalGap;
                    else
                        candidate.X = collisions.Max(rect => rect.Right) + horizontalGap;
                }

                volume.X = candidate.X;
                volume.Y = candidate.Y;
                placedVolumeBounds.Add(candidate);
            }
        }

        private static NodeViewModel? GetOtherNode(ConnectorViewModel connector, NodeViewModel node)
        {
            if (ReferenceEquals(connector.Source, node)) return connector.Target as NodeViewModel;
            if (ReferenceEquals(connector.Target, node)) return connector.Source as NodeViewModel;
            return null;
        }

        private static bool IntersectsWithSpacing(Rect left, Rect right, double spacing)
        {
            Rect expanded = new(left.X - spacing, left.Y - spacing, left.Width + spacing * 2, left.Height + spacing * 2);
            return expanded.IntersectsWith(right);
        }

        private static void AvoidExistingNodeCollisions(
            SheetViewModel sheet,
            IReadOnlyCollection<NodeViewModel> arrangedNodes,
            bool isHorizontal)
        {
            var arrangedSet = new HashSet<NodeViewModel>(arrangedNodes, ReferenceEqualityComparer.Instance);
            var obstacles = sheet.Nodes
                .Where(node => !arrangedSet.Contains(node))
                .Select(ToRect)
                .ToList();
            if (obstacles.Count == 0 || arrangedNodes.Count == 0) return;

            for (int attempt = 0; attempt < 100; attempt++)
            {
                var collisions = arrangedNodes
                    .Select(ToRect)
                    .SelectMany(nodeRect => obstacles.Where(obstacle => IntersectsWithSpacing(obstacle, nodeRect, 18)))
                    .ToList();
                if (collisions.Count == 0) return;

                if (isHorizontal)
                {
                    double currentTop = arrangedNodes.Min(node => node.Y);
                    double nextTop = collisions.Max(rect => rect.Bottom) + VerticalGap;
                    double shiftY = Math.Max(VerticalGap, nextTop - currentTop);
                    foreach (NodeViewModel node in arrangedNodes) node.Y += shiftY;
                }
                else
                {
                    double currentLeft = arrangedNodes.Min(node => node.X);
                    double nextLeft = collisions.Max(rect => rect.Right) + HorizontalGap;
                    double shiftX = Math.Max(HorizontalGap, nextLeft - currentLeft);
                    foreach (NodeViewModel node in arrangedNodes) node.X += shiftX;
                }
            }
        }

        private static void ResizeGroupsRecursively(IEnumerable<GroupViewModel> groups)
        {
            var activeGroups = groups.Where(group => group.ContainedNodes.Count > 0).ToList();
            var children = activeGroups.ToDictionary(group => group, _ => new List<GroupViewModel>());
            var parentMap = new Dictionary<GroupViewModel, GroupViewModel?>();
            var uniqueNodes = new HashSet<NodeViewModel>(ReferenceEqualityComparer.Instance);
            foreach (GroupViewModel group in activeGroups)
                uniqueNodes.UnionWith(group.ContainedNodes);
            var nodeOrder = new Dictionary<NodeViewModel, int>(ReferenceEqualityComparer.Instance);
            int nodeIndex = 0;
            foreach (NodeViewModel node in uniqueNodes) nodeOrder[node] = nodeIndex++;
            var coincidentIndex = new Dictionary<GroupViewModel, int>();

            foreach (var coincidentGroups in activeGroups
                         .GroupBy(group => string.Join(",", group.ContainedNodes.Select(node => nodeOrder[node]).OrderBy(index => index))))
            {
                int index = 0;
                foreach (GroupViewModel group in coincidentGroups.OrderBy(group => group.Title, StringComparer.OrdinalIgnoreCase))
                    coincidentIndex[group] = index++;
            }

            foreach (GroupViewModel group in activeGroups)
            {
                GroupViewModel? parent = activeGroups
                    .Where(candidate => candidate != group &&
                                        candidate.ContainedNodes.Count > group.ContainedNodes.Count &&
                                        group.ContainedNodes.All(candidate.ContainedNodes.Contains))
                    .OrderBy(candidate => candidate.ContainedNodes.Count)
                    .FirstOrDefault();
                parentMap[group] = parent;
                if (parent != null) children[parent].Add(group);
            }

            Rect Resize(GroupViewModel group)
            {
                var bounds = group.ContainedNodes.Select(ToRect).ToList();
                bounds.AddRange(children[group].Select(Resize));
                Rect content = Union(bounds);
                double ringPadding = coincidentIndex[group] * CoincidentGroupGap;

                group.X = content.Left - GroupSidePadding - ringPadding;
                group.Y = content.Top - GroupTopPadding - ringPadding;
                group.Width = Math.Max(GroupViewModel.MinimumWidth, content.Width + (GroupSidePadding + ringPadding) * 2);
                group.Height = Math.Max(GroupViewModel.MinimumHeight, content.Height + GroupTopPadding + GroupBottomPadding + ringPadding * 2);
                return new Rect(group.X, group.Y, group.Width, group.Height);
            }

            var rootGroups = activeGroups.Where(group => parentMap[group] == null).ToList();
            foreach (GroupViewModel root in rootGroups)
                Resize(root);

            SeparateOverlappingGroupHeaders(rootGroups);
        }

        private static void SeparateOverlappingGroupHeaders(IReadOnlyCollection<GroupViewModel> groups)
        {
            var occupiedHeaders = new List<Rect>();
            foreach (GroupViewModel group in groups
                         .OrderByDescending(group => group.Width * group.Height)
                         .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase))
            {
                double originalBottom = group.Y + group.Height;
                Rect header = CreateHeaderRect(group);
                int attempts = 0;
                while (occupiedHeaders.Any(existing => existing.IntersectsWith(header)) && attempts++ < 20)
                {
                    header.Y -= GroupHeaderHeight + 4;
                }

                if (header.Y < group.Y)
                {
                    group.Y = header.Y;
                    group.Height = originalBottom - group.Y;
                }
                occupiedHeaders.Add(header);
            }
        }

        private static Rect CreateHeaderRect(GroupViewModel group) =>
            new(group.X, group.Y, Math.Min(group.Width, 190), GroupHeaderHeight);

        private static void ArrangeEmptyGroups(
            IEnumerable<GroupViewModel> groups,
            IReadOnlyCollection<NodeViewModel> arrangedNodes,
            double originX,
            double originY,
            bool isHorizontal)
        {
            var groupList = groups.ToList();
            var emptyGroups = groupList
                .Where(group => group.ContainedNodes.Count == 0)
                .OrderBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (emptyGroups.Count == 0) return;

            double contentBottom = arrangedNodes.Count > 0
                ? arrangedNodes.Max(node => node.Y + node.Height)
                : originY;
            if (groupList.Any(group => group.ContainedNodes.Count > 0))
                contentBottom = Math.Max(contentBottom, groupList.Where(group => group.ContainedNodes.Count > 0).Max(group => group.Y + group.Height));

            double contentRight = arrangedNodes.Count > 0
                ? arrangedNodes.Max(node => node.X + node.Width)
                : originX;
            if (groupList.Any(group => group.ContainedNodes.Count > 0))
                contentRight = Math.Max(contentRight, groupList.Where(group => group.ContainedNodes.Count > 0).Max(group => group.X + group.Width));

            double currentX = isHorizontal ? originX : contentRight + ForestGap;
            double currentY = isHorizontal ? contentBottom + ForestGap : originY;
            foreach (GroupViewModel group in emptyGroups)
            {
                group.X = currentX;
                group.Y = currentY;
                group.Width = Math.Max(GroupViewModel.MinimumWidth, group.Width);
                group.Height = Math.Max(GroupViewModel.MinimumHeight, group.Height);
                if (isHorizontal)
                    currentX += group.Width + HorizontalGap;
                else
                    currentY += group.Height + VerticalGap;
            }
        }

        private static void FinalizePlacement(
            SheetViewModel sheet,
            IReadOnlyCollection<NodeViewModel> scopedNodes,
            IReadOnlyCollection<GroupViewModel> scopedGroups,
            bool isHorizontal)
        {
            var nodes = scopedNodes.Distinct<NodeViewModel>(ReferenceEqualityComparer.Instance).ToList();
            var groups = scopedGroups.Distinct<GroupViewModel>(ReferenceEqualityComparer.Instance).ToList();
            if (nodes.Count == 0 && groups.Count == 0) return;

            var nodeSet = new HashSet<NodeViewModel>(nodes, ReferenceEqualityComparer.Instance);
            var groupSet = new HashSet<GroupViewModel>(groups, ReferenceEqualityComparer.Instance);
            var obstacles = sheet.Nodes
                .Where(node => !nodeSet.Contains(node))
                .Select(ToRect)
                .Concat(sheet.Groups
                    .Where(group => !groupSet.Contains(group))
                    .Select(group => new Rect(group.X, group.Y, Math.Max(1, group.Width), Math.Max(1, group.Height))))
                .ToList();

            Rect GetBounds()
            {
                var bounds = nodes.Select(ToRect)
                    .Concat(groups.Select(group => new Rect(group.X, group.Y, Math.Max(1, group.Width), Math.Max(1, group.Height))))
                    .ToList();
                return Union(bounds);
            }

            void Shift(double deltaX, double deltaY)
            {
                foreach (NodeViewModel node in nodes)
                {
                    node.X += deltaX;
                    node.Y += deltaY;
                }
                foreach (GroupViewModel group in groups)
                {
                    group.X += deltaX;
                    group.Y += deltaY;
                }
            }

            for (int attempt = 0; attempt < 100; attempt++)
            {
                Rect bounds = GetBounds();
                var collisions = obstacles
                    .Where(obstacle => IntersectsWithSpacing(obstacle, bounds, 24))
                    .ToList();
                if (collisions.Count == 0) break;

                if (isHorizontal)
                {
                    double nextTop = collisions.Max(rect => rect.Bottom) + VerticalGap;
                    Shift(0, Math.Max(VerticalGap, nextTop - bounds.Top));
                }
                else
                {
                    double nextLeft = collisions.Max(rect => rect.Right) + HorizontalGap;
                    Shift(Math.Max(HorizontalGap, nextLeft - bounds.Left), 0);
                }
            }

            Rect finalBounds = GetBounds();
            Shift(Math.Max(0, -finalBounds.Left), Math.Max(0, -finalBounds.Top));
            finalBounds = GetBounds();
            const double mapPadding = 120;
            sheet.MapWidth = Math.Max(sheet.MapWidth, finalBounds.Right + mapPadding);
            sheet.MapHeight = Math.Max(sheet.MapHeight, finalBounds.Bottom + mapPadding);
        }
        public static void CenterOn(
            SheetViewModel sheet,
            IReadOnlyCollection<NodeViewModel> scopedNodes,
            IReadOnlyCollection<GroupViewModel> scopedGroups,
            double centerX,
            double centerY)
        {
            var nodes = scopedNodes
                .Distinct<NodeViewModel>(ReferenceEqualityComparer.Instance)
                .ToList();
            var groups = scopedGroups
                .Distinct<GroupViewModel>(ReferenceEqualityComparer.Instance)
                .ToList();
            var bounds = nodes.Select(ToRect)
                .Concat(groups.Select(group => new Rect(
                    group.X,
                    group.Y,
                    Math.Max(1, group.Width),
                    Math.Max(1, group.Height))))
                .ToList();
            if (bounds.Count == 0) return;

            using IDisposable layoutUpdate = sheet.BeginLayoutUpdate();
            Rect projectBounds = Union(bounds);
            double deltaX = Math.Round((centerX - projectBounds.Left - (projectBounds.Width / 2.0)) / 10.0) * 10.0;
            double deltaY = Math.Round((centerY - projectBounds.Top - (projectBounds.Height / 2.0)) / 10.0) * 10.0;

            foreach (NodeViewModel node in nodes)
            {
                node.X += deltaX;
                node.Y += deltaY;
            }
            foreach (GroupViewModel group in groups)
            {
                group.X += deltaX;
                group.Y += deltaY;
            }

            double left = nodes.Select(node => node.X)
                .Concat(groups.Select(group => group.X))
                .DefaultIfEmpty(0)
                .Min();
            double top = nodes.Select(node => node.Y)
                .Concat(groups.Select(group => group.Y))
                .DefaultIfEmpty(0)
                .Min();
            double correctionX = Math.Max(0, -left);
            double correctionY = Math.Max(0, -top);
            if (correctionX > 0 || correctionY > 0)
            {
                foreach (NodeViewModel node in nodes)
                {
                    node.X += correctionX;
                    node.Y += correctionY;
                }
                foreach (GroupViewModel group in groups)
                {
                    group.X += correctionX;
                    group.Y += correctionY;
                }
            }

            double right = nodes.Select(node => node.X + node.Width)
                .Concat(groups.Select(group => group.X + group.Width))
                .DefaultIfEmpty(0)
                .Max();
            double bottom = nodes.Select(node => node.Y + node.Height)
                .Concat(groups.Select(group => group.Y + group.Height))
                .DefaultIfEmpty(0)
                .Max();
            const double mapPadding = 120;
            sheet.MapWidth = Math.Max(sheet.MapWidth, right + mapPadding);
            sheet.MapHeight = Math.Max(sheet.MapHeight, bottom + mapPadding);
            sheet.UpdateGroupLayering();
        }

        private static Rect Union(IReadOnlyList<Rect> bounds)
        {
            if (bounds.Count == 0) return Rect.Empty;
            Rect result = bounds[0];
            for (int index = 1; index < bounds.Count; index++) result.Union(bounds[index]);
            return result;
        }

        private static Rect ToRect(NodeViewModel node) =>
            new(node.X, node.Y, Math.Max(1, node.Width), Math.Max(1, node.Height));
    }
}
