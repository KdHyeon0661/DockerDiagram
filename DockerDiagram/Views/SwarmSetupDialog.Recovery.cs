using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class SwarmSetupDialog
    {
        private void UpdateActionAvailability()
        {
            if (InitializeButton == null || CloseButton == null) return;
            bool idle = !_isLoading;
            bool manager = idle && _currentState?.IsManager == true;
            bool worker = idle && _currentState?.IsWorker == true;
            RefreshButton.IsEnabled = idle;
            CloseButton.IsEnabled = idle;
            InitializeButton.IsEnabled = idle && _initializationAddressReady &&
                                         _currentState?.Membership == SwarmMembershipState.Inactive;
            AddTargetNodeButton.IsEnabled = manager && !_isDockerDesktopLocalDemo;
            JoinInfoButton.IsEnabled = manager;
            WorkerManagerInfoButton.IsEnabled = worker;
            LeaveWorkerButton.IsEnabled = worker;
            LeaveManagerButton.IsEnabled = manager;
            JoinTargetButton.IsEnabled = manager && !_isDockerDesktopLocalDemo && _targetConnection != null && _targetCanJoin;
            RecheckTargetButton.IsEnabled = idle && _targetConnection != null;
            DisconnectTargetButton.IsEnabled = idle && _targetConnection != null;
            TargetManagerAddressTextBox.IsEnabled = idle && _targetConnection != null && _targetCanJoin;
        }

        private async void LeaveSwarm_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            ClearJoinSecrets();
            LoadingText.Text = "탈퇴할 노드와 클러스터 구성 확인 중...";
            var workflow = new SwarmLeaveWorkflow(_swarmService);
            try
            {
                SwarmLeavePlan plan = await workflow.PrepareAsync(_lifetimeCts.Token);
                string effect = plan.Force
                    ? "이 노드만 있는 Swarm 클러스터를 종료합니다. Swarm 서비스 관리 상태가 사라지며 되돌리려면 다시 구성해야 합니다."
                    : "이 Docker Engine이 Worker 역할을 종료합니다. 실행 중인 Swarm 작업이 중단될 수 있습니다. " +
                      "Manager 목록에는 down 노드로 남을 수 있습니다.";
                if (!_dialogService.ShowConfirm(
                        $"현재 연결된 Docker Engine을 Swarm에서 탈퇴시킵니다.\n\n" +
                        $"Node ID: {plan.NodeId}\n역할: {plan.Membership}\n\n{effect}\n\n진행하시겠습니까?",
                        plan.Force ? "단일 노드 Swarm 종료" : "Worker Swarm 탈퇴"))
                    return;

                LoadingText.Text = "Swarm 탈퇴 요청 및 실제 상태 확인 중...";
                SwarmLeaveResult result = await workflow.ExecuteAsync(plan, _lifetimeCts.Token);
                if (result.ConfirmedInactive)
                {
                    WasInitialized = false;
                    ClearTargetConnection();
                    _dialogService.ShowInfo(result.Detail, "Swarm 탈퇴 완료");
                }
                else
                    _dialogService.ShowInfo(result.Detail, "Swarm 탈퇴 확인 대기");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(ex.GetBaseException().Message, "Swarm 탈퇴");
            }
            finally
            {
                _isLoading = false;
                LoadingText.Text = string.Empty;
                await RefreshStateAsync();
            }
        }

        private async void RecheckTarget_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || _targetConnection == null) return;
            _isLoading = true;
            LoadingText.Text = "대상 PC와 Manager 상태 재확인 중...";
            try { await RecoverTargetAsync(); }
            finally
            {
                ClearJoinSecrets();
                _isLoading = false;
                LoadingText.Text = string.Empty;
                UpdateActionAvailability();
            }
        }

        private async Task RecoverTargetAsync(string detail = "")
        {
            if (_targetConnection == null) return;
            SwarmTargetConnectionSession connection = _targetConnection;
            SwarmClusterState? observed = await TryReadTargetStateAsync(connection.DockerService);
            try
            {
                if (observed != null && SwarmJoinVerification.HasExpectedRole(observed, connection.Options.Role) &&
                    !string.IsNullOrWhiteSpace(observed.NodeId))
                {
                    var nodes = await _swarmService.GetSwarmNodesAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10), _lifetimeCts.Token);
                    var match = SwarmJoinVerification.FindJoinedNode(
                        nodes, observed.NodeId, connection.Options.AdvertiseAddress, connection.Options.Role);
                    if (match != null)
                    {
                        CompleteTargetJoin(connection.Options, observed, match);
                        return;
                    }
                    detail = "대상은 가입 상태이지만 현재 Manager의 노드 목록에서 같은 Node ID를 찾지 못했습니다. " +
                             "목록 반영 지연 또는 다른 클러스터 가입 여부를 확인해 주세요.";
                }
            }
            catch (Exception ex)
            {
                detail = "Manager 조회 실패: " + SafeJoinError(ex, string.Empty);
            }
            PresentTargetRecovery(observed, detail);
        }

        private void PresentTargetRecovery(SwarmClusterState? state, string detail)
        {
            if (_targetConnection == null) return;
            _targetCanJoin = state?.Membership == SwarmMembershipState.Inactive;
            string action = state?.Membership switch
            {
                SwarmMembershipState.Inactive => "미가입 상태입니다. 주소와 연결 정보를 확인한 뒤 Join을 다시 시도할 수 있습니다.",
                SwarmMembershipState.Pending => "가입 처리 중입니다. 잠시 뒤 대상 상태를 다시 확인해 주세요.",
                SwarmMembershipState.Worker or SwarmMembershipState.Manager =>
                    "이미 가입한 노드입니다. 역할과 현재 Manager 목록을 다시 확인해 주세요.",
                SwarmMembershipState.Locked => "대상 Docker Engine의 Swarm 잠금을 해제한 뒤 상태를 다시 확인해 주세요.",
                SwarmMembershipState.Error => "대상 Docker Engine의 Swarm 오류를 해결한 뒤 상태를 다시 확인해 주세요.",
                _ => "상태를 읽지 못했습니다. Docker 실행 상태와 SSH 연결을 확인해 주세요. 재연결하려면 다른 PC를 노드로 추가를 다시 누르세요."
            };
            var target = _targetConnection.Options;
            TargetConnectionSummaryText.Text =
                $"{target.Role} · {target.Host}\n대상 상태: {state?.Membership.ToString() ?? "확인 불가"}\n" +
                action + (string.IsNullOrWhiteSpace(detail) ? string.Empty : "\n" + detail);
            JoinTargetButton.Content = _targetCanJoin ? "Swarm Join 다시 시도" : "상태 재확인 필요";
            TargetConnectionPanel.Visibility = Visibility.Visible;
            UpdateActionAvailability();
        }

        private static string SafeJoinError(Exception exception, string token)
        {
            string message = exception is OperationCanceledException
                ? "Join 요청 응답 시간이 초과되었습니다. 실제 가입 상태를 다시 확인합니다."
                : exception.GetBaseException().Message;
            if (!string.IsNullOrWhiteSpace(token))
                message = message.Replace(token, "<hidden>", StringComparison.Ordinal);
            return Regex.Replace(message, @"SWMTKN-[^\s""'<>]+", "<hidden>");
        }
    }
}
