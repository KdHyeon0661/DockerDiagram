namespace DockerDiagram.Models
{
    public enum SwarmTaskFailureCategory
    {
        Healthy,
        Starting,
        Completed,
        SchedulingPending,
        SchedulingFailure,
        ImagePullFailure,
        PortConflict,
        ProcessExit,
        Rejected,
        Orphaned,
        Removed,
        Failed,
        Historical,
        Unknown
    }

    public sealed class SwarmTaskDiagnostic
    {
        public string TaskId { get; init; } = string.Empty;
        public string TaskName { get; init; } = string.Empty;
        public ulong Slot { get; init; }
        public string NodeId { get; init; } = string.Empty;
        public string NodeName { get; init; } = "Unassigned";
        public string DesiredState { get; init; } = string.Empty;
        public string CurrentState { get; init; } = string.Empty;
        public string Image { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string ContainerId { get; init; } = string.Empty;
        public long ExitCode { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
        public DateTimeOffset? StatusTimestamp { get; init; }
        public int AttemptNumber { get; set; } = 1;
        public bool IsHistorical { get; set; }
        public SwarmTaskFailureCategory Category { get; set; } = SwarmTaskFailureCategory.Unknown;
        public string StatusColor { get; set; } = "#808080";
        public string ShortTaskId => TaskId.Length > 12 ? TaskId[..12] : TaskId;
        public string ShortContainerId => ContainerId.Length > 12 ? ContainerId[..12] : ContainerId;
        public string SlotLabel => Slot == 0 ? "global" : Slot.ToString();
        public string AttemptLabel => $"#{AttemptNumber}" + (IsHistorical ? " old" : string.Empty);
        public string CategoryLabel => Category switch
        {
            SwarmTaskFailureCategory.SchedulingPending => "Scheduling pending",
            SwarmTaskFailureCategory.SchedulingFailure => "Scheduling failure",
            SwarmTaskFailureCategory.ImagePullFailure => "Image pull",
            SwarmTaskFailureCategory.PortConflict => "Port conflict",
            SwarmTaskFailureCategory.ProcessExit => "Process exit",
            _ => Category.ToString()
        };
        public string DiagnosticText
        {
            get
            {
                var lines = new List<string>
                {
                    $"Task: {TaskId}",
                    $"Slot / attempt: {SlotLabel} / {AttemptNumber}",
                    $"Node: {NodeName} ({(string.IsNullOrWhiteSpace(NodeId) ? "unassigned" : NodeId)})",
                    $"Desired / current: {DesiredState} / {CurrentState}",
                    $"Category: {CategoryLabel}",
                    $"Image: {Image}",
                    $"Container: {(string.IsNullOrWhiteSpace(ContainerId) ? "not created" : ContainerId)}",
                    $"Exit code: {ExitCode}"
                };
                if (!string.IsNullOrWhiteSpace(Message)) lines.Add($"Message: {Message}");
                if (!string.IsNullOrWhiteSpace(Error)) lines.Add($"Error: {Error}");
                if (StatusTimestamp.HasValue) lines.Add($"Status time: {StatusTimestamp:O}");
                return string.Join(Environment.NewLine, lines);
            }
        }
    }

    public sealed class SwarmTaskDiagnosticReport
    {
        public string ServiceId { get; init; } = string.Empty;
        public string ServiceName { get; init; } = string.Empty;
        public string LocalNodeId { get; init; } = string.Empty;
        public IReadOnlyList<SwarmTaskDiagnostic> Tasks { get; init; } = Array.Empty<SwarmTaskDiagnostic>();
        public int CurrentCount => Tasks.Count(task => !task.IsHistorical);
        public int RunningCount => Tasks.Count(task => !task.IsHistorical && task.Category == SwarmTaskFailureCategory.Healthy);
        public int FailureCount => Tasks.Count(task => !task.IsHistorical && task.Category is
            SwarmTaskFailureCategory.SchedulingFailure or
            SwarmTaskFailureCategory.ImagePullFailure or
            SwarmTaskFailureCategory.PortConflict or
            SwarmTaskFailureCategory.ProcessExit or
            SwarmTaskFailureCategory.Rejected or
            SwarmTaskFailureCategory.Orphaned or
            SwarmTaskFailureCategory.Failed);
        public int PendingCount => Tasks.Count(task => !task.IsHistorical && task.Category is
            SwarmTaskFailureCategory.Starting or SwarmTaskFailureCategory.SchedulingPending);
        public int HistoricalCount => Tasks.Count(task => task.IsHistorical);
        public string Summary =>
            $"current {CurrentCount} · running {RunningCount} · pending {PendingCount} · failed {FailureCount} · history {HistoricalCount}";
    }
}
