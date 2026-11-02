namespace DockerDiagram.Models
{
    public enum NodeOperationPhase
    {
        Preparing,
        Pulling,
        Building,
        Creating,
        Starting,
        Synchronizing,
        Updating,
        Completed,
        Failed
    }

    public sealed record NodeOperationProgress(
        NodeOperationPhase Phase,
        string Message,
        string Detail,
        double? Percent = null,
        string Error = "")
    {
        public static NodeOperationProgress Idle { get; } =
            new(NodeOperationPhase.Preparing, string.Empty, string.Empty);

        public double Value => Math.Clamp(Percent ?? 0, 0, 100);
        public bool IsIndeterminate => !Percent.HasValue;
    }
}
