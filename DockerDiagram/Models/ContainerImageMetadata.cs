using System.Collections.Generic;

namespace DockerDiagram.Models
{
    public sealed class ContainerImageMetadata
    {
        public string ImageReference { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public List<string> Environment { get; set; } = [];
        public List<string> ExposedPorts { get; set; } = [];
        public List<string> Volumes { get; set; } = [];
        public List<string> Entrypoint { get; set; } = [];
        public List<string> Command { get; set; } = [];
    }
}
