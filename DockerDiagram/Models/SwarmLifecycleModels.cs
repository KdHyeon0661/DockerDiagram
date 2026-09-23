using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Models
{
    public enum SwarmJoinRole
    {
        Worker,
        Manager
    }

    /// <summary>
    /// Manager가 발급한 Join Token의 메모리 전용 표현입니다.
    /// 저장 모델에는 포함하지 않으며 문자열 변환 시에도 실제 Token을 노출하지 않습니다.
    /// </summary>
    public sealed class SwarmJoinTokens
    {
        public SwarmJoinTokens(string workerToken, string managerToken)
        {
            WorkerToken = workerToken ?? string.Empty;
            ManagerToken = managerToken ?? string.Empty;
        }

        public string WorkerToken { get; }
        public string ManagerToken { get; }

        public string GetToken(SwarmJoinRole role) =>
            role == SwarmJoinRole.Manager ? ManagerToken : WorkerToken;

        public override string ToString() => "Swarm join tokens (redacted)";
    }

    public sealed class SwarmInitializeOptions
    {
        public string ListenAddress { get; init; } = "0.0.0.0:2377";
        public string AdvertiseAddress { get; init; } = string.Empty;
        public string DataPathAddress { get; init; } = string.Empty;
        public uint DataPathPort { get; init; }
        public bool AutoLockManagers { get; init; }
        public string Availability { get; init; } = "active";

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ListenAddress))
                throw new ArgumentException("Swarm listen address가 비어 있습니다.", nameof(ListenAddress));
            if (string.IsNullOrWhiteSpace(AdvertiseAddress))
                throw new ArgumentException("Swarm advertise address가 비어 있습니다.", nameof(AdvertiseAddress));

            ValidateDataPathPort(DataPathPort);
            ValidateAvailability(Availability);
        }

        internal static void ValidateDataPathPort(uint port)
        {
            if (port != 0 && (port < 1024 || port > 49151))
                throw new ArgumentOutOfRangeException(nameof(DataPathPort), "Data path port는 0(기본값) 또는 1024~49151 범위여야 합니다.");
        }

        internal static void ValidateAvailability(string availability)
        {
            string normalized = (availability ?? string.Empty).Trim();
            if (!normalized.Equals("active", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Equals("pause", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Equals("drain", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Availability는 active, pause, drain 중 하나여야 합니다.", nameof(Availability));
            }
        }
    }

    public sealed class SwarmJoinOptions
    {
        public IReadOnlyList<string> RemoteManagerAddresses { get; init; } = Array.Empty<string>();
        public string JoinToken { get; init; } = string.Empty;
        public SwarmJoinRole Role { get; init; } = SwarmJoinRole.Worker;
        public string ListenAddress { get; init; } = "0.0.0.0:2377";
        public string AdvertiseAddress { get; init; } = string.Empty;
        public string DataPathAddress { get; init; } = string.Empty;
        public string Availability { get; init; } = "active";

        public IReadOnlyList<string> GetNormalizedManagerAddresses() =>
            RemoteManagerAddresses
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .Select(address => address.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        public void Validate()
        {
            if (GetNormalizedManagerAddresses().Count == 0)
                throw new ArgumentException("접속할 Swarm Manager 주소가 하나 이상 필요합니다.", nameof(RemoteManagerAddresses));
            if (string.IsNullOrWhiteSpace(JoinToken))
                throw new ArgumentException("Swarm Join Token이 비어 있습니다.", nameof(JoinToken));
            if (string.IsNullOrWhiteSpace(ListenAddress))
                throw new ArgumentException("Swarm listen address가 비어 있습니다.", nameof(ListenAddress));

            SwarmInitializeOptions.ValidateAvailability(Availability);
        }

        public override string ToString() =>
            $"{Role} join · managers {GetNormalizedManagerAddresses().Count} · token redacted";
    }
}
