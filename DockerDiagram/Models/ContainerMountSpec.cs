using System.Text.Json.Serialization;
using DockerDiagram.Common;

namespace DockerDiagram.Models
{
    public enum MountKind
    {
        Volume,
        Bind,
        Tmpfs
    }

    /// <summary>
    /// Docker 컨테이너에 연결되는 저장소를 문자열이 아닌 명시적인 유형과 옵션으로 표현합니다.
    /// </summary>
    public sealed class ContainerMountSpec
    {
        public MountKind Kind { get; set; } = MountKind.Volume;
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public bool ReadOnly { get; set; }
        public string BindPropagation { get; set; } = "rprivate";
        public bool VolumeNoCopy { get; set; }
        public long TmpfsSizeBytes { get; set; }
        public uint TmpfsMode { get; set; } = Convert.ToUInt32("1777", 8);

        [JsonIgnore]
        public string KindLabel => Kind switch
        {
            MountKind.Volume => "Named Volume",
            MountKind.Bind => "Bind Mount",
            MountKind.Tmpfs => "Tmpfs",
            _ => Kind.ToString()
        };

        [JsonIgnore]
        public string Icon => Kind switch
        {
            MountKind.Volume => "🛢",
            MountKind.Bind => "📁",
            MountKind.Tmpfs => "⚡",
            _ => "•"
        };

        [JsonIgnore]
        public string SourceDisplay => Kind switch
        {
            MountKind.Tmpfs => "Memory",
            MountKind.Volume when string.IsNullOrWhiteSpace(Source) => "(anonymous volume)",
            _ => Source
        };

        [JsonIgnore]
        public string OptionSummary
        {
            get
            {
                var options = new List<string> { ReadOnly ? "ro" : "rw" };
                if (Kind == MountKind.Bind && !string.IsNullOrWhiteSpace(BindPropagation))
                    options.Add(BindPropagation);
                if (Kind == MountKind.Volume && VolumeNoCopy)
                    options.Add("nocopy");
                if (Kind == MountKind.Tmpfs)
                {
                    if (TmpfsSizeBytes > 0)
                        options.Add(FormatBytes(TmpfsSizeBytes));
                    options.Add($"mode={Convert.ToString(TmpfsMode, 8)}");
                }
                return string.Join(" · ", options);
            }
        }

        public ContainerMountSpec Clone() => new()
        {
            Kind = Kind,
            Source = Source,
            Target = Target,
            ReadOnly = ReadOnly,
            BindPropagation = BindPropagation,
            VolumeNoCopy = VolumeNoCopy,
            TmpfsSizeBytes = TmpfsSizeBytes,
            TmpfsMode = TmpfsMode
        };

        public string ToLegacyString()
        {
            string mode = ReadOnly ? ":ro" : string.Empty;
            return Kind switch
            {
                MountKind.Tmpfs => Target,
                MountKind.Volume when string.IsNullOrWhiteSpace(Source) => $"{Target}{mode}",
                _ => $"{Source}:{Target}{mode}"
            };
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "unlimited";
            return ByteSizeFormatter.FormatMetric(
                bytes,
                maximumFractionDigits: 1,
                wholeNumberThreshold: 10);
        }
    }
}
