using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Models
{
    public enum DockerPruneTarget
    {
        System,
        Container,
        Image,
        Volume,
        Network
    }

    public class DockerPruneOptions
    {
        public DockerPruneTarget Target { get; set; }
        public bool AllImages { get; set; }
        public bool IncludeVolumes { get; set; }
    }

    public class DockerPruneResult
    {
        public List<string> ContainersDeleted { get; set; } = new();
        public List<DockerPruneImageDelete> ImagesDeleted { get; set; } = new();
        public List<string> VolumesDeleted { get; set; } = new();
        public List<string> NetworksDeleted { get; set; } = new();
        public List<string> BuildCacheDeleted { get; set; } = new();
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
