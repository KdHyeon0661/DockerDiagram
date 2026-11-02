namespace DockerDiagram.Models
{
    public enum RuntimeLifecycleState
    {
        Unknown,
        Creating,
        Running,
        Paused,
        Stopped,
        Failed
    }

    public enum RuntimeConnectionState
    {
        Unknown,
        Connected,
        Disconnected
    }
}
