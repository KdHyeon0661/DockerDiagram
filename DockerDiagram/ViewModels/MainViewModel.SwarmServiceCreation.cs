using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public partial class MainViewModel
    {
        public async Task CreateSwarmServiceNodeAsync(
            SwarmServiceCreateOptions options,
            double x,
            double y)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();

            SheetViewModel? creationSheet = ActiveSheet;
            if (creationSheet?.RuntimeKind != RuntimeKind.DockerSwarm)
            {
                _dialogService.ShowError("Swarm 서비스는 Docker Swarm 시트에서만 생성할 수 있습니다.", "Create Swarm Service");
                return;
            }

            if (creationSheet.IsRuntimeUnavailable ||
                creationSheet.DockerService is not ISwarmServiceMutationService mutationService)
            {
                _dialogService.ShowError(
                    "현재 Swarm Manager 연결에서는 서비스 생성 기능을 사용할 수 없습니다.",
                    "Create Swarm Service");
                return;
            }

            SwarmServiceSpecOptions spec = options.Spec;
            ulong desiredReplicas = spec.Replicas ?? 0;
            var node = new NodeViewModel(creationSheet.DockerService, creationSheet.DockerService, _dialogService)
            {
                Name = spec.Name,
                ImageName = spec.Image,
                Type = NodeType.Container,
                X = x,
                Y = y,
                RuntimeKind = RuntimeKind.DockerSwarm,
                ResourceKind = RuntimeResourceKind.SwarmService,
                BindingState = RuntimeBindingState.Draft,
                IsSwarmService = true,
                SwarmMode = SwarmServiceNodeProjection.ModeText(spec),
                SwarmDesiredReplicas = desiredReplicas,
                SwarmRunningReplicas = 0,
                TargetSwarmReplicas = desiredReplicas,
                PortBindings = SwarmServiceNodeProjection.PortBindings(spec),
                PortInfo = SwarmServiceNodeProjection.InitialSummary(spec),
                EnvironmentVariables = spec.EnvironmentVariables.ToList(),
                RestartPolicy = SwarmServiceNodeProjection.RestartText(spec.RestartPolicy),
                IsDockerConnected = false,
                IsRunning = false,
                DetailStatus = "Draft",
                StatusColor = "#7652A8"
            };

            creationSheet.Nodes.Add(node);
            IsModified = true;
            node.BindingState = RuntimeBindingState.Applying;
            node.IsCreating = true;
            node.SetCreationProgress("Creating Swarm service...");

            async Task RetryCreationAsync()
            {
                ActiveSheet = creationSheet;
                await CreateSwarmServiceNodeAsync(options, x, y);
            }

            SwarmServiceMutationResult result;
            try
            {
                result = await mutationService.CreateSwarmServiceAsync(options);
            }
            catch (Exception ex)
            {
                node.BindingState = RuntimeBindingState.Error;
                string message = $"Swarm 서비스 생성 실패:\n{ex.GetBaseException().Message}";
                node.MarkCreationFailed(message, RetryCreationAsync);
                _dialogService.ShowError(message, "Create Swarm Service");
                return;
            }

            node.ContainerId = result.ServiceId;
            node.BindingState = RuntimeBindingState.Bound;
            node.ClearCreationFailure();
            node.IsCreating = false;
            node.SetCreationProgress("Swarm service created", 100);
            node.IsDockerConnected = true;
            node.IsRunning = true;
            node.StatusColor = "#28A745";
            node.DetailStatus = SwarmServiceNodeProjection.InitialSummary(spec);

            // 일반 컨테이너 history는 Docker container API를 사용하므로 Swarm service에는 적용하지 않습니다.
            // Swarm service 생성/삭제는 확인 후 클러스터에 반영되는 영구 작업으로 취급합니다.
            try
            {
                await RestoreExistingSwarmServiceTopologyAsync(creationSheet, node);
                await node.RefreshSwarmServiceAsync();
                if (ReferenceEquals(ActiveSheet, creationSheet))
                    await RefreshRuntimeResourcesAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"서비스는 생성되었지만 다이어그램 관계 복원 또는 최신 상태 동기화에 실패했습니다:\n{ex.GetBaseException().Message}",
                    "Swarm Sync");
            }

            if (result.Warnings.Count > 0)
            {
                _dialogService.ShowInfo(
                    $"Swarm 서비스가 생성되었습니다.\n\n경고:\n{string.Join("\n", result.Warnings)}",
                    "Create Swarm Service");
            }
        }
    }
}
