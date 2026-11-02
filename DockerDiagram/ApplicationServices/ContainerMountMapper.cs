using Docker.DotNet.Models;
using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static class ContainerMountMapper
    {
        public static List<ContainerMountSpec> FromInspect(
            IEnumerable<MountPoint>? actualMounts,
            IEnumerable<Mount>? configuredMounts = null)
        {
            List<Mount> configured = configuredMounts?.ToList() ?? [];
            var result = new List<ContainerMountSpec>();
            foreach (MountPoint actual in actualMounts ?? [])
            {
                MountKind? kind = ParseKind(actual.Type);
                if (!kind.HasValue) continue;

                string source = kind == MountKind.Volume && !string.IsNullOrWhiteSpace(actual.Name)
                    ? actual.Name
                    : actual.Source ?? string.Empty;
                string target = actual.Destination ?? string.Empty;
                Mount? options = configured.FirstOrDefault(candidate =>
                    ParseKind(candidate.Type) == kind &&
                    string.Equals(candidate.Target, target, StringComparison.Ordinal));

                result.Add(new ContainerMountSpec
                {
                    Kind = kind.Value,
                    Source = source,
                    Target = target,
                    ReadOnly = !actual.RW,
                    BindPropagation = options?.BindOptions?.Propagation ?? "rprivate",
                    VolumeNoCopy = options?.VolumeOptions?.NoCopy == true,
                    TmpfsSizeBytes = options?.TmpfsOptions?.SizeBytes ?? 0,
                    TmpfsMode = options?.TmpfsOptions?.Mode ?? Convert.ToUInt32("1777", 8)
                });
            }
            return result;
        }

        public static MountKind? ParseKind(string? type) => type?.ToLowerInvariant() switch
        {
            "volume" => MountKind.Volume,
            "bind" => MountKind.Bind,
            "tmpfs" => MountKind.Tmpfs,
            _ => null
        };
    }
}
