namespace DockerDiagram.Models
{
    public class VolumeUsageInfo
    {
        public string ContainerId { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public bool ReadWrite { get; set; } = true;
        public string Mode { get; set; } = string.Empty;

        public string DisplayText
        {
            get
            {
                var access = ReadWrite ? "rw" : "ro";
                var mode = string.IsNullOrWhiteSpace(Mode) ? access : $"{access}, {Mode}";
                return $"{ContainerName} -> {Destination} ({mode})";
            }
        }
    }
}
