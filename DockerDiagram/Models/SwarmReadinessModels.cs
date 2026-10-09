namespace DockerDiagram.Models
{
    public enum SwarmReadinessSeverity
    {
        Pass,
        Warning,
        Blocked
    }

    public sealed record SwarmReadinessCheck(
        string Category,
        string Name,
        SwarmReadinessSeverity Severity,
        string Detail)
    {
        public string StatusText => Severity switch
        {
            SwarmReadinessSeverity.Pass => "PASS",
            SwarmReadinessSeverity.Warning => "WARN",
            _ => "BLOCKED"
        };

        public string StatusColor => Severity switch
        {
            SwarmReadinessSeverity.Pass => "#2E7D32",
            SwarmReadinessSeverity.Warning => "#B26A00",
            _ => "#B3261E"
        };
    }

    public sealed record SwarmReadinessReport(
        DateTimeOffset CheckedAt,
        IReadOnlyList<SwarmReadinessCheck> Checks)
    {
        public SwarmReadinessSeverity OverallSeverity =>
            Checks.Any(check => check.Severity == SwarmReadinessSeverity.Blocked)
                ? SwarmReadinessSeverity.Blocked
                : Checks.Any(check => check.Severity == SwarmReadinessSeverity.Warning)
                    ? SwarmReadinessSeverity.Warning
                    : SwarmReadinessSeverity.Pass;

        public string Summary =>
            $"{OverallSeverity} · " +
            $"{Checks.Count(check => check.Severity == SwarmReadinessSeverity.Pass)} pass, " +
            $"{Checks.Count(check => check.Severity == SwarmReadinessSeverity.Warning)} warning, " +
            $"{Checks.Count(check => check.Severity == SwarmReadinessSeverity.Blocked)} blocked";
    }

    public sealed record DockerCliProbeResult(bool IsAvailable, string Version, string Error);
}
