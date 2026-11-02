using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
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
            if (SwarmIngressNetworkSupport.RequiresIngress(spec) &&
                !await EnsureSwarmIngressNetworkAsync(creationSheet))
            {
                return;
            }

            ulong desiredReplicas = spec.Replicas ?? 0;
            var node = DiagramNodeFactory.CreateSwarmNode(
                creationSheet.DockerService,
                creationSheet.DockerService,
                _dialogService,
                new SwarmNodeSpec(
                    RuntimeResourceKind.SwarmService,
                    spec.Name,
                    spec.Image,
                    x,
                    y,
                    creationSheet,
                    RuntimeBindingState.Draft,
                    IsConnected: false));
            node.SwarmMode = SwarmServiceNodeProjection.ModeText(spec);
            node.SwarmDesiredReplicas = desiredReplicas;
            node.SwarmRunningReplicas = 0;
            node.TargetSwarmReplicas = desiredReplicas;
            node.PortBindings = SwarmServiceNodeProjection.PortBindings(spec);
            node.PortInfo = SwarmServiceNodeProjection.InitialSummary(spec);
            node.EnvironmentVariables = spec.EnvironmentVariables.ToList();
            node.RestartPolicy = SwarmServiceNodeProjection.RestartText(spec.RestartPolicy);
            node.ApplyRuntimeState(RuntimeConnectionState.Disconnected, RuntimeLifecycleState.Stopped);
            node.DetailStatus = "Draft";
            node.StatusColor = "#7652A8";

            creationSheet.Nodes.Add(node);
            IsModified = true;

            async Task ApplyCreationAsync(bool revalidate)
            {
                if (revalidate)
                {
                    if (SwarmIngressNetworkSupport.RequiresIngress(spec) &&
                        !await EnsureSwarmIngressNetworkAsync(creationSheet))
                    {
                        return;
                    }
                }

                node.BindingState = RuntimeBindingState.Applying;
                node.ClearCreationFailure();
                node.SetLifecycleState(RuntimeLifecycleState.Creating);
                node.SetCreationProgress(NodeOperationPhase.Creating, "Creating Swarm service...");

                SwarmServiceMutationResult result;
                try
                {
                    result = await mutationService.CreateSwarmServiceAsync(options);
                }
                catch (Exception ex)
                {
                    node.BindingState = RuntimeBindingState.Error;
                    string message = $"Swarm 서비스 생성 실패:\n{ex.GetBaseException().Message}";
                    node.MarkCreationFailed(
                        message,
                        () => ApplyCreationAsync(revalidate: true),
                        removeBeforeRetry: false);
                    _dialogService.ShowError(message, "Create Swarm Service");
                    return;
                }

                node.ContainerId = result.ServiceId;
                node.BindingState = RuntimeBindingState.Bound;
                node.ClearCreationFailure();
                node.EndCreating();
                node.SetCreationProgress(NodeOperationPhase.Completed, "Swarm service created", 100);
                node.ApplyRuntimeState(RuntimeConnectionState.Connected, RuntimeLifecycleState.Running);
                node.StatusColor = StatusColors.Running;
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

            await ApplyCreationAsync(revalidate: false);
        }

        private async Task<bool> EnsureSwarmIngressNetworkAsync(SheetViewModel sheet)
        {
            var networkService = (INetworkService)sheet.DockerService;
            List<DockerNetworkGroup> networks;
            try
            {
                networks = await networkService.GetNetworksAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Swarm ingress 네트워크 상태를 확인할 수 없어 서비스를 생성하지 않았습니다.\n\n{ex.GetBaseException().Message}",
                    "Ingress Check Failed");
                return false;
            }

            if (SwarmIngressNetworkSupport.HasIngressNetwork(networks))
                return true;

            bool repair = _dialogService.ShowConfirm(
                "이 서비스는 ingress publish mode를 사용하지만 Docker가 관리하는 ingress 네트워크가 없습니다.\n\n" +
                "지금 routing mesh용 ingress 네트워크를 다시 생성할까요?\n" +
                "아니오를 선택하면 서비스는 생성되지 않습니다. 대신 포트 publish mode를 Host로 변경할 수 있습니다.",
                "Repair Swarm Ingress");
            if (!repair)
                return false;

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
                    "Swarm routing mesh용 ingress 네트워크를 복구했습니다. 서비스를 계속 생성합니다.",
                    "Ingress Repaired");
                return true;
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"ingress 네트워크 복구에 실패하여 서비스를 생성하지 않았습니다.\n" +
                    $"포트 publish mode를 Host로 바꾸거나 Docker Swarm 상태를 점검하세요.\n\n{ex.GetBaseException().Message}",
                    "Ingress Repair Failed");
                return false;
            }
        }
    }
}
