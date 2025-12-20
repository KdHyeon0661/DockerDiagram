namespace DockerDiagram.Models
{
    public class DockerImage
    {
        public string Id { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public long Size { get; set; }
    }
}