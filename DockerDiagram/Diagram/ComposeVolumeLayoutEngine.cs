using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Diagram
{
    public sealed record ComposeVolumeLayoutItem(
        string Id,
        double DepthSize,
        double BreadthSize,
        IReadOnlyList<string> OwnerIds);

    public sealed record ComposeVolumeServiceSlot(
        string Id,
        int Rank,
        double Depth,
        double Breadth,
        double DepthSize,
        double BreadthSize);

    public sealed record ComposeVolumeAxisPosition(
        double Depth,
        double Breadth);

    public sealed record ComposeVolumeNetworkRegion(
        string Id,
        double Depth,
        double Breadth,
        double DepthSize,
        double BreadthSize,
        IReadOnlyList<string> MemberIds)
    {
        public double DepthEnd => Depth + DepthSize;
        public double BreadthEnd => Breadth + BreadthSize;
    }

    public sealed class ComposeVolumeLayoutPlan
    {
        internal ComposeVolumeLayoutPlan(
            IReadOnlyDictionary<string, ComposeVolumeLayoutItem> volumes,
            IReadOnlyDictionary<string, string> primaryOwnerByVolume,
            IReadOnlyDictionary<string, int> anchorRankByVolume,
            IReadOnlyDictionary<string, IReadOnlyList<string>> volumeIdsByOwner,
            IReadOnlyList<string> orphanVolumeIds,
            IReadOnlyDictionary<string, double> reservedBreadthByService,
            IReadOnlySet<string> inlineVolumeIds,
            IReadOnlyDictionary<string, int> serviceOrder,
            IReadOnlyDictionary<string, int> componentByService,
            double volumeBreadthGap)
        {
            Volumes = volumes;
            PrimaryOwnerByVolume = primaryOwnerByVolume;
            AnchorRankByVolume = anchorRankByVolume;
            VolumeIdsByOwner = volumeIdsByOwner;
            OrphanVolumeIds = orphanVolumeIds;
            ReservedBreadthByService = reservedBreadthByService;
            InlineVolumeIds = inlineVolumeIds;
            ServiceOrder = serviceOrder;
            ComponentByService = componentByService;
            VolumeBreadthGap = volumeBreadthGap;
        }

        public IReadOnlyDictionary<string, ComposeVolumeLayoutItem> Volumes { get; }
        public IReadOnlyDictionary<string, string> PrimaryOwnerByVolume { get; }
        public IReadOnlyDictionary<string, int> AnchorRankByVolume { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> VolumeIdsByOwner { get; }
        public IReadOnlyList<string> OrphanVolumeIds { get; }
        public IReadOnlyDictionary<string, double> ReservedBreadthByService { get; }
        public IReadOnlySet<string> InlineVolumeIds { get; }
        internal IReadOnlyDictionary<string, int> ServiceOrder { get; }
        internal IReadOnlyDictionary<string, int> ComponentByService { get; }
        internal double VolumeBreadthGap { get; }
    }

    public sealed class ComposeVolumeLayoutResult
    {
        internal ComposeVolumeLayoutResult(
            IReadOnlyDictionary<string, ComposeVolumeAxisPosition> servicePositions,
            IReadOnlyDictionary<string, ComposeVolumeAxisPosition> volumePositions,
            IReadOnlyDictionary<int, double> depthShiftByRank,
            IReadOnlyDictionary<int, double> requiredDepthGapByRank,
            IReadOnlyDictionary<string, int> laneByVolume,
            IReadOnlySet<string> externalVolumeIds,
            IReadOnlyDictionary<string, IReadOnlyList<string>> internalNetworkIdsByVolume,
            double breadthStart,
            double breadthEnd)
        {
            ServicePositions = servicePositions;
            VolumePositions = volumePositions;
            DepthShiftByRank = depthShiftByRank;
            RequiredDepthGapByRank = requiredDepthGapByRank;
            LaneByVolume = laneByVolume;
            ExternalVolumeIds = externalVolumeIds;
            InternalNetworkIdsByVolume = internalNetworkIdsByVolume;
            BreadthStart = breadthStart;
            BreadthEnd = breadthEnd;
        }

        public IReadOnlyDictionary<string, ComposeVolumeAxisPosition> ServicePositions { get; }
        public IReadOnlyDictionary<string, ComposeVolumeAxisPosition> VolumePositions { get; }
        public IReadOnlyDictionary<int, double> DepthShiftByRank { get; }
        public IReadOnlyDictionary<int, double> RequiredDepthGapByRank { get; }
        public IReadOnlyDictionary<string, int> LaneByVolume { get; }
        public IReadOnlySet<string> ExternalVolumeIds { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> InternalNetworkIdsByVolume { get; }
        public double BreadthStart { get; }
        public double BreadthEnd { get; }
    }

    /// <summary>
    /// 서비스의 볼륨 envelope를 예약하고, Rank 사이에 역계단 형태로 볼륨을 배치합니다.
    /// 축 독립 좌표를 사용하므로 LeftToRight와 TopToBottom에 동일하게 적용됩니다.
    /// </summary>
    public static class ComposeVolumeLayoutEngine
    {
        public static ComposeVolumeLayoutPlan CreatePlan(
            IEnumerable<string> orderedServiceIds,
            IReadOnlyDictionary<string, int> rankByService,
            IReadOnlyDictionary<string, double> serviceBreadthSizes,
            IEnumerable<ComposeVolumeLayoutItem> inputVolumes,
            double volumeBreadthGap,
            IReadOnlySet<string>? inlineVolumeIds = null,
            IReadOnlyDictionary<string, int>? componentByService = null)
        {
            ArgumentNullException.ThrowIfNull(orderedServiceIds);
            ArgumentNullException.ThrowIfNull(rankByService);
            ArgumentNullException.ThrowIfNull(serviceBreadthSizes);
            ArgumentNullException.ThrowIfNull(inputVolumes);

            string[] serviceIds = orderedServiceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var serviceOrder = serviceIds
                .Select((id, index) => (id, index))
                .ToDictionary(
                    item => item.id,
                    item => item.index,
                    StringComparer.OrdinalIgnoreCase);
            var serviceComponents = serviceIds.ToDictionary(
                id => id,
                id => componentByService is not null &&
                      componentByService.TryGetValue(id, out int component)
                    ? component
                    : 0,
                StringComparer.OrdinalIgnoreCase);
            foreach (string serviceId in serviceIds)
            {
                if (!rankByService.ContainsKey(serviceId))
                    throw new ArgumentException($"Missing rank for service '{serviceId}'.", nameof(rankByService));
                if (!serviceBreadthSizes.ContainsKey(serviceId))
                    throw new ArgumentException(
                        $"Missing breadth size for service '{serviceId}'.",
                        nameof(serviceBreadthSizes));
            }

            var volumes = inputVolumes
                .Where(volume => !string.IsNullOrWhiteSpace(volume.Id))
                .GroupBy(volume => volume.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(
                    volume => volume.Id,
                    volume => new ComposeVolumeLayoutItem(
                        volume.Id,
                        Math.Max(1, volume.DepthSize),
                        Math.Max(1, volume.BreadthSize),
                        volume.OwnerIds
                            .Where(serviceOrder.ContainsKey)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()),
                    StringComparer.OrdinalIgnoreCase);
            var primaryOwnerByVolume =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var anchorRankByVolume =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var volumeIdsByOwner = serviceIds.ToDictionary(
                id => id,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var orphanVolumeIds = new List<string>();

            foreach (ComposeVolumeLayoutItem volume in volumes.Values)
            {
                string? primaryOwner = volume.OwnerIds
                    .OrderByDescending(ownerId => rankByService[ownerId])
                    .ThenBy(ownerId => serviceOrder[ownerId])
                    .FirstOrDefault();
                if (primaryOwner is null)
                {
                    orphanVolumeIds.Add(volume.Id);
                    continue;
                }

                primaryOwnerByVolume[volume.Id] = primaryOwner;
                anchorRankByVolume[volume.Id] = rankByService[primaryOwner];
                volumeIdsByOwner[primaryOwner].Add(volume.Id);
            }

            var inlineIds = new HashSet<string>(
                inlineVolumeIds ?? volumes.Values
                    .Where(volume => volume.OwnerIds.Count > 0)
                    .Select(volume => volume.Id),
                StringComparer.OrdinalIgnoreCase);
            inlineIds.IntersectWith(primaryOwnerByVolume.Keys);

            double breadthGap = Math.Max(0, volumeBreadthGap);
            var reservedBreadth = serviceIds.ToDictionary(
                id => id,
                id => Math.Max(1, serviceBreadthSizes[id]),
                StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<int, string> component in serviceIds.GroupBy(
                         id => serviceComponents[id]))
            {
                string[] componentServiceIds = component.ToArray();
                string[] componentVolumeIds = primaryOwnerByVolume
                    .Where(pair =>
                        inlineIds.Contains(pair.Key) &&
                        componentServiceIds.Contains(pair.Value, StringComparer.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .ToArray();
                if (componentVolumeIds.Length == 0) continue;

                double serviceEnvelope = componentServiceIds.Sum(id =>
                    Math.Max(1, serviceBreadthSizes[id])) +
                    (breadthGap * Math.Max(0, componentServiceIds.Length - 1));
                double volumeEnvelope = componentVolumeIds.Length == 0
                    ? 0
                    : componentVolumeIds.Sum(volumeId => volumes[volumeId].BreadthSize) +
                      (breadthGap * (componentVolumeIds.Length - 1));
                string anchorId = componentServiceIds
                    .OrderBy(id => rankByService[id])
                    .ThenBy(id => serviceOrder[id])
                    .First();

                // 볼륨은 각 컨테이너 사이가 아니라 연결된 컨테이너 영역 전체의 아래층에 둡니다.
                // 첫 서비스의 가상 envelope에 구성요소 전체 높이를 예약해 다른 트리와의 충돌을 막습니다.
                reservedBreadth[anchorId] = Math.Max(
                    reservedBreadth[anchorId],
                    serviceEnvelope + breadthGap + volumeEnvelope);
            }

            return new ComposeVolumeLayoutPlan(
                volumes,
                primaryOwnerByVolume,
                anchorRankByVolume,
                volumeIdsByOwner.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                orphanVolumeIds.ToArray(),
                reservedBreadth,
                inlineIds,
                serviceOrder,
                serviceComponents,
                breadthGap);
        }

        public static ComposeVolumeLayoutResult Arrange(
            ComposeVolumeLayoutPlan plan,
            IEnumerable<ComposeVolumeServiceSlot> inputServices,
            double depthOrigin,
            double breadthOrigin,
            double volumeLeadGap,
            double volumeTrailGap,
            double volumeStairStep,
            double orphanGap)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(inputServices);

            var services = inputServices
                .Where(service => !string.IsNullOrWhiteSpace(service.Id))
                .GroupBy(service => service.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(
                    service => service.Id,
                    service => new ComposeVolumeServiceSlot(
                        service.Id,
                        service.Rank,
                        service.Depth,
                        service.Breadth,
                        Math.Max(1, service.DepthSize),
                        Math.Max(1, service.BreadthSize)),
                    StringComparer.OrdinalIgnoreCase);
            foreach (string serviceId in plan.ReservedBreadthByService.Keys)
            {
                if (!services.ContainsKey(serviceId))
                    throw new ArgumentException($"Missing service slot '{serviceId}'.", nameof(inputServices));
            }

            double leadGap = Math.Max(0, volumeLeadGap);
            double trailGap = Math.Max(0, volumeTrailGap);
            double stairStep = Math.Max(0, volumeStairStep);
            int[] ranks = services.Values
                .Select(service => service.Rank)
                .Distinct()
                .OrderBy(rank => rank)
                .ToArray();
            var baseRankStart = ranks.ToDictionary(
                rank => rank,
                rank => services.Values
                    .Where(service => service.Rank == rank)
                    .Min(service => service.Depth));
            var baseRankEnd = ranks.ToDictionary(
                rank => rank,
                rank => services.Values
                    .Where(service => service.Rank == rank)
                    .Max(service => service.Depth + service.DepthSize));
            var volumeIdsByRank = ranks.ToDictionary(
                rank => rank,
                _ => new List<string>());
            foreach ((string volumeId, int rank) in plan.AnchorRankByVolume)
            {
                if (!plan.InlineVolumeIds.Contains(volumeId)) continue;
                if (volumeIdsByRank.TryGetValue(rank, out List<string>? rankVolumes))
                    rankVolumes.Add(volumeId);
            }

            var requiredDepthGapByRank = new Dictionary<int, double>();
            foreach (int rank in ranks)
            {
                IReadOnlyList<string> rankVolumeIds = volumeIdsByRank[rank];
                if (rankVolumeIds.Count == 0)
                {
                    requiredDepthGapByRank[rank] = 0;
                    continue;
                }

                double maximumVolumeDepth = rankVolumeIds
                    .Max(volumeId => plan.Volumes[volumeId].DepthSize);
                requiredDepthGapByRank[rank] =
                    leadGap +
                    maximumVolumeDepth +
                    (stairStep * (rankVolumeIds.Count - 1)) +
                    trailGap;
            }

            var depthShiftByRank = new Dictionary<int, double>();
            double cumulativeShift = 0;
            for (int index = 0; index < ranks.Length; index++)
            {
                int rank = ranks[index];
                depthShiftByRank[rank] = cumulativeShift;
                if (index == ranks.Length - 1) continue;

                int nextRank = ranks[index + 1];
                double availableGap = baseRankStart[nextRank] - baseRankEnd[rank];
                double extraGap = Math.Max(
                    0,
                    requiredDepthGapByRank[rank] - availableGap);
                cumulativeShift += extraGap;
            }

            var servicePositions =
                new Dictionary<string, ComposeVolumeAxisPosition>(StringComparer.OrdinalIgnoreCase);
            foreach (ComposeVolumeServiceSlot service in services.Values)
            {
                servicePositions[service.Id] = new ComposeVolumeAxisPosition(
                    service.Depth + depthShiftByRank[service.Rank],
                    service.Breadth);
            }

            var candidates = new List<VolumeCandidate>();
            foreach (string ownerId in services.Values
                         .OrderBy(service => plan.ComponentByService[service.Id])
                         .ThenBy(service => service.Breadth)
                         .ThenBy(service => plan.ServiceOrder[service.Id])
                         .Select(service => service.Id))
            {
                string[] ownerVolumeIds = plan.VolumeIdsByOwner[ownerId]
                    .Where(plan.InlineVolumeIds.Contains)
                    .ToArray();
                for (int localIndex = 0; localIndex < ownerVolumeIds.Length; localIndex++)
                {
                    string volumeId = ownerVolumeIds[localIndex];
                    candidates.Add(new VolumeCandidate(
                        volumeId,
                        ownerId,
                        0,
                        0,
                        plan.ServiceOrder[ownerId],
                        localIndex));
                }
            }

            var placedCandidates = new List<VolumeCandidate>();
            foreach (IGrouping<int, VolumeCandidate> componentCandidates in candidates.GroupBy(
                         candidate => plan.ComponentByService[candidate.OwnerId]))
            {
                double cursor = services.Values
                    .Where(service => plan.ComponentByService[service.Id] == componentCandidates.Key)
                    .Max(service => service.Breadth + service.BreadthSize) +
                    plan.VolumeBreadthGap;
                foreach (VolumeCandidate candidate in componentCandidates
                             .OrderBy(item => services[item.OwnerId].Rank)
                             .ThenBy(item => services[item.OwnerId].Breadth)
                             .ThenBy(candidate => candidate.OwnerOrder)
                             .ThenBy(item => item.LocalIndex))
                {
                    ComposeVolumeLayoutItem volume = plan.Volumes[candidate.VolumeId];
                    placedCandidates.Add(candidate with
                    {
                        Breadth = cursor,
                        BreadthCenter = cursor + (volume.BreadthSize / 2.0)
                    });
                    cursor += volume.BreadthSize + plan.VolumeBreadthGap;
                }
            }

            var volumePositions =
                new Dictionary<string, ComposeVolumeAxisPosition>(StringComparer.OrdinalIgnoreCase);
            var laneByVolume = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<(int Component, int Rank), VolumeCandidate> laneGroup in
                     placedCandidates.GroupBy(candidate => (
                         plan.ComponentByService[candidate.OwnerId],
                         services[candidate.OwnerId].Rank)))
            {
                VolumeCandidate[] orderedCandidates = laneGroup
                    .OrderBy(candidate => candidate.Breadth)
                    .ThenBy(candidate => candidate.OwnerOrder)
                    .ToArray();
                int rank = laneGroup.Key.Rank;
                double shiftedRankEnd = baseRankEnd[rank] + depthShiftByRank[rank];
                for (int index = 0; index < orderedCandidates.Length; index++)
                {
                    VolumeCandidate candidate = orderedCandidates[index];
                    int lane = orderedCandidates.Length - 1 - index;
                    volumePositions[candidate.VolumeId] = new ComposeVolumeAxisPosition(
                        shiftedRankEnd + leadGap + (lane * stairStep),
                        candidate.Breadth);
                    laneByVolume[candidate.VolumeId] = lane;
                }
            }

            double breadthStart = services.Values.Min(service => service.Breadth);
            double breadthEnd = services.Values.Max(service =>
                service.Breadth + plan.ReservedBreadthByService[service.Id]);
            foreach ((string volumeId, ComposeVolumeAxisPosition position) in volumePositions)
            {
                ComposeVolumeLayoutItem volume = plan.Volumes[volumeId];
                breadthStart = Math.Min(breadthStart, position.Breadth);
                breadthEnd = Math.Max(breadthEnd, position.Breadth + volume.BreadthSize);
            }

            double orphanCursor = breadthEnd + Math.Max(0, orphanGap);
            foreach (string volumeId in plan.OrphanVolumeIds)
            {
                ComposeVolumeLayoutItem volume = plan.Volumes[volumeId];
                volumePositions[volumeId] = new ComposeVolumeAxisPosition(
                    depthOrigin,
                    orphanCursor);
                laneByVolume[volumeId] = 0;
                breadthEnd = orphanCursor + volume.BreadthSize;
                orphanCursor = breadthEnd + plan.VolumeBreadthGap;
            }

            breadthStart = Math.Min(breadthStart, breadthOrigin);
            return new ComposeVolumeLayoutResult(
                servicePositions,
                volumePositions,
                depthShiftByRank,
                requiredDepthGapByRank,
                laneByVolume,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                breadthStart,
                breadthEnd);
        }

        /// <summary>
        /// 볼륨의 1차 후보는 컨테이너 연결선 아래에 유지합니다. 다만 소유 컨테이너와
        /// 다음 깊이 컨테이너가 공유하는 네트워크 내부에 후보 전체가 들어가지 않으면,
        /// 모든 네트워크 아래의 외부 선반으로 보내 소유자 순서대로 적층합니다.
        /// </summary>
        public static ComposeVolumeLayoutResult ResolveNetworkAwarePlacement(
            ComposeVolumeLayoutPlan plan,
            IEnumerable<ComposeVolumeServiceSlot> inputServices,
            ComposeVolumeLayoutResult currentLayout,
            IEnumerable<ComposeVolumeNetworkRegion> inputRegions,
            IReadOnlyDictionary<string, IReadOnlySet<string>> descendantIdsByService,
            double externalGap)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(inputServices);
            ArgumentNullException.ThrowIfNull(currentLayout);
            ArgumentNullException.ThrowIfNull(inputRegions);
            ArgumentNullException.ThrowIfNull(descendantIdsByService);

            var services = inputServices
                .Where(service => !string.IsNullOrWhiteSpace(service.Id))
                .GroupBy(service => service.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(
                    service => service.Id,
                    service => service,
                    StringComparer.OrdinalIgnoreCase);
            var regions = inputRegions
                .Where(region => !string.IsNullOrWhiteSpace(region.Id))
                .Select(region => new ComposeVolumeNetworkRegion(
                    region.Id,
                    region.Depth,
                    region.Breadth,
                    Math.Max(1, region.DepthSize),
                    Math.Max(1, region.BreadthSize),
                    region.MemberIds
                        .Where(services.ContainsKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .Where(region => region.MemberIds.Count > 0)
                .ToArray();
            if (regions.Length == 0 || plan.Volumes.Count == 0)
                return currentLayout;

            var volumePositions = new Dictionary<string, ComposeVolumeAxisPosition>(
                currentLayout.VolumePositions,
                StringComparer.OrdinalIgnoreCase);
            var externalVolumeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var internalNetworkIdsByVolume =
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var relatedRegionIdsByExternalVolume =
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string volumeId, ComposeVolumeLayoutItem volume) in plan.Volumes)
            {
                string[] ownerIds = volume.OwnerIds
                    .Where(services.ContainsKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (ownerIds.Length == 0)
                {
                    continue;
                }

                ComposeVolumeNetworkRegion[] relatedOwnerRegions = regions
                    .Where(region => ownerIds.Any(ownerId => region.MemberIds.Contains(
                        ownerId,
                        StringComparer.OrdinalIgnoreCase)))
                    .ToArray();
                if (relatedOwnerRegions.Length == 0) continue;
                relatedRegionIdsByExternalVolume[volumeId] = relatedOwnerRegions
                    .Select(region => region.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (!plan.InlineVolumeIds.Contains(volumeId) ||
                    !volumePositions.TryGetValue(volumeId, out ComposeVolumeAxisPosition? candidate) ||
                    candidate is null)
                {
                    externalVolumeIds.Add(volumeId);
                    continue;
                }

                var requiredRegions = new Dictionary<string, ComposeVolumeNetworkRegion>(
                    StringComparer.OrdinalIgnoreCase);
                bool everyNetworkOwnerHasSharedDescendantRegion = true;
                foreach (string ownerId in ownerIds)
                {
                    ComposeVolumeNetworkRegion[] ownerRegions = relatedOwnerRegions
                        .Where(region => region.MemberIds.Contains(
                            ownerId,
                            StringComparer.OrdinalIgnoreCase))
                        .ToArray();
                    if (ownerRegions.Length == 0) continue;

                    IReadOnlySet<string> descendants =
                        descendantIdsByService.TryGetValue(ownerId, out IReadOnlySet<string>? found)
                            ? found
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ComposeVolumeNetworkRegion[] sharedDescendantRegions = ownerRegions
                        .Where(region => region.MemberIds.Any(descendants.Contains))
                        .ToArray();
                    if (sharedDescendantRegions.Length == 0)
                    {
                        everyNetworkOwnerHasSharedDescendantRegion = false;
                        break;
                    }

                    foreach (ComposeVolumeNetworkRegion region in sharedDescendantRegions)
                        requiredRegions[region.Id] = region;
                }

                ComposeVolumeNetworkRegion? intersection =
                    everyNetworkOwnerHasSharedDescendantRegion && requiredRegions.Count > 0
                        ? IntersectRegions(requiredRegions.Values)
                        : null;
                if (intersection is null || !Contains(intersection, candidate, volume))
                {
                    externalVolumeIds.Add(volumeId);
                    continue;
                }

                internalNetworkIdsByVolume[volumeId] = requiredRegions.Keys
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (externalVolumeIds.Count == 0)
            {
                return new ComposeVolumeLayoutResult(
                    currentLayout.ServicePositions,
                    currentLayout.VolumePositions,
                    currentLayout.DepthShiftByRank,
                    currentLayout.RequiredDepthGapByRank,
                    currentLayout.LaneByVolume,
                    externalVolumeIds,
                    internalNetworkIdsByVolume,
                    currentLayout.BreadthStart,
                    currentLayout.BreadthEnd);
            }

            IReadOnlyDictionary<string, int> componentByRegion = BuildRegionComponents(regions);
            var shelfGroups = externalVolumeIds
                .Select(volumeId =>
                {
                    IReadOnlyList<string> regionIds =
                        relatedRegionIdsByExternalVolume.GetValueOrDefault(volumeId) ??
                        Array.Empty<string>();
                    string[] componentKeys = regionIds
                        .Where(componentByRegion.ContainsKey)
                        .Select(id => componentByRegion[id].ToString())
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    string shelfKey = componentKeys.Length == 0
                        ? "unscoped"
                        : string.Join("+", componentKeys);
                    int anchorRank = plan.AnchorRankByVolume.GetValueOrDefault(
                        volumeId,
                        int.MaxValue);
                    return (
                        VolumeId: volumeId,
                        ShelfKey: shelfKey,
                        AnchorRank: anchorRank,
                        RegionIds: regionIds);
                })
                .GroupBy(item => item.ShelfKey, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();
            var occupiedShelves = new List<ComposeVolumeNetworkRegion>();
            var servicePositions = new Dictionary<string, ComposeVolumeAxisPosition>(
                currentLayout.ServicePositions,
                StringComparer.OrdinalIgnoreCase);
            var laneByVolume = new Dictionary<string, int>(
                currentLayout.LaneByVolume,
                StringComparer.OrdinalIgnoreCase);
            double safeExternalGap = Math.Max(0, externalGap);
            double corridorPadding = Math.Max(28, Math.Min(60, safeExternalGap * 0.30));
            double externalStairStep = Math.Max(30, Math.Min(55, safeExternalGap * 0.35));
            foreach (var shelfGroup in shelfGroups)
            {
                string[] shelfRegionIds = shelfGroup
                    .SelectMany(item => item.RegionIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                ComposeVolumeNetworkRegion shelfBounds = shelfRegionIds.Length == 0
                    ? UnionRegions(regions)
                    : UnionRegions(regions.Where(region => shelfRegionIds.Contains(
                        region.Id,
                        StringComparer.OrdinalIgnoreCase)));
                var volumeGroups = shelfGroup
                    .GroupBy(item => item.AnchorRank)
                    .OrderBy(group => group.Key)
                    .Select(group =>
                    {
                        string[] volumeIds = group
                            .Select(item => item.VolumeId)
                            .OrderBy(id =>
                            {
                                string? ownerId = plan.PrimaryOwnerByVolume.GetValueOrDefault(id);
                                return ownerId is not null &&
                                       services.TryGetValue(ownerId, out ComposeVolumeServiceSlot? owner)
                                    ? owner.Breadth
                                    : double.MaxValue;
                            })
                            .ThenBy(id => plan.Volumes[id].OwnerIds
                                .Where(plan.ServiceOrder.ContainsKey)
                                .Select(ownerId => plan.ServiceOrder[ownerId])
                                .DefaultIfEmpty(int.MaxValue)
                                .Min())
                            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        double depthSize = volumeIds
                            .Select((id, index) =>
                                plan.Volumes[id].DepthSize +
                                ((volumeIds.Length - 1 - index) * externalStairStep))
                            .DefaultIfEmpty(1)
                            .Max();
                        return new
                        {
                            AnchorRank = group.Key,
                            VolumeIds = volumeIds,
                            DepthSize = depthSize
                        };
                    })
                    .ToArray();

                foreach (var volumeGroup in volumeGroups)
                {
                    var groupDepthByVolume = new Dictionary<string, double>(
                        StringComparer.OrdinalIgnoreCase);
                    for (int volumeIndex = 0;
                         volumeIndex < volumeGroup.VolumeIds.Length;
                         volumeIndex++)
                    {
                        string volumeId = volumeGroup.VolumeIds[volumeIndex];
                        int lane = volumeGroup.VolumeIds.Length - 1 - volumeIndex;
                        if (currentLayout.VolumePositions.TryGetValue(
                                volumeId,
                                out ComposeVolumeAxisPosition? reservedPosition) &&
                            reservedPosition is not null)
                        {
                            groupDepthByVolume[volumeId] = reservedPosition.Depth;
                        }
                        else
                        {
                            string? ownerId = plan.PrimaryOwnerByVolume.GetValueOrDefault(volumeId);
                            double ownerRight = ownerId is not null &&
                                                services.TryGetValue(ownerId, out ComposeVolumeServiceSlot? owner)
                                ? servicePositions[owner.Id].Depth + owner.DepthSize
                                : shelfBounds.DepthEnd;
                            groupDepthByVolume[volumeId] =
                                ownerRight + corridorPadding + (lane * externalStairStep);
                        }
                    }

                    double groupDepthStart = volumeGroup.VolumeIds
                        .Min(id => groupDepthByVolume[id]);
                    double groupDepthEnd = volumeGroup.VolumeIds
                        .Max(id => groupDepthByVolume[id] + plan.Volumes[id].DepthSize);
                    double groupBreadthCursor = shelfBounds.BreadthEnd + safeExternalGap;
                    var depthRegion = new ComposeVolumeNetworkRegion(
                        $"{shelfGroup.Key}:{volumeGroup.AnchorRank}",
                        groupDepthStart,
                        groupBreadthCursor,
                        Math.Max(1, groupDepthEnd - groupDepthStart),
                        1,
                        Array.Empty<string>());
                    foreach (ComposeVolumeNetworkRegion occupied in occupiedShelves.Where(
                                 occupied => DepthRangesOverlap(occupied, depthRegion)))
                    {
                        groupBreadthCursor = Math.Max(
                            groupBreadthCursor,
                            occupied.BreadthEnd + safeExternalGap);
                    }

                    double groupBreadthStart = groupBreadthCursor;
                    for (int volumeIndex = 0;
                         volumeIndex < volumeGroup.VolumeIds.Length;
                         volumeIndex++)
                    {
                        string volumeId = volumeGroup.VolumeIds[volumeIndex];
                        ComposeVolumeLayoutItem volume = plan.Volumes[volumeId];
                        int lane = volumeGroup.VolumeIds.Length - 1 - volumeIndex;
                        double depth = groupDepthByVolume[volumeId];
                        volumePositions[volumeId] = new ComposeVolumeAxisPosition(
                            depth,
                            groupBreadthCursor);
                        laneByVolume[volumeId] = lane;
                        groupBreadthCursor += volume.BreadthSize + plan.VolumeBreadthGap;
                    }

                    double groupBreadthSize = Math.Max(
                        1,
                        groupBreadthCursor - groupBreadthStart - plan.VolumeBreadthGap);
                    occupiedShelves.Add(new ComposeVolumeNetworkRegion(
                        depthRegion.Id,
                        groupDepthStart,
                        groupBreadthStart,
                        Math.Max(1, groupDepthEnd - groupDepthStart),
                        groupBreadthSize,
                        Array.Empty<string>()));
                }
            }

            double breadthStart = currentLayout.BreadthStart;
            double breadthEnd = Math.Max(
                currentLayout.BreadthEnd,
                volumePositions.Max(pair =>
                    pair.Value.Breadth + plan.Volumes[pair.Key].BreadthSize));
            return new ComposeVolumeLayoutResult(
                servicePositions,
                volumePositions,
                currentLayout.DepthShiftByRank,
                currentLayout.RequiredDepthGapByRank,
                laneByVolume,
                externalVolumeIds,
                internalNetworkIdsByVolume,
                breadthStart,
                breadthEnd);
        }

        private static ComposeVolumeNetworkRegion? IntersectRegions(
            IEnumerable<ComposeVolumeNetworkRegion> inputRegions)
        {
            ComposeVolumeNetworkRegion[] regions = inputRegions.ToArray();
            if (regions.Length == 0) return null;
            double depth = regions.Max(region => region.Depth);
            double breadth = regions.Max(region => region.Breadth);
            double depthEnd = regions.Min(region => region.DepthEnd);
            double breadthEnd = regions.Min(region => region.BreadthEnd);
            return depthEnd <= depth || breadthEnd <= breadth
                ? null
                : new ComposeVolumeNetworkRegion(
                    "intersection",
                    depth,
                    breadth,
                    depthEnd - depth,
                    breadthEnd - breadth,
                    Array.Empty<string>());
        }

        private static ComposeVolumeNetworkRegion UnionRegions(
            IEnumerable<ComposeVolumeNetworkRegion> inputRegions)
        {
            ComposeVolumeNetworkRegion[] regions = inputRegions.ToArray();
            if (regions.Length == 0)
                return new ComposeVolumeNetworkRegion("empty", 0, 0, 1, 1, Array.Empty<string>());
            double depth = regions.Min(region => region.Depth);
            double breadth = regions.Min(region => region.Breadth);
            double depthEnd = regions.Max(region => region.DepthEnd);
            double breadthEnd = regions.Max(region => region.BreadthEnd);
            return new ComposeVolumeNetworkRegion(
                "union",
                depth,
                breadth,
                depthEnd - depth,
                breadthEnd - breadth,
                Array.Empty<string>());
        }

        private static IReadOnlyDictionary<string, int> BuildRegionComponents(
            IReadOnlyList<ComposeVolumeNetworkRegion> regions)
        {
            int[] parent = Enumerable.Range(0, regions.Count).ToArray();
            int Find(int index)
            {
                while (parent[index] != index)
                {
                    parent[index] = parent[parent[index]];
                    index = parent[index];
                }
                return index;
            }
            void Union(int left, int right)
            {
                int leftRoot = Find(left);
                int rightRoot = Find(right);
                if (leftRoot != rightRoot) parent[rightRoot] = leftRoot;
            }

            for (int left = 0; left < regions.Count; left++)
            {
                for (int right = left + 1; right < regions.Count; right++)
                {
                    if (RegionsOverlap(regions[left], regions[right]))
                        Union(left, right);
                }
            }

            var stableComponentByRoot = new Dictionary<int, int>();
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int nextComponent = 0;
            for (int index = 0; index < regions.Count; index++)
            {
                int root = Find(index);
                if (!stableComponentByRoot.TryGetValue(root, out int component))
                {
                    component = nextComponent++;
                    stableComponentByRoot[root] = component;
                }
                result[regions[index].Id] = component;
            }
            return result;
        }

        private static bool RegionsOverlap(
            ComposeVolumeNetworkRegion left,
            ComposeVolumeNetworkRegion right) =>
            left.Depth < right.DepthEnd &&
            left.DepthEnd > right.Depth &&
            left.Breadth < right.BreadthEnd &&
            left.BreadthEnd > right.Breadth;

        private static bool DepthRangesOverlap(
            ComposeVolumeNetworkRegion left,
            ComposeVolumeNetworkRegion right) =>
            left.Depth < right.DepthEnd && left.DepthEnd > right.Depth;

        private static bool Contains(
            ComposeVolumeNetworkRegion region,
            ComposeVolumeAxisPosition position,
            ComposeVolumeLayoutItem volume)
        {
            const double tolerance = 0.001;
            return
                position.Depth >= region.Depth - tolerance &&
                position.Breadth >= region.Breadth - tolerance &&
                position.Depth + volume.DepthSize <= region.DepthEnd + tolerance &&
                position.Breadth + volume.BreadthSize <= region.BreadthEnd + tolerance;
        }

        private sealed record VolumeCandidate(
            string VolumeId,
            string OwnerId,
            double Breadth,
            double BreadthCenter,
            int OwnerOrder,
            int LocalIndex);
    }
}
