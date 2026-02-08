namespace DockerDiagram.Models
{
    public enum RelationType
    {
        Dependency,     // Container <-> Container
        VolumeMount,    // Container <-> Volume
        NetworkAttach   // Container <-> Internet
    }
}
