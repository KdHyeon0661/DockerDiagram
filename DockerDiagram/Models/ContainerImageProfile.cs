using System.Collections.Generic;

namespace DockerDiagram.Models
{
    public sealed class ContainerImageProfile
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public IReadOnlyList<string> Notes { get; init; } = [];
        public IReadOnlyList<string> ImageAliases { get; init; } = [];
        public IReadOnlyList<ContainerImageProfileField> Fields { get; init; } = [];
        public IReadOnlyList<ContainerImageProfileVolume> Volumes { get; init; } = [];
        public string CommandTemplate { get; init; } = string.Empty;
    }

    public sealed class ContainerImageProfileField
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string Type { get; init; } = "text";
        public string DefaultValue { get; init; } = string.Empty;
        public string HelpText { get; init; } = string.Empty;
        public bool Required { get; init; }
        public string EnvironmentVariable { get; init; } = string.Empty;
        public string TrueValue { get; init; } = "true";
        public string FalseValue { get; init; } = "false";
        public string ContainerPort { get; init; } = string.Empty;
    }

    public sealed class ContainerImageProfileVolume
    {
        public string NameSuffix { get; init; } = "data";
        public string ContainerPath { get; init; } = string.Empty;
    }
}
