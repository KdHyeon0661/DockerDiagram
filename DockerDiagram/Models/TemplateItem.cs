namespace DockerDiagram.Models
{
    public class TemplateItem
    {
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public NodeType Type { get; set; } = NodeType.Container;
        public bool IsDefault { get; set; } = false; // 기본 제공 여부

        public string DisplayName => IsDefault ? $"[Basic] {Name}" : Name;
    }
}