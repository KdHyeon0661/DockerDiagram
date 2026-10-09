using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public sealed class SwarmDiagramTopologyInput
    {
        public IReadOnlyList<SwarmPublishedPortOptions> PublishedPorts { get; init; } = Array.Empty<SwarmPublishedPortOptions>();
        public IReadOnlyList<SwarmMountOptions> Mounts { get; init; } = Array.Empty<SwarmMountOptions>();
        public IReadOnlyList<SwarmNetworkAttachmentOptions> Networks { get; init; } = Array.Empty<SwarmNetworkAttachmentOptions>();
        public bool HasDiagramPublishedPorts { get; init; }
        public bool HasDiagramMounts { get; init; }
        public bool HasDiagramNetworks { get; init; }

        public bool HasChanges => HasDiagramPublishedPorts || HasDiagramMounts || HasDiagramNetworks;
    }

    /// <summary>다이어그램 관계를 기존 ServiceSpec에 안전하게 합성합니다.</summary>
    public static class SwarmDiagramTopologyCompiler
    {
        public static SwarmServiceSpecOptions Apply(
            SwarmServiceSpecOptions baseline,
            SwarmDiagramTopologyInput topology)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(topology);

            var result = new SwarmServiceSpecOptions
            {
                Name = baseline.Name,
                Image = baseline.Image,
                Mode = baseline.Mode,
                Replicas = baseline.Replicas,
                Command = baseline.Command,
                Arguments = baseline.Arguments,
                EnvironmentVariables = baseline.EnvironmentVariables,
                Labels = baseline.Labels,
                PublishedPorts = topology.HasDiagramPublishedPorts ? topology.PublishedPorts : baseline.PublishedPorts,
                Mounts = topology.HasDiagramMounts ? topology.Mounts : baseline.Mounts,
                Networks = topology.HasDiagramNetworks ? topology.Networks : baseline.Networks,
                RestartPolicy = baseline.RestartPolicy,
                UpdatePolicy = baseline.UpdatePolicy,
                RollbackPolicy = baseline.RollbackPolicy
            };

            result.Validate();
            return result;
        }
    }
}
