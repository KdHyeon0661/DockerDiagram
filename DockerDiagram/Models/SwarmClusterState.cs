using System;
using System.Collections.Generic;

namespace DockerDiagram.Models
{
    /// <summary>
    /// 현재 Docker Engine이 Swarm에서 맡고 있는 로컬 역할입니다.
    /// Docker /info의 LocalNodeState와 ControlAvailable을 함께 해석합니다.
    /// </summary>
    public enum SwarmMembershipState
    {
        Unknown,
        Inactive,
        Pending,
        Manager,
        Worker,
        Locked,
        Error
    }

    public sealed record SwarmManagerEndpoint(string NodeId, string Address);

    /// <summary>
    /// 한 Docker Engine의 Swarm 가입 상태를 UI와 서비스 계층에서 공유하기 위한 읽기 전용 모델입니다.
    /// Manager 전용 nodes API를 호출하지 않아도 Worker와 미가입 상태를 구분할 수 있습니다.
    /// </summary>
    public sealed class SwarmClusterState
    {
        public SwarmMembershipState Membership { get; init; } = SwarmMembershipState.Unknown;
        public string LocalNodeState { get; init; } = string.Empty;
        public bool ControlAvailable { get; init; }
        public string NodeId { get; init; } = string.Empty;
        public string NodeAddress { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
        public IReadOnlyList<SwarmManagerEndpoint> RemoteManagers { get; init; } = Array.Empty<SwarmManagerEndpoint>();

        public bool IsActive => Membership is SwarmMembershipState.Manager or SwarmMembershipState.Worker;
        public bool IsManager => Membership == SwarmMembershipState.Manager;
        public bool IsWorker => Membership == SwarmMembershipState.Worker;

        public static SwarmClusterState Create(
            string? localNodeState,
            bool controlAvailable,
            string? nodeId = null,
            string? nodeAddress = null,
            string? errorMessage = null,
            IReadOnlyList<SwarmManagerEndpoint>? remoteManagers = null)
        {
            string normalizedState = (localNodeState ?? string.Empty).Trim().ToLowerInvariant();
            string normalizedError = (errorMessage ?? string.Empty).Trim();

            SwarmMembershipState membership = normalizedState switch
            {
                "inactive" => SwarmMembershipState.Inactive,
                "pending" => SwarmMembershipState.Pending,
                "active" when controlAvailable => SwarmMembershipState.Manager,
                "active" => SwarmMembershipState.Worker,
                "locked" => SwarmMembershipState.Locked,
                "error" => SwarmMembershipState.Error,
                _ when !string.IsNullOrWhiteSpace(normalizedError) => SwarmMembershipState.Error,
                _ => SwarmMembershipState.Unknown
            };

            return new SwarmClusterState
            {
                Membership = membership,
                LocalNodeState = normalizedState,
                ControlAvailable = controlAvailable,
                NodeId = (nodeId ?? string.Empty).Trim(),
                NodeAddress = (nodeAddress ?? string.Empty).Trim(),
                ErrorMessage = normalizedError,
                RemoteManagers = remoteManagers ?? Array.Empty<SwarmManagerEndpoint>()
            };
        }
    }
}
