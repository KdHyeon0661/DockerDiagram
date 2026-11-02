using System.Collections;
using DockerDiagram.Common;
using DockerDiagram.Models;
using YamlDotNet.Serialization;

namespace DockerDiagram.ApplicationServices
{
    internal static class ComposeYamlHelper
    {
        private static readonly IDeserializer RawDeserializer = new DeserializerBuilder().Build();

        private static readonly ISerializer RawSerializer = new SerializerBuilder()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .Build();

        public static Dictionary<object, object>? ParseMapping(string? yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml)) return null;

            try
            {
                return RawDeserializer.Deserialize<Dictionary<object, object>>(yaml);
            }
            catch (Exception ex)
            {
                AppLog.WarningThrottled(
                    "compose-yaml-fragment-parse",
                    "Compose YAML 조각을 해석하지 못해 보존 가능한 필드만 사용합니다.",
                    ex);
                return null;
            }
        }

        public static string SerializeObject(object? value) =>
            value == null ? string.Empty : RawSerializer.Serialize(value);

        public static Dictionary<object, object>? GetMapping(object? value)
        {
            return value switch
            {
                Dictionary<object, object> raw => raw,
                IDictionary dictionary => dictionary.Cast<DictionaryEntry>()
                    .ToDictionary(entry => entry.Key, entry => entry.Value!),
                _ => null
            };
        }

        public static object? GetValue(Dictionary<object, object>? map, string key)
        {
            if (map == null) return null;

            foreach (KeyValuePair<object, object> entry in map)
            {
                if (string.Equals(entry.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
            }

            return null;
        }

        public static Dictionary<object, object>? GetServiceMap(
            Dictionary<object, object>? root,
            string serviceName)
        {
            Dictionary<object, object>? services = GetMapping(GetValue(root, "services"));
            return GetMapping(GetValue(services, serviceName));
        }

        public static Dictionary<object, object>? GetNetworkMap(
            Dictionary<object, object>? root,
            string networkName)
        {
            Dictionary<object, object>? networks = GetMapping(GetValue(root, "networks"));
            return GetMapping(GetValue(networks, networkName));
        }

        public static Dictionary<object, object>? GetVolumeMap(
            Dictionary<object, object>? root,
            string volumeName)
        {
            Dictionary<object, object>? volumes = GetMapping(GetValue(root, "volumes"));
            return GetMapping(GetValue(volumes, volumeName));
        }

        public static string GetServiceYaml(Dictionary<object, object>? root, string serviceName)
        {
            Dictionary<object, object>? service = GetServiceMap(root, serviceName);
            return service == null ? string.Empty : SerializeObject(service);
        }

        public static string GetNetworkYaml(Dictionary<object, object>? root, string networkName)
        {
            Dictionary<object, object>? network = GetNetworkMap(root, networkName);
            return network == null ? string.Empty : SerializeObject(network);
        }

        public static string GetVolumeYaml(Dictionary<object, object>? root, string volumeName)
        {
            Dictionary<object, object>? volume = GetVolumeMap(root, volumeName);
            return volume == null ? string.Empty : SerializeObject(volume);
        }

        public static List<string> ToStringList(object? value)
        {
            if (value == null) return [];
            if (value is string scalar) return [scalar];

            if (value is IEnumerable enumerable && value is not IDictionary)
            {
                var list = new List<string>();
                foreach (object? item in enumerable)
                {
                    if (item == null) continue;
                    list.Add(IsScalar(item) ? item.ToString() ?? string.Empty : SerializeObject(item).Trim());
                }

                return list.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            }

            Dictionary<object, object>? map = GetMapping(value);
            if (map != null)
            {
                return map.Select(entry => entry.Value == null
                        ? entry.Key.ToString() ?? string.Empty
                        : $"{entry.Key}={entry.Value}")
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
            }

            return [value.ToString() ?? string.Empty];
        }

        public static List<string> ToEnvironmentList(object? value)
        {
            Dictionary<object, object>? map = GetMapping(value);
            if (map == null) return ToStringList(value);

            return map.Select(entry => entry.Value == null
                    ? entry.Key.ToString() ?? string.Empty
                    : $"{entry.Key}={entry.Value}")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        public static List<string> ToPortBindingList(object? value)
        {
            if (value == null) return [];
            if (value is string scalar) return [scalar];

            if (value is IEnumerable enumerable && value is not IDictionary)
            {
                var list = new List<string>();
                foreach (object? item in enumerable)
                {
                    string? port = PortToDisplayString(item);
                    if (!string.IsNullOrWhiteSpace(port)) list.Add(port);
                }

                return list;
            }

            string? single = PortToDisplayString(value);
            return string.IsNullOrWhiteSpace(single) ? [] : [single];
        }

        public static List<VolumeMountInfo> ToVolumeMounts(object? value)
        {
            var result = new List<VolumeMountInfo>();
            if (value == null) return result;

            if (value is string scalar)
            {
                VolumeMountInfo? parsed = ParseShortVolume(scalar);
                if (parsed != null) result.Add(parsed);
                return result;
            }

            if (value is IEnumerable enumerable && value is not IDictionary)
            {
                foreach (object? item in enumerable)
                {
                    VolumeMountInfo? mount = ToVolumeMount(item);
                    if (mount != null) result.Add(mount);
                }

                return result;
            }

            VolumeMountInfo? single = ToVolumeMount(value);
            if (single != null) result.Add(single);
            return result;
        }

        public static List<string> ToDependsOnServiceNames(object? value)
        {
            Dictionary<object, object>? map = GetMapping(value);
            return map == null
                ? ToStringList(value)
                : map.Keys.Select(key => key.ToString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
        }

        public static List<string> ToNetworkNames(object? value)
        {
            Dictionary<object, object>? map = GetMapping(value);
            return map == null
                ? ToStringList(value)
                : map.Keys.Select(key => key.ToString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
        }

        public static string? GetNetworkIpv4(object? networks, string networkName)
        {
            Dictionary<object, object>? map = GetMapping(networks);
            if (map == null) return null;

            Dictionary<object, object>? config = GetMapping(GetValue(map, networkName));
            return GetValue(config, "ipv4_address")?.ToString();
        }

        public static ContainerNetworkOptions GetNetworkOptions(object? networks, string networkName)
        {
            var options = new ContainerNetworkOptions();
            Dictionary<object, object>? map = GetMapping(networks);
            if (map == null) return options;

            Dictionary<object, object>? config = GetMapping(GetValue(map, networkName));
            if (config == null) return options;

            options.StaticIPv4 = GetValue(config, "ipv4_address")?.ToString() ?? string.Empty;
            options.StaticIPv6 = GetValue(config, "ipv6_address")?.ToString() ?? string.Empty;
            options.Aliases = ToStringList(GetValue(config, "aliases"));
            options.DriverOptions = ToStringDictionary(GetValue(config, "driver_opts"));
            return options;
        }

        public static Dictionary<string, string> ToStringDictionary(object? value)
        {
            Dictionary<object, object>? map = GetMapping(value);
            if (map == null) return [];

            return map
                .Where(entry => entry.Key != null && entry.Value != null)
                .ToDictionary(
                    entry => entry.Key.ToString() ?? string.Empty,
                    entry => entry.Value?.ToString() ?? string.Empty);
        }

        public static List<ContainerMountSpec> ToTmpfsMounts(object? value)
        {
            if (value == null) return [];

            IEnumerable<object?> entries = value is string
                ? [value]
                : value is IEnumerable sequence
                    ? sequence.Cast<object?>()
                    : [value];

            var mounts = new List<ContainerMountSpec>();
            foreach (object? entry in entries)
            {
                if (entry == null) continue;
                try
                {
                    mounts.Add(ContainerMountParser.ParseTmpfs(entry.ToString() ?? string.Empty));
                }
                catch (FormatException)
                {
                    // 잘못된 한 항목 때문에 나머지 Compose 구성을 버리지 않습니다.
                }
            }
            return mounts;
        }

        public static string? GetBuildLabel(object? build)
        {
            if (build == null) return null;
            if (build is string scalar) return $"build:{scalar}";

            Dictionary<object, object>? map = GetMapping(build);
            string? context = GetValue(map, "context")?.ToString();
            return string.IsNullOrWhiteSpace(context) ? "build" : $"build:{context}";
        }

        public static Dictionary<object, object> ToMutableServiceMap(string? rawServiceYaml) =>
            ParseMapping(rawServiceYaml) ?? [];

        public static Dictionary<object, object> ToMutableRootMap(string? rawYaml) =>
            ParseMapping(rawYaml) ?? [];

        public static void SetValue(Dictionary<object, object> map, string key, object? value)
        {
            if (value == null) return;
            if (value is string text && string.IsNullOrWhiteSpace(text)) return;
            if (value is ICollection collection && collection.Count == 0) return;
            map[key] = value;
        }

        public static bool HasKey(Dictionary<object, object> map, string key) =>
            map.Keys.Any(candidate => string.Equals(candidate?.ToString(), key, StringComparison.OrdinalIgnoreCase));

        private static VolumeMountInfo? ToVolumeMount(object? item)
        {
            if (item == null) return null;
            if (item is string scalar) return ParseShortVolume(scalar);

            Dictionary<object, object>? map = GetMapping(item);
            if (map == null) return null;

            string? source = GetValue(map, "source")?.ToString();
            string? target = GetValue(map, "target")?.ToString();
            string type = GetValue(map, "type")?.ToString() ?? string.Empty;
            MountKind? kind = type.ToLowerInvariant() switch
            {
                "bind" => MountKind.Bind,
                "tmpfs" => MountKind.Tmpfs,
                "volume" => MountKind.Volume,
                "" => !string.IsNullOrWhiteSpace(source) && ContainerMountParser.LooksLikeBindSource(source)
                    ? MountKind.Bind
                    : MountKind.Volume,
                _ => null
            };
            if (!kind.HasValue) return null;
            bool readOnly = bool.TryParse(GetValue(map, "read_only")?.ToString(), out bool parsedReadOnly) && parsedReadOnly;
            if (string.IsNullOrWhiteSpace(target) || (kind.Value != MountKind.Tmpfs && string.IsNullOrWhiteSpace(source)))
                return null;

            Dictionary<object, object>? bindOptions = GetMapping(GetValue(map, "bind"));
            Dictionary<object, object>? volumeOptions = GetMapping(GetValue(map, "volume"));
            Dictionary<object, object>? tmpfsOptions = GetMapping(GetValue(map, "tmpfs"));
            string propagation = GetValue(bindOptions, "propagation")?.ToString() ?? "rprivate";
            bool noCopy = bool.TryParse(GetValue(volumeOptions, "nocopy")?.ToString(), out bool parsedNoCopy) && parsedNoCopy;
            long tmpfsSize = 0;
            uint tmpfsMode = Convert.ToUInt32("1777", 8);
            try
            {
                tmpfsSize = ContainerMountParser.ParseByteSize(GetValue(tmpfsOptions, "size")?.ToString());
                tmpfsMode = ContainerMountParser.ParseOctalMode(GetValue(tmpfsOptions, "mode")?.ToString());
            }
            catch (FormatException)
            {
                // Compose 원문은 보존되므로 표시 모델에는 안전한 기본값을 사용합니다.
            }

            return new VolumeMountInfo(
                source ?? string.Empty,
                target,
                kind.Value,
                readOnly,
                propagation,
                noCopy,
                tmpfsSize,
                tmpfsMode);
        }

        private static VolumeMountInfo? ParseShortVolume(string volume)
        {
            try
            {
                ContainerMountSpec parsed = ContainerMountParser.ParseLegacy(volume);
                return new VolumeMountInfo(
                    parsed.Source,
                    parsed.Target,
                    parsed.Kind,
                    parsed.ReadOnly,
                    parsed.BindPropagation,
                    parsed.VolumeNoCopy,
                    parsed.TmpfsSizeBytes,
                    parsed.TmpfsMode);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static string? PortToDisplayString(object? item)
        {
            if (item == null) return null;
            if (IsScalar(item)) return item.ToString();

            Dictionary<object, object>? map = GetMapping(item);
            if (map == null) return SerializeObject(item).Trim();

            string? target = GetValue(map, "target")?.ToString();
            string? published = GetValue(map, "published")?.ToString();
            string? protocol = GetValue(map, "protocol")?.ToString();
            string? hostIp = GetValue(map, "host_ip")?.ToString();
            if (string.IsNullOrWhiteSpace(target)) return null;

            string display = string.IsNullOrWhiteSpace(published) ? target : $"{published}:{target}";
            if (!string.IsNullOrWhiteSpace(hostIp) && !string.IsNullOrWhiteSpace(published))
                display = $"{hostIp}:{display}";
            if (!string.IsNullOrWhiteSpace(protocol))
                display = $"{display}/{protocol}";
            return display;
        }

        private static bool IsScalar(object value) =>
            value is string || value is bool || value is char || value.GetType().IsPrimitive || value is decimal;
    }

    internal record VolumeMountInfo(
        string Source,
        string Target,
        MountKind Kind,
        bool ReadOnly,
        string BindPropagation,
        bool VolumeNoCopy,
        long TmpfsSizeBytes,
        uint TmpfsMode);
}
