using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public partial class NodeViewModel
    {
        public async Task<bool> ApplySwarmDiagramTopologyAsync()
        {
            if (!CanControlSwarmService || ParentSheet?.RuntimeKind != RuntimeKind.DockerSwarm)
            {
                _dialogService.ShowError("현재 Swarm service에는 다이어그램 설정을 적용할 수 없습니다.", "Apply Swarm Diagram");
                return false;
            }

            IReadOnlyList<ConnectorViewModel> related = ParentSheet.Connectors
                .Where(connector => ReferenceEquals(connector.Source, this) || ReferenceEquals(connector.Target, this))
                .ToArray();
            if (related.Any(connector => connector.RelationType is RelationType.SwarmSecretReference or RelationType.SwarmConfigReference))
            {
                _dialogService.ShowError(
                    "Secret/Config 연결은 실제 Swarm 리소스 ID가 필요합니다. 아직 생성되지 않은 Draft Secret/Config 연결을 제거한 뒤 다시 적용해 주세요.",
                    "Apply Swarm Diagram");
                return false;
            }

            IReadOnlyList<GroupViewModel> overlayGroups = ParentSheet.Groups
                .Where(group => group.ResourceKind == RuntimeResourceKind.SwarmOverlayNetwork && group.ContainedNodes.Contains(this))
                .ToArray();

            if (!await ConfirmSwarmVolumeScopeAsync(related))
                return false;

            if (!related.Any(connector => connector.RelationType is RelationType.SwarmPublishedPort or RelationType.VolumeMount) &&
                overlayGroups.Count == 0 &&
                !SwarmDiagramManagesPublishedPorts &&
                !SwarmDiagramManagesMounts &&
                !SwarmDiagramManagesNetworks)
            {
                _dialogService.ShowInfo(
                    "적용할 published port, volume mount 또는 overlay network 관계가 없습니다.",
                    "Apply Swarm Diagram");
                return false;
            }

            try
            {
                await EnsureOverlayNetworksAsync(overlayGroups);
                SwarmServiceEditSnapshot snapshot = await LoadSwarmServiceEditSnapshotAsync();
                SwarmDiagramTopologyInput topology = BuildTopologyInput(related, overlayGroups);
                SwarmServiceSpecOptions compiled = SwarmDiagramTopologyCompiler.Apply(snapshot.Spec, topology);
                bool updated = await ApplySwarmServiceEditAsync(
                    snapshot,
                    new SwarmServiceCreateOptions { Spec = compiled });
                if (!updated) return false;

                RememberManagedTopology(topology);
                BindAppliedDraftResources(related);
                IsSwarmDiagramDirty = false;
                NotifyModified();
                return true;
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Swarm 다이어그램 적용 실패:\n{ex.GetBaseException().Message}",
                    "Apply Swarm Diagram");
                return false;
            }
        }

        private async Task EnsureOverlayNetworksAsync(IReadOnlyList<GroupViewModel> groups)
        {
            if (groups.Count == 0) return;
            if (_containerService is not INetworkService networkService)
                throw new InvalidOperationException("현재 연결은 Docker network 작업을 지원하지 않습니다.");

            List<DockerNetworkGroup> existing = await networkService.GetNetworksAsync();
            foreach (GroupViewModel group in groups)
            {
                DockerNetworkGroup? match = existing.FirstOrDefault(network =>
                        !string.IsNullOrWhiteSpace(group.Id) &&
                        string.Equals(network.Id, group.Id, StringComparison.OrdinalIgnoreCase))
                    ?? existing.FirstOrDefault(network =>
                        network.Name.Equals(group.DockerNetworkName, StringComparison.OrdinalIgnoreCase))
                    ?? existing.FirstOrDefault(network =>
                        network.Name.Equals(group.Title, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    throw new InvalidOperationException(
                        $"Overlay network '{group.DockerNetworkName}' does not exist on the Swarm manager. " +
                        "Create it with New Overlay Network or place an existing overlay network before applying the service.");
                }

                if (!SwarmResourceFilter.IsOverlayNetwork(match))
                    throw new InvalidOperationException($"'{match.Name}' is not a user-managed overlay network.");

                group.Id = match.Id;

                group.Driver = "overlay";
                group.BindingState = RuntimeBindingState.Bound;
                group.IsDockerConnected = true;
            }
        }

        private SwarmDiagramTopologyInput BuildTopologyInput(
            IReadOnlyList<ConnectorViewModel> related,
            IReadOnlyList<GroupViewModel> overlayGroups)
        {
            ConnectorViewModel[] portConnectors = related
                .Where(connector => connector.RelationType == RelationType.SwarmPublishedPort)
                .ToArray();
            ConnectorViewModel[] mountConnectors = related
                .Where(connector => connector.RelationType == RelationType.VolumeMount)
                .ToArray();

            return new SwarmDiagramTopologyInput
            {
                HasDiagramPublishedPorts = SwarmDiagramManagesPublishedPorts || portConnectors.Length > 0,
                PublishedPorts = portConnectors.Select(ParsePublishedPort).ToArray(),
                HasDiagramMounts = SwarmDiagramManagesMounts || mountConnectors.Length > 0,
                Mounts = mountConnectors.Select(ParseMount).ToArray(),
                HasDiagramNetworks = SwarmDiagramManagesNetworks || overlayGroups.Count > 0,
                Networks = overlayGroups.Select(group => new SwarmNetworkAttachmentOptions(
                    string.IsNullOrWhiteSpace(group.Id) ? group.Title : group.Id)).ToArray()
            };
        }

        private SwarmPublishedPortOptions ParsePublishedPort(ConnectorViewModel connector)
        {
            return connector.GetSwarmPublishedPortOptions();
        }

        private SwarmMountOptions ParseMount(ConnectorViewModel connector)
        {
            NodeViewModel? volume = connector.Source as NodeViewModel;
            if (ReferenceEquals(volume, this)) volume = connector.Target as NodeViewModel;
            if (volume?.ResourceKind != RuntimeResourceKind.SwarmVolume)
                throw new InvalidOperationException("Volume mount 연결의 Swarm Volume 노드를 찾을 수 없습니다.");

            string target = connector.MountPath?.Trim() ?? string.Empty;
            if (target.Length == 0)
                throw new ArgumentException($"Volume '{volume.Name}'의 mount target이 비어 있습니다.");
            return new SwarmMountOptions(
                SwarmMountKind.Volume,
                volume.EffectiveVolumeName,
                target,
                false,
                string.IsNullOrWhiteSpace(volume.Driver) ? "local" : volume.Driver,
                new Dictionary<string, string>(volume.VolumeDriverOptions),
                new Dictionary<string, string>(volume.VolumeLabels));
        }

        private async Task<bool> ConfirmSwarmVolumeScopeAsync(IReadOnlyList<ConnectorViewModel> related)
        {
            NodeViewModel[] volumes = related
                .Where(connector => connector.RelationType == RelationType.VolumeMount)
                .Select(connector => ReferenceEquals(connector.Source, this) ? connector.Target : connector.Source)
                .OfType<NodeViewModel>()
                .Where(node => node.ResourceKind == RuntimeResourceKind.SwarmVolume)
                .Distinct()
                .ToArray();
            if (volumes.Length == 0) return true;

            NodeViewModel[] externalVolumes = volumes.Where(volume => volume.VolumeExternal).ToArray();
            if (externalVolumes.Length > 0)
            {
                List<DockerVolume> managerVolumes = await _volumeService.GetVolumesAsync();
                string[] missing = externalVolumes
                    .Where(volume => !managerVolumes.Any(existing => string.Equals(
                        existing.Name,
                        volume.EffectiveVolumeName,
                        StringComparison.OrdinalIgnoreCase)))
                    .Select(volume => volume.EffectiveVolumeName)
                    .ToArray();
                if (missing.Length > 0)
                {
                    _dialogService.ShowError(
                        "현재 Manager 호스트에서 다음 external volume을 찾을 수 없습니다:\n" +
                        string.Join("\n", missing),
                        "Swarm Volume Validation");
                    return false;
                }
            }

            NodeViewModel[] localVolumes = volumes.Where(volume =>
                string.IsNullOrWhiteSpace(volume.Driver) ||
                volume.Driver.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                volume.Driver.Equals("-", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (localVolumes.Length == 0 || _containerService is not ISwarmService swarmService)
                return true;

            List<DockerSwarmNode> activeNodes = (await swarmService.GetSwarmNodesAsync())
                .Where(node => !node.Availability.Equals("drain", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (activeNodes.Count <= 1) return true;

            return _dialogService.ShowConfirm(
                "이 Service는 여러 Swarm 노드에서 실행될 수 있지만 다음 volume은 local driver를 사용합니다:\n" +
                string.Join("\n", localVolumes.Select(volume => $"- {volume.EffectiveVolumeName}")) +
                "\n\nlocal volume 데이터는 노드 사이에 자동 복제되지 않습니다. " +
                "placement constraint 또는 공유 스토리지 드라이버를 확인한 뒤 계속하세요.",
                "Swarm Volume Scope Warning");
        }

        private void BindAppliedDraftResources(IEnumerable<ConnectorViewModel> related)
        {
            foreach (NodeViewModel resource in related
                         .Select(connector => ReferenceEquals(connector.Source, this) ? connector.Target : connector.Source)
                         .OfType<NodeViewModel>()
                         .Where(node => node.ResourceKind is RuntimeResourceKind.SwarmVolume or RuntimeResourceKind.SwarmExternalTraffic))
            {
                resource.BindingState = RuntimeBindingState.Bound;
                bool isVolumeDeclaration = resource.ResourceKind == RuntimeResourceKind.SwarmVolume;
                resource.IsDockerConnected = !isVolumeDeclaration;
                resource.StatusColor = isVolumeDeclaration ? "#5B6B7A" : "#28A745";
                resource.DetailStatus = isVolumeDeclaration
                    ? $"Declared by service {Name} · created on task nodes"
                    : $"Published by service {Name}";
            }
        }

        private void RememberManagedTopology(SwarmDiagramTopologyInput topology)
        {
            if (topology.HasDiagramPublishedPorts)
                SwarmDiagramManagesPublishedPorts = true;
            if (topology.HasDiagramMounts)
                SwarmDiagramManagesMounts = true;
            if (topology.HasDiagramNetworks)
                SwarmDiagramManagesNetworks = true;
        }
    }
}
