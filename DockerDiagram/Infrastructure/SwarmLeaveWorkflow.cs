using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.Infrastructure
{
    public sealed record SwarmLeavePlan(string NodeId, SwarmMembershipState Membership, bool Force);
    public sealed record SwarmLeaveResult(bool ConfirmedInactive, string Detail);

    public static class SwarmLeavePolicy
    {
        public static SwarmLeavePlan Evaluate(SwarmClusterState state, IReadOnlyList<DockerSwarmNode>? nodes = null)
        {
            if (state.IsWorker && !string.IsNullOrWhiteSpace(state.NodeId))
                return new(state.NodeId, state.Membership, false);

            if (state.IsManager && !string.IsNullOrWhiteSpace(state.NodeId))
            {
                if (nodes == null || nodes.Count == 0 ||
                    !nodes.Any(node => node.Id == state.NodeId && node.Role.Equals("manager", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("현재 Manager와 클러스터 노드 구성을 확인하지 못했습니다. 새로고침 후 다시 확인해 주세요.");
                if (nodes.Count != 1)
                    throw new InvalidOperationException(
                        "여러 노드가 있는 클러스터의 Manager는 바로 탈퇴할 수 없습니다. " +
                        "다른 Manager에서 이 노드를 Worker로 강등한 뒤 탈퇴해 주세요. " +
                        "유일한 Manager라면 먼저 다른 Manager를 확보해야 합니다.");
                return new(state.NodeId, state.Membership, true);
            }

            throw new InvalidOperationException($"탈퇴할 활성 노드를 확인하지 못했습니다. 현재 상태: {state.Membership}");
        }
    }

    /// <summary>확인창 전후의 노드 상태를 재검증하고, 응답 유실 시 실제 탈퇴 여부를 조회합니다.</summary>
    public sealed class SwarmLeaveWorkflow(ISwarmService service)
    {
        public async Task<SwarmLeavePlan> PrepareAsync(CancellationToken cancellationToken = default)
        {
            SwarmClusterState state = await ReadStateAsync(service, cancellationToken);
            var nodes = state.IsManager
                ? await service.GetSwarmNodesAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                : null;
            return SwarmLeavePolicy.Evaluate(state, nodes);
        }

        public async Task<SwarmLeaveResult> ExecuteAsync(SwarmLeavePlan confirmedPlan, CancellationToken cancellationToken = default)
        {
            SwarmLeavePlan current = await PrepareAsync(cancellationToken);
            if (current != confirmedPlan)
                throw new InvalidOperationException("확인 후 노드 ID 또는 역할이 변경되었습니다. 탈퇴 조건을 다시 확인해 주세요.");

            string requestError = string.Empty;
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await service.LeaveSwarmAsync(current.Force, requestCts.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                requestError = ex is OperationCanceledException ? "탈퇴 요청 응답 시간 초과" : ex.GetBaseException().Message;
            }

            SwarmClusterState? latest = null;
            try
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    latest = await ReadStateAsync(service, cancellationToken);
                    if (latest.Membership == SwarmMembershipState.Inactive)
                        return new(true, "Docker Engine의 미가입 상태를 확인했습니다.");
                    await Task.Delay(250, cancellationToken);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 요청을 자동 반복하지 않습니다. 사용자가 새로고침으로 관측 상태를 확인합니다.
            }
            return new(false,
                $"탈퇴 완료를 확인하지 못했습니다. 현재 상태: {latest?.Membership.ToString() ?? "확인 불가"}. " +
                "요청이 처리되었을 수 있으므로 새로고침으로 상태를 확인해 주세요." +
                (requestError.Length == 0 ? string.Empty : $"\n{requestError}"));
        }

        public static Task<SwarmClusterState> ReadStateAsync(ISwarmService service, CancellationToken cancellationToken = default) =>
            service.GetSwarmStateAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }
}
