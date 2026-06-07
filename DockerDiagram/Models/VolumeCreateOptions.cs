using System.Collections.Generic;

namespace DockerDiagram.Models
{
    public class VolumeCreateOptions
    {
        public string Name { get; set; } = string.Empty;
        public string DockerVolumeName { get; set; } = string.Empty;
        public string Driver { get; set; } = "local";
        public bool External { get; set; }
        public Dictionary<string, string> Labels { get; set; } = new();
        public Dictionary<string, string> DriverOptions { get; set; } = new();

        public string EffectiveDockerVolumeName =>
            string.IsNullOrWhiteSpace(DockerVolumeName) ? Name : DockerVolumeName;

        public static VolumeCreateOptions Basic(string name, string driver) => new()
        {
            Name = name,
            Driver = string.IsNullOrWhiteSpace(driver) ? "local" : driver
        };
    }
}
