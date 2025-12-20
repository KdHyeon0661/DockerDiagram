namespace DockerDiagram.Models
{
    public class DockerContainer
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Ports { get; set; } = string.Empty;
        public string StateColor { get; set; } = "#FFFFFF";

        public NodeType Type { get; set; }
    }
}