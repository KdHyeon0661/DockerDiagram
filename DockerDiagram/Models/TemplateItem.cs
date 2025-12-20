namespace DockerDiagram.Models
{
    public class TemplateItem
    {
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public NodeType Type { get; set; } = NodeType.Container;
        public bool IsDefault { get; set; } = false; // 기본 제공 여부

        // 화면 표시용 (기본이면 아이콘 색상 등을 다르게 줄 수도 있음)
        public string DisplayName => IsDefault ? $"[Basic] {Name}" : Name;
    }
}