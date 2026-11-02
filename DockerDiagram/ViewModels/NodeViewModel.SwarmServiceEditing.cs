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
            if (SwarmIngressNetworkSupport.RequiresIngress(desired) &&
                !await EnsureSwarmIngressNetworkAsync())
            {
                return false;
            }

            string previousStatus = DetailStatus;
            string previousColor = StatusColor;
            RuntimeLifecycleState previousLifecycleState = LifecycleState;
            BindingState = RuntimeBindingState.Applying;
            SetLifecycleState(RuntimeLifecycleState.Creating);
            SetCreationProgress(NodeOperationPhase.Updating, "Updating Swarm service...");
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
                EndCreating();
                SetCreationProgress(NodeOperationPhase.Completed, "Swarm service updated", 100);
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
                RestoreAfterEditFailure(previousStatus, previousColor, previousLifecycleState);
                await RefreshSwarmServiceAsync();
                _dialogService.ShowError(
                    $"다른 작업에서 service가 먼저 변경되었습니다. 최신 상태를 불러왔으므로 다시 편집해 주세요.\n\n{ex.Message}",
                    "Service Version Conflict");
                return false;
            }
            catch (Exception ex)
            {
                RestoreAfterEditFailure(previousStatus, previousColor, previousLifecycleState);
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

        private void RestoreAfterEditFailure(
            string previousStatus,
            string previousColor,
            RuntimeLifecycleState previousLifecycleState)
        {
            BindingState = RuntimeBindingState.Bound;
            SetLifecycleState(previousLifecycleState, previousStatus);
            SetCreationProgress(
                NodeOperationPhase.Failed,
                "Update failed",
                0,
                previousStatus,
                previousStatus);
            DetailStatus = previousStatus;
            StatusColor = previousColor;
            RaiseSwarmCommandStates();
        }

        private async Task<bool> EnsureSwarmIngressNetworkAsync()
        {
            if (_containerService is not INetworkService networkService)
            {
                _dialogService.ShowError(
                    "현재 연결에서는 Swarm ingress 네트워크 상태를 확인할 수 없습니다.",
                    "Ingress Check Failed");
                return false;
            }

            List<DockerNetworkGroup> networks;
            try
            {
                networks = await networkService.GetNetworksAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Swarm ingress 네트워크 상태를 확인할 수 없어 service를 수정하지 않았습니다.\n\n{ex.GetBaseException().Message}",
                    "Ingress Check Failed");
                return false;
            }

            if (SwarmIngressNetworkSupport.HasIngressNetwork(networks))
                return true;

            if (!_dialogService.ShowConfirm(
                    "이 변경은 ingress publish mode를 사용하지만 Docker가 관리하는 ingress 네트워크가 없습니다.\n\n" +
                    "지금 routing mesh용 ingress 네트워크를 다시 생성할까요?\n" +
                    "아니오를 선택하면 service는 수정되지 않습니다. 대신 포트 publish mode를 Host로 변경할 수 있습니다.",
                    "Repair Swarm Ingress"))
            {
                return false;
            }

            try
            {
                await networkService.CreateNetworkAsync(new NetworkCreateOptions
                {
                    Name = "ingress",
                    Driver = "overlay",
                    Ingress = true
                });
                networks = await networkService.GetNetworksAsync();
                if (!SwarmIngressNetworkSupport.HasIngressNetwork(networks))
                    throw new InvalidOperationException("Docker API가 ingress 네트워크 생성을 완료하지 못했습니다.");

                _dialogService.ShowInfo(
                    "Swarm routing mesh용 ingress 네트워크를 복구했습니다. service 변경을 계속합니다.",
                    "Ingress Repaired");
                return true;
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"ingress 네트워크 복구에 실패하여 service를 수정하지 않았습니다.\n" +
                    $"포트 publish mode를 Host로 바꾸거나 Docker Swarm 상태를 점검하세요.\n\n{ex.GetBaseException().Message}",
                    "Ingress Repair Failed");
                return false;
            }
        }
    }
}
