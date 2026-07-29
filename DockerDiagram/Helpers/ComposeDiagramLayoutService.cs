using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DockerDiagram.Helpers
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
            ComposeLayoutOptions options = layoutOptions ?? new ComposeLayoutOptions();
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
                ArrangeEmptyGroups(groupsWithoutServices, volumesWithoutServices, originX, originY, isHorizontal);
                sheet.UpdateGroupLayering();
                return;
            }

            var orderedServices = serviceNodes.Keys.ToList();
            var orderIndex = orderedServices
                .Select((name, index) => (name, index))
                .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
            var nodes = serviceNodes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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

            foreach (var childList in children.Values)
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
                .Where(service => !primaryParent.TryGetValue(service, out string? parent) || string.IsNullOrWhiteSpace(parent))
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

            double depthGap = isHorizontal ? horizontalGap : verticalGap;
            double siblingGap = isHorizontal ? verticalGap : horizontalGap;
            double depthOrigin = isHorizontal ? originX : originY;
            double siblingOrigin = isHorizontal ? originY : originX;
            var levelPositions = new Dictionary<int, double> { [0] = depthOrigin };
            for (int depth = 1; depth <= levelSizes.Keys.DefaultIfEmpty(0).Max(); depth++)
                levelPositions[depth] = levelPositions[depth - 1] + levelSizes.GetValueOrDefault(depth - 1, 80) + depthGap;

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
                    subtreeEnd = Math.Max(subtreeEnd, LayoutTree(child, depth + 1, childStart));
                    isFirstChild = false;
                }

                return subtreeEnd;
            }

            double forestEnd = siblingOrigin;
            bool isFirstRoot = true;
            foreach (string root in roots)
            {
                double rootStart = isFirstRoot ? siblingOrigin : forestEnd + ForestGap + siblingGap;
                forestEnd = Math.Max(forestEnd, LayoutTree(root, 0, rootStart));
                isFirstRoot = false;
            }

            IReadOnlyCollection<NodeViewModel> volumes =
                scopedVolumes ?? sheet.Nodes.Where(node => node.Type == NodeType.Volume).ToList();
            ArrangeVolumes(
                sheet,
                volumes,
                originX,
                originY,
                forestEnd,
                isHorizontal,
                horizontalGap,
                verticalGap);
            AvoidExistingNodeCollisions(sheet, serviceNodes.Values.Concat(volumes).ToList(), isHorizontal);
            IReadOnlyCollection<GroupViewModel> groups = scopedGroups ?? sheet.Groups.ToList();
            ResizeGroupsRecursively(groups);
            ArrangeEmptyGroups(groups, serviceNodes.Values.Concat(volumes).ToList(), originX, originY, isHorizontal);
            sheet.UpdateGroupLayering();
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
