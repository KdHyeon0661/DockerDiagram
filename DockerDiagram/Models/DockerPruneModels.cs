using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace DockerDiagram.Models
{
    public enum DockerPruneTarget
    {
        System,
        Container,
        Image,
        Volume,
        Network,
        AllResources
    }

    [Flags]
    public enum DockerPruneResource
    {
        None = 0,
        Containers = 1 << 0,
        Images = 1 << 1,
        Volumes = 1 << 2,
        Networks = 1 << 3,
        BuildCache = 1 << 4,
        All = Containers | Images | Volumes | Networks | BuildCache
    }

    public static class DockerPruneSafety
    {
        private static readonly string[] OrchestratorManagedLabelKeys =
        [
            "com.docker.swarm.service.id",
            "com.docker.swarm.task.id",
            "io.kubernetes.container.name",
            "io.kubernetes.pod.name",
            "io.kubernetes.pod.namespace"
        ];

        public static bool IsOrchestratorManaged(IReadOnlyDictionary<string, string>? labels) =>
            labels != null && OrchestratorManagedLabelKeys.Any(labels.ContainsKey);
    }

    public class DockerPruneOptions
    {
        public DockerPruneTarget Target { get; set; }
        public DockerPruneResource Resources { get; set; }
        public bool IncludeRunningContainers { get; set; }
        public bool AllImages { get; set; }
        public bool IncludeVolumes { get; set; }
        public bool IncludeAnonymousVolumes { get; set; } = true;
        public bool IncludeNamedVolumes { get; set; } = true;

        [JsonIgnore]
        public DockerPruneResource EffectiveResources => Resources != DockerPruneResource.None
            ? Resources
            : Target switch
            {
                DockerPruneTarget.Container => DockerPruneResource.Containers,
                DockerPruneTarget.Image => DockerPruneResource.Images,
                DockerPruneTarget.Volume => DockerPruneResource.Volumes,
                DockerPruneTarget.Network => DockerPruneResource.Networks,
                DockerPruneTarget.AllResources => DockerPruneResource.All,
                _ => DockerPruneResource.Containers |
                     DockerPruneResource.Images |
                     DockerPruneResource.Networks |
                     DockerPruneResource.BuildCache |
                     (IncludeVolumes ? DockerPruneResource.Volumes : DockerPruneResource.None)
            };
    }

    public class DockerPruneResult
    {
        private List<string> _containersDeleted = [];
        private List<DockerPruneImageDelete> _imagesDeleted = [];
        private List<string> _volumesDeleted = [];
        private List<string> _networksDeleted = [];
        private List<string> _buildCacheDeleted = [];
        private List<string> _failures = [];

        public List<string> ContainersDeleted
        {
            get => _containersDeleted;
            set => _containersDeleted = value ?? [];
        }

        public List<DockerPruneImageDelete> ImagesDeleted
        {
            get => _imagesDeleted;
            set => _imagesDeleted = value ?? [];
        }

        public List<string> VolumesDeleted
        {
            get => _volumesDeleted;
            set => _volumesDeleted = value ?? [];
        }

        public List<string> NetworksDeleted
        {
            get => _networksDeleted;
            set => _networksDeleted = value ?? [];
        }

        // Docker Engine의 build/prune 응답 필드명은 CachesDeleted다.
        [JsonProperty("CachesDeleted")]
        public List<string> BuildCacheDeleted
        {
            get => _buildCacheDeleted;
            set => _buildCacheDeleted = value ?? [];
        }

        [JsonIgnore]
        public List<string> Failures
        {
            get => _failures;
            set => _failures = value ?? [];
        }

        public long SpaceReclaimed { get; set; }

        public string Summary
        {
            get
            {
                var lines = new List<string>
                {
                    $"Containers: {ContainersDeleted.Count}",
                    $"Images: {ImagesDeleted.Count}",
                    $"Volumes: {VolumesDeleted.Count}",
                    $"Networks: {NetworksDeleted.Count}",
                    $"Build Cache: {BuildCacheDeleted.Count}",
                    $"Space reclaimed: {DiskUsageFormat.FormatBytes(SpaceReclaimed)}"
                };

                var deletedItems = ContainersDeleted
                    .Concat(VolumesDeleted)
                    .Concat(NetworksDeleted)
                    .Concat(BuildCacheDeleted)
                    .Concat(ImagesDeleted.Select(i => string.IsNullOrWhiteSpace(i.Deleted) ? i.Untagged : i.Deleted)
                        .Where(s => !string.IsNullOrWhiteSpace(s)))
                    .ToList();

                if (deletedItems.Count > 0)
                {
                    lines.Add("");
                    lines.AddRange(deletedItems.Select(item => $"- {item}"));
                }

                return string.Join("\n", lines);
            }
        }
    }

    public class DockerPruneImageDelete
    {
        public string Deleted { get; set; } = string.Empty;
        public string Untagged { get; set; } = string.Empty;
    }
}
