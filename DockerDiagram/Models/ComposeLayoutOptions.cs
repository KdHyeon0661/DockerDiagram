namespace DockerDiagram.Models
{
    public enum ComposeLayoutDirection
    {
        LeftToRight,
        TopToBottom
    }

    public sealed class ComposeLayoutOptions
    {
        public ComposeLayoutDirection Direction { get; set; } = ComposeLayoutDirection.LeftToRight;
        public double HorizontalGap { get; set; } = 90;
        public double VerticalGap { get; set; } = 35;
        public bool UseAdaptiveSpacing { get; set; } = true;

        public ComposeLayoutOptions Clone() => new()
        {
            Direction = Direction,
            HorizontalGap = HorizontalGap,
            VerticalGap = VerticalGap,
            UseAdaptiveSpacing = UseAdaptiveSpacing
        };
    }
}
