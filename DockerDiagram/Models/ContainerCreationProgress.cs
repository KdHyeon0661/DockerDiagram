namespace DockerDiagram.Models
{
    public enum ContainerCreationPhase
    {
        Preparing,
        Creating,
        Starting,
        Started
    }

    public sealed record ContainerCreationProgress(
        ContainerCreationPhase Phase,
        string Message,
        string Detail);
}
