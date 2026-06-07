using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Models
{
    public class SystemDiskUsage
    {
        public long LayersSize { get; set; }
        public List<SystemDiskUsageImage> Images { get; set; } = [];
        public List<SystemDiskUsageContainer> Containers { get; set; } = [];
        public List<SystemDiskUsageVolume> Volumes { get; set; } = [];
        public List<SystemDiskUsageBuildCache> BuildCache { get; set; } = [];

        public long ImagesSize => Images.Sum(i => i.Size);
        public long ContainersSize => Containers.Sum(c => c.SizeRw);
        public long VolumesSize => Volumes.Sum(v => v.UsageData?.Size ?? 0);
        public long BuildCacheSize => BuildCache.Sum(c => c.Size);
        public string FormattedLayersSize => DiskUsageFormat.FormatBytes(LayersSize);
        public string FormattedImagesSize => DiskUsageFormat.FormatBytes(ImagesSize);
        public string FormattedContainersSize => DiskUsageFormat.FormatBytes(ContainersSize);
        public string FormattedVolumesSize => DiskUsageFormat.FormatBytes(VolumesSize);
        public string FormattedBuildCacheSize => DiskUsageFormat.FormatBytes(BuildCacheSize);
    }

    public class SystemDiskUsageImage
    {
        public string? Id { get; set; }
        public string? ParentId { get; set; }
        public List<string>? RepoTags { get; set; }
        public List<string>? RepoDigests { get; set; }
        public long Created { get; set; }
        public long Size { get; set; }
        public long SharedSize { get; set; }
        public long VirtualSize { get; set; }
        public int Containers { get; set; }

        public string Repository => RepoTags == null || RepoTags.Count == 0 ? "<none>" : string.Join(", ", RepoTags);
        public string ShortId => DiskUsageFormat.ShortenId(Id);
        public string FormattedSize => DiskUsageFormat.FormatBytes(Size);
        public string FormattedSharedSize => DiskUsageFormat.FormatBytes(SharedSize);
        public string CreatedText => DiskUsageFormat.FormatUnixTime(Created);
    }

    public class SystemDiskUsageContainer
    {
        public string? Id { get; set; }
        public List<string>? Names { get; set; }
        public string? Image { get; set; }
        public string? State { get; set; }
        public long SizeRw { get; set; }
        public long SizeRootFs { get; set; }

        public string Name => Names == null || Names.Count == 0 ? "<unnamed>" : string.Join(", ", Names.Select(n => n.TrimStart('/')));
        public string ShortId => DiskUsageFormat.ShortenId(Id);
        public string FormattedSizeRw => DiskUsageFormat.FormatBytes(SizeRw);
        public string FormattedSizeRootFs => DiskUsageFormat.FormatBytes(SizeRootFs);
    }

    public class SystemDiskUsageVolume
    {
        public string? Name { get; set; }
        public string? Driver { get; set; }
        public string? Mountpoint { get; set; }
        public SystemDiskUsageUsageData? UsageData { get; set; }

        public long Size => UsageData?.Size ?? 0;
        public long RefCount => UsageData?.RefCount ?? 0;
        public string FormattedSize => DiskUsageFormat.FormatBytes(Size);
    }

    public class SystemDiskUsageUsageData
    {
        public long Size { get; set; }
        public long RefCount { get; set; }
    }

    public class SystemDiskUsageBuildCache
    {
        public string? ID { get; set; }
        public string? Parent { get; set; }
        public string? Type { get; set; }
        public string? Description { get; set; }
        public bool InUse { get; set; }
        public bool Shared { get; set; }
        public long Size { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public int UsageCount { get; set; }

        public string ShortId => DiskUsageFormat.ShortenId(ID);
        public string FormattedSize => DiskUsageFormat.FormatBytes(Size);
        public string CreatedText => DiskUsageFormat.FormatDateTime(CreatedAt);
        public string LastUsedText => DiskUsageFormat.FormatDateTime(LastUsedAt);
    }

    public static class DiskUsageFormat
    {
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "-";

            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            int unitIndex = 0;

            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.##} {units[unitIndex]}";
        }

        public static string ShortenId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";

            var cleanId = id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? id.Substring("sha256:".Length)
                : id;

            return cleanId.Length <= 12 ? cleanId : cleanId.Substring(0, 12);
        }

        public static string FormatUnixTime(long value)
        {
            if (value <= 0) return "";

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "";
            }
        }

        public static string FormatDateTime(DateTime? value)
        {
            return value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
        }
    }
}
