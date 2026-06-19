using System.Collections.Generic;

namespace DockerDiagram.Models
{
    public sealed class StackTemplateDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string DefaultProjectName { get; set; } = "stack";
        public string AccentColor { get; set; } = "#287BAE";
        public List<StackTemplateVariableDefinition> Variables { get; set; } = [];
        public List<StackTemplateNetworkDefinition> Networks { get; set; } = [];
        public List<StackTemplateVolumeDefinition> Volumes { get; set; } = [];
        public List<StackTemplateContainerDefinition> Containers { get; set; } = [];
        public List<StackTemplateDependencyDefinition> Dependencies { get; set; } = [];

        public string ResourceSummary =>
            $"{Containers.Count} containers | {Networks.Count} networks | {Volumes.Count} volumes";
    }

    public sealed class StackTemplateVariableDefinition
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Type { get; set; } = "text";
        public string DefaultValue { get; set; } = string.Empty;
        public bool Required { get; set; } = true;
    }

    public sealed class StackTemplateNetworkDefinition
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Driver { get; set; } = "bridge";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 460;
        public double Height { get; set; } = 260;
    }

    public sealed class StackTemplateVolumeDefinition
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Driver { get; set; } = "local";
        public string EnabledWhen { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class StackTemplateContainerDefinition
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public List<string> Ports { get; set; } = [];
        public Dictionary<string, string> Environment { get; set; } = [];
        public List<StackTemplateVolumeMountDefinition> VolumeMounts { get; set; } = [];
        public List<string> Networks { get; set; } = [];
        public string Command { get; set; } = string.Empty;
        public string RestartPolicy { get; set; } = "unless-stopped";
        public string EnabledWhen { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class StackTemplateVolumeMountDefinition
    {
        public string Volume { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public bool ReadOnly { get; set; }
    }

    public sealed class StackTemplateDependencyDefinition
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
    }

    public sealed class StackTemplateDeploymentOptions
    {
        public string ProjectName { get; set; } = "stack";
        public bool DeployToDocker { get; set; } = true;
        public Dictionary<string, string> Variables { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
