namespace DockerDiagram.Models
{
    public class DockerContextInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string DockerEndpoint { get; set; } = "";
        public string Error { get; set; } = "";
        public bool IsCurrent { get; set; }

        public bool IsDefault => string.Equals(Name, "default", System.StringComparison.OrdinalIgnoreCase);
        public string CurrentText => IsCurrent ? "*" : "";
        public string StatusText => string.IsNullOrWhiteSpace(Error) ? "Ready" : Error;
    }
}
