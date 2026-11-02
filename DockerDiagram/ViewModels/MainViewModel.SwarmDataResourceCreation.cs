using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public partial class MainViewModel
    {
        public Task PlaceExistingSwarmDataResourceNodeAsync(
            SwarmDataResourceSnapshot resource,
            double x,
            double y)
        {
            ArgumentNullException.ThrowIfNull(resource);
            SheetViewModel? sheet = ActiveSheet;
            if (sheet?.RuntimeKind != RuntimeKind.DockerSwarm) return Task.CompletedTask;

            RuntimeResourceKind resourceKind = resource.Kind == SwarmDataResourceKind.Secret
                ? RuntimeResourceKind.SwarmSecret
                : RuntimeResourceKind.SwarmConfig;
            if (sheet.Nodes.Any(node =>
                    node.ResourceKind == resourceKind &&
                    ((!string.IsNullOrWhiteSpace(resource.Id) &&
                      string.Equals(node.ContainerId, resource.Id, StringComparison.OrdinalIgnoreCase)) ||
                     string.Equals(node.Name, resource.Name, StringComparison.OrdinalIgnoreCase))))
            {
                return Task.CompletedTask;
            }

            var historyBefore = CaptureDiagramState(sheet);
            var node = DiagramNodeFactory.CreateSwarmNode(
                sheet.DockerService,
                sheet.DockerService,
                _dialogService,
                new SwarmNodeSpec(
                    resourceKind,
                    resource.Name,
                    resource.Kind == SwarmDataResourceKind.Secret ? "swarm-secret" : "swarm-config",
                    x,
                    y,
                    sheet,
                    RuntimeBindingState.Bound,
                    IsConnected: true));
            node.ContainerId = resource.Id;
            node.DetailStatus = $"Bound · {DockerIdFormatter.Shorten(resource.Id)}";
            node.StatusColor = resource.Kind == SwarmDataResourceKind.Secret ? "#C08A00" : "#4D7C6F";
            sheet.Nodes.Add(node);
            IsModified = true;
            RecordAdditionsFromSnapshot(sheet, historyBefore, $"Add existing Swarm {resource.Kind}", affectsDocker: false);
            return Task.CompletedTask;
        }

        public async Task CreateSwarmDataResourceNodeAsync(
            SwarmDataResourceCreateOptions options,
            double x,
            double y)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            SheetViewModel? sheet = ActiveSheet;
            if (sheet?.RuntimeKind != RuntimeKind.DockerSwarm ||
                sheet.IsRuntimeUnavailable ||
                sheet.DockerService is not ISwarmDataResourceMutationService mutationService)
            {
                _dialogService.ShowError("활성 Swarm Manager 연결이 없습니다.", $"Create Swarm {options.Kind}");
                return;
            }

            RuntimeResourceKind resourceKind = options.Kind == SwarmDataResourceKind.Secret
                ? RuntimeResourceKind.SwarmSecret
                : RuntimeResourceKind.SwarmConfig;
            var node = DiagramNodeFactory.CreateSwarmNode(
                sheet.DockerService,
                sheet.DockerService,
                _dialogService,
                new SwarmNodeSpec(
                    resourceKind,
                    options.Name,
                    options.Kind == SwarmDataResourceKind.Secret ? "swarm-secret" : "swarm-config",
                    x,
                    y,
                    sheet,
                    RuntimeBindingState.Draft,
                    IsConnected: false));
            node.DetailStatus = "Draft";
            node.StatusColor = "#7652A8";
            sheet.Nodes.Add(node);
            IsModified = true;

            async Task ApplyCreationAsync()
            {
                node.BindingState = RuntimeBindingState.Applying;
                node.ClearCreationFailure();
                node.SetLifecycleState(RuntimeLifecycleState.Creating);
                node.SetCreationProgress(
                    NodeOperationPhase.Creating,
                    $"Creating Swarm {options.Kind.ToString().ToLowerInvariant()}...");

                SwarmDataResourceMutationResult result;
                try
                {
                    result = await mutationService.CreateSwarmDataResourceAsync(options);
                }
                catch (Exception ex)
                {
                    node.BindingState = RuntimeBindingState.Error;
                    string message = $"Swarm {options.Kind} 생성 실패:\n{ex.GetBaseException().Message}";
                    node.MarkCreationFailed(message, ApplyCreationAsync, removeBeforeRetry: false);
                    _dialogService.ShowError(message, $"Create Swarm {options.Kind}");
                    return;
                }

                node.ContainerId = result.ResourceId;
                node.BindingState = RuntimeBindingState.Bound;
                node.ClearCreationFailure();
                node.EndCreating();
                node.SetDockerConnection(true);
                node.StatusColor = options.Kind == SwarmDataResourceKind.Secret ? "#C08A00" : "#4D7C6F";
                node.SetCreationProgress(
                    NodeOperationPhase.Completed,
                    $"Swarm {options.Kind} created",
                    100);
                node.DetailStatus = $"Bound · {DockerIdFormatter.Shorten(result.ResourceId)}";

                if (ReferenceEquals(ActiveSheet, sheet))
                {
                    try
                    {
                        await RefreshRuntimeResourcesAsync();
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowError(
                            $"Swarm {options.Kind}은 생성되었지만 리소스 목록 동기화에 실패했습니다:\n{ex.GetBaseException().Message}",
                            "Swarm Sync");
                    }
                }
            }

            await ApplyCreationAsync();
        }

    }
}
