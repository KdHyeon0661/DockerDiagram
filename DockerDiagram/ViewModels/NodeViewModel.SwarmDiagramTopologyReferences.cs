using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public partial class NodeViewModel
    {
        /// <summary>
        /// Applies diagram topology and bound Swarm Secret/Config references.
        /// Payload data is never read from or written to the diagram model.
        /// </summary>
        public async Task<bool> ApplySwarmDiagramTopologyWithReferencesAsync()
        {
            if (!CanControlSwarmService || ParentSheet?.RuntimeKind != RuntimeKind.DockerSwarm)
            {
                _dialogService.ShowError("현재 Swarm service에는 다이어그램 설정을 적용할 수 없습니다.", "Apply Swarm Diagram");
                return false;
            }

            IReadOnlyList<ConnectorViewModel> related = ParentSheet.Connectors
                .Where(connector => ReferenceEquals(connector.Source, this) || ReferenceEquals(connector.Target, this))
                .ToArray();

            IReadOnlyList<(ConnectorViewModel Connector, NodeViewModel Resource)> secretReferences = ResolveReferencedResources(
                related,
                RelationType.SwarmSecretReference,
                RuntimeResourceKind.SwarmSecret);
            IReadOnlyList<(ConnectorViewModel Connector, NodeViewModel Resource)> configReferences = ResolveReferencedResources(
                related,
                RelationType.SwarmConfigReference,
                RuntimeResourceKind.SwarmConfig);

            string[] unavailable = secretReferences.Concat(configReferences)
                .Select(reference => reference.Resource)
                .Where(node => node.BindingState != RuntimeBindingState.Bound ||
                               !node.IsDockerConnected ||
                               string.IsNullOrWhiteSpace(node.ContainerId))
                .Select(node => node.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unavailable.Length > 0)
            {
                _dialogService.ShowError(
                    $"다음 Swarm 리소스를 먼저 생성하거나 연결해 주세요:\n{string.Join("\n", unavailable)}",
                    "Apply Swarm Diagram");
                return false;
            }

            bool hasSecretRelations = related.Any(connector => connector.RelationType == RelationType.SwarmSecretReference);
            bool hasConfigRelations = related.Any(connector => connector.RelationType == RelationType.SwarmConfigReference);
            if ((hasSecretRelations && secretReferences.Count == 0) || (hasConfigRelations && configReferences.Count == 0))
            {
                _dialogService.ShowError(
                    "Secret/Config 연결 대상의 리소스 종류가 올바르지 않습니다.",
                    "Apply Swarm Diagram");
                return false;
            }

            if (!await ConfirmSwarmVolumeScopeAsync(related))
                return false;

            if (_containerService is not ISwarmDataResourceMutationService resourceService)
            {
                _dialogService.ShowError(
                    "현재 Docker 연결은 Swarm Secret/Config 참조 업데이트를 지원하지 않습니다.",
                    "Apply Swarm Diagram");
                return false;
            }

            IReadOnlyList<GroupViewModel> overlayGroups = ParentSheet.Groups
                .Where(group => group.ResourceKind == RuntimeResourceKind.SwarmOverlayNetwork &&
                                group.ContainedNodes.Contains(this))
                .ToArray();

            string previousStatus = DetailStatus;
            string previousColor = StatusColor;
            BindingState = RuntimeBindingState.Applying;
            IsCreating = true;
            SetCreationProgress("Applying diagram topology and Swarm data references...");
            RaiseSwarmCommandStates();

            try
            {
                await EnsureOverlayNetworksAsync(overlayGroups);
                SwarmServiceEditSnapshot snapshot = await LoadSwarmServiceEditSnapshotAsync();
                SwarmDiagramTopologyInput topology = BuildTopologyInput(related, overlayGroups);
                SwarmServiceSpecOptions compiled = SwarmDiagramTopologyCompiler.Apply(snapshot.Spec, topology);

                SwarmServiceMutationResult result = await resourceService.UpdateSwarmServiceTopologyAsync(
                    new SwarmServiceTopologyUpdateOptions
                    {
                        Service = new SwarmServiceUpdateOptions
                        {
                            ServiceId = snapshot.ServiceId,
                            Version = snapshot.Version,
                            Spec = compiled
                        },
                        ApplySecrets = hasSecretRelations,
                        Secrets = secretReferences.Select(ToResourceReference).ToArray(),
                        ApplyConfigs = hasConfigRelations,
                        Configs = configReferences.Select(ToResourceReference).ToArray()
                    });

                RememberManagedTopology(topology);
                ApplyEditedSummary(compiled);
                BindAppliedDraftResources(related);
                BindingState = RuntimeBindingState.Bound;
                IsCreating = false;
                SetCreationProgress("Diagram topology applied", 100);
                await RefreshSwarmServiceAsync();
                BindingState = RuntimeBindingState.Bound;
                IsSwarmDiagramDirty = false;
                NotifyModified();
                RaiseSwarmCommandStates();

                string warningText = result.Warnings.Count == 0
                    ? string.Empty
                    : $"\n\n경고:\n{string.Join("\n", result.Warnings)}";
                _dialogService.ShowInfo(
                    $"다이어그램 토폴로지와 Swarm Secret/Config 참조를 적용했습니다.{warningText}",
                    "Apply Swarm Diagram");
                return true;
            }
            catch (SwarmServiceVersionConflictException ex)
            {
                RestoreAfterEditFailure(previousStatus, previousColor);
                await RefreshSwarmServiceAsync();
                _dialogService.ShowError(
                    $"다른 작업에서 service가 먼저 변경되었습니다. 최신 상태를 불러왔으므로 다시 적용해 주세요.\n\n{ex.Message}",
                    "Swarm Service Conflict");
                return false;
            }
            catch (Exception ex)
            {
                RestoreAfterEditFailure(previousStatus, previousColor);
                _dialogService.ShowError(
                    $"Swarm 다이어그램 적용 실패:\n{ex.GetBaseException().Message}",
                    "Apply Swarm Diagram");
                return false;
            }
        }

        private List<(ConnectorViewModel Connector, NodeViewModel Resource)> ResolveReferencedResources(
            IEnumerable<ConnectorViewModel> connectors,
            RelationType relationType,
            RuntimeResourceKind expectedKind)
        {
            return connectors
                .Where(connector => connector.RelationType == relationType)
                .Select(connector => (
                    Connector: connector,
                    Resource: (ReferenceEquals(connector.Source, this) ? connector.Target : connector.Source) as NodeViewModel))
                .Where(reference => reference.Resource?.ResourceKind == expectedKind)
                .Select(reference => (reference.Connector, reference.Resource!))
                .ToList();
        }

        private static SwarmServiceResourceReferenceOptions ToResourceReference(
            (ConnectorViewModel Connector, NodeViewModel Resource) reference) =>
            reference.Connector.GetSwarmResourceReferenceOptions(
                reference.Resource.ContainerId,
                reference.Resource.Name);
    }
}
