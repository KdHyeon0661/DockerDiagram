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
            var node = new NodeViewModel(sheet.DockerService, sheet.DockerService, _dialogService)
            {
                Name = resource.Name,
                ImageName = resource.Kind == SwarmDataResourceKind.Secret ? "swarm-secret" : "swarm-config",
                Type = NodeType.Container,
                ParentSheet = sheet,
                X = x,
                Y = y,
                ContainerId = resource.Id,
                RuntimeKind = RuntimeKind.DockerSwarm,
                ResourceKind = resourceKind,
                BindingState = RuntimeBindingState.Bound,
                DetailStatus = $"Bound · {ShortId(resource.Id)}",
                StatusColor = resource.Kind == SwarmDataResourceKind.Secret ? "#C08A00" : "#4D7C6F",
                IsDockerConnected = true
            };
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
            var node = new NodeViewModel(sheet.DockerService, sheet.DockerService, _dialogService)
            {
                Name = options.Name,
                ImageName = options.Kind == SwarmDataResourceKind.Secret ? "swarm-secret" : "swarm-config",
                Type = NodeType.Container,
                ParentSheet = sheet,
                X = x,
                Y = y,
                RuntimeKind = RuntimeKind.DockerSwarm,
                ResourceKind = resourceKind,
                BindingState = RuntimeBindingState.Draft,
                DetailStatus = "Draft",
                StatusColor = "#7652A8",
                IsDockerConnected = false
            };
            sheet.Nodes.Add(node);
            IsModified = true;
            node.BindingState = RuntimeBindingState.Applying;
            node.IsCreating = true;
            node.SetCreationProgress($"Creating Swarm {options.Kind.ToString().ToLowerInvariant()}...");

            async Task RetryAsync()
            {
                ActiveSheet = sheet;
                await CreateSwarmDataResourceNodeAsync(options, x, y);
            }

            try
            {
                SwarmDataResourceMutationResult result = await mutationService.CreateSwarmDataResourceAsync(options);
                node.ContainerId = result.ResourceId;
                node.BindingState = RuntimeBindingState.Bound;
                node.ClearCreationFailure();
                node.IsCreating = false;
                node.IsDockerConnected = true;
                node.StatusColor = options.Kind == SwarmDataResourceKind.Secret ? "#C08A00" : "#4D7C6F";
                node.SetCreationProgress($"Swarm {options.Kind} created", 100);
                node.DetailStatus = $"Bound · {ShortId(result.ResourceId)}";
            }
            catch (Exception ex)
            {
                node.BindingState = RuntimeBindingState.Error;
                string message = $"Swarm {options.Kind} 생성 실패:\n{ex.GetBaseException().Message}";
                node.MarkCreationFailed(message, RetryAsync);
                _dialogService.ShowError(message, $"Create Swarm {options.Kind}");
            }
        }

        private static string ShortId(string id) => id.Length <= 12 ? id : id[..12];
    }
}
