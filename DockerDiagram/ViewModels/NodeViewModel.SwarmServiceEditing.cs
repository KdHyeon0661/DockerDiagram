using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.ViewModels
{
    public partial class NodeViewModel
    {
        public async Task<SwarmServiceEditSnapshot> LoadSwarmServiceEditSnapshotAsync()
        {
            if (!CanControlSwarmService || _containerService is not ISwarmService swarmService)
                throw new InvalidOperationException("현재 Swarm service는 편집할 수 없습니다.");

            object raw = await swarmService.InspectSwarmServiceRawAsync(ContainerId);
            SwarmServiceEditSnapshot snapshot = SwarmServiceSpecReader.Read(raw);
            if (!snapshot.ServiceId.Equals(ContainerId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("편집 대상 service ID가 현재 노드와 일치하지 않습니다.");

            SwarmServiceInspectJson = snapshot.RawJson;
            return snapshot;
        }

        public async Task<bool> ApplySwarmServiceEditAsync(
            SwarmServiceEditSnapshot snapshot,
            SwarmServiceCreateOptions editedOptions)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(editedOptions);
            editedOptions.Validate();

            if (!CanControlSwarmService || _containerService is not ISwarmServiceMutationService mutationService)
            {
                _dialogService.ShowError("현재 Swarm Manager 연결에서는 service를 수정할 수 없습니다.", "Edit Swarm Service");
                return false;
            }

            if (!snapshot.ServiceId.Equals(ContainerId, StringComparison.OrdinalIgnoreCase))
            {
                _dialogService.ShowError("편집 대상 service가 현재 노드와 일치하지 않습니다.", "Edit Swarm Service");
                return false;
            }

            SwarmServiceSpecOptions desired = SwarmServiceEditFormatter.PreserveHiddenSettings(
                snapshot.Spec,
                editedOptions.Spec);
            desired.Validate();

            string previousStatus = DetailStatus;
            string previousColor = StatusColor;
            BindingState = RuntimeBindingState.Applying;
            IsCreating = true;
            SetCreationProgress("Updating Swarm service...");
            RaiseSwarmCommandStates();

            try
            {
                SwarmServiceMutationResult result = await mutationService.UpdateSwarmServiceAsync(
                    new SwarmServiceUpdateOptions
                    {
                        ServiceId = snapshot.ServiceId,
                        Version = snapshot.Version,
                        Spec = desired
                    });

                ApplyEditedSummary(desired);
                BindingState = RuntimeBindingState.Bound;
                IsCreating = false;
                SetCreationProgress("Swarm service updated", 100);
                await RefreshSwarmServiceAsync();
                BindingState = RuntimeBindingState.Bound;
                NotifyModified();
                RaiseSwarmCommandStates();

                string warningText = result.Warnings.Count == 0
                    ? string.Empty
                    : $"\n\n경고:\n{string.Join("\n", result.Warnings)}";
                _dialogService.ShowInfo($"'{Name}' service 설정을 갱신했습니다.{warningText}", "Edit Swarm Service");
                return true;
            }
            catch (SwarmServiceVersionConflictException ex)
            {
                RestoreAfterEditFailure(previousStatus, previousColor);
                await RefreshSwarmServiceAsync();
                _dialogService.ShowError(
                    $"다른 작업에서 service가 먼저 변경되었습니다. 최신 상태를 불러왔으므로 다시 편집해 주세요.\n\n{ex.Message}",
                    "Service Version Conflict");
                return false;
            }
            catch (Exception ex)
            {
                RestoreAfterEditFailure(previousStatus, previousColor);
                _dialogService.ShowError($"Swarm service 수정 실패:\n{ex.GetBaseException().Message}", "Edit Swarm Service");
                return false;
            }
        }

        private void ApplyEditedSummary(SwarmServiceSpecOptions spec)
        {
            Name = spec.Name;
            ImageName = spec.Image;
            SwarmMode = SwarmServiceNodeProjection.ModeText(spec);
            SwarmDesiredReplicas = spec.Replicas ?? 0;
            TargetSwarmReplicas = SwarmDesiredReplicas;
            PortBindings = SwarmServiceNodeProjection.PortBindings(spec);
            PortInfo = SwarmServiceNodeProjection.InitialSummary(spec);
            EnvironmentVariables = spec.EnvironmentVariables.ToList();
            RestartPolicy = SwarmServiceNodeProjection.RestartText(spec.RestartPolicy);
        }

        private void RestoreAfterEditFailure(string previousStatus, string previousColor)
        {
            BindingState = RuntimeBindingState.Bound;
            IsCreating = false;
            CreationProgressMessage = "Update failed";
            CreationProgressValue = 0;
            IsCreationProgressIndeterminate = false;
            DetailStatus = previousStatus;
            StatusColor = previousColor;
            RaiseSwarmCommandStates();
        }
    }
}
