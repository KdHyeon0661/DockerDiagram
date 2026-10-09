namespace DockerDiagram.Models
{
    public sealed class SwarmNodeEditSnapshot
    {
        public string NodeId { get; init; } = string.Empty;
        public ulong Version { get; init; }
        public string Hostname { get; init; } = string.Empty;
        public string Role { get; init; } = "worker";
        public string Availability { get; init; } = "active";
        public string Status { get; init; } = string.Empty;
        public string ManagerStatus { get; init; } = string.Empty;
        public bool IsLeader { get; init; }
        public bool IsLocalNode { get; init; }
        public IReadOnlyDictionary<string, string> Labels { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class SwarmNodeUpdateOptions
    {
        public string NodeId { get; init; } = string.Empty;
        public ulong Version { get; init; }
        public string Role { get; init; } = "worker";
        public string Availability { get; init; } = "active";
        public IReadOnlyDictionary<string, string> Labels { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(NodeId))
                throw new ArgumentException("Swarm node ID가 비어 있습니다.", nameof(NodeId));
            if (!Role.Equals("worker", StringComparison.OrdinalIgnoreCase) &&
                !Role.Equals("manager", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Role은 worker 또는 manager여야 합니다.", nameof(Role));
            SwarmInitializeOptions.ValidateAvailability(Availability);
            if (Labels.Any(label => string.IsNullOrWhiteSpace(label.Key)))
                throw new ArgumentException("Node label key가 비어 있습니다.", nameof(Labels));
        }
    }

    public sealed record SwarmNodeMutationResult(
        string NodeId,
        ulong Version,
        IReadOnlyList<string> Warnings)
    {
        public static SwarmNodeMutationResult Create(
            string nodeId,
            ulong version,
            IEnumerable<string>? warnings = null) =>
            new(nodeId, version, warnings?.ToArray() ?? Array.Empty<string>());
    }

    public sealed class SwarmNodeVersionConflictException : InvalidOperationException
    {
        public SwarmNodeVersionConflictException(string nodeId, ulong expectedVersion, ulong actualVersion)
            : base(
                $"Swarm node '{nodeId}'이(가) 다른 작업에서 변경되었습니다. " +
                $"요청 version={expectedVersion}, 현재 version={actualVersion}. 새로고침 후 다시 시도해 주세요.")
        {
            NodeId = nodeId;
            ExpectedVersion = expectedVersion;
            ActualVersion = actualVersion;
        }

        public string NodeId { get; }
        public ulong ExpectedVersion { get; }
        public ulong ActualVersion { get; }
    }

    public sealed class SwarmNodeMutationException : InvalidOperationException
    {
        public SwarmNodeMutationException(
            string operation,
            int? statusCode,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            Operation = operation;
            StatusCode = statusCode;
        }

        public string Operation { get; }
        public int? StatusCode { get; }
    }
}
