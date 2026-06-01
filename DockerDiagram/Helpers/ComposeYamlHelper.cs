using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DockerDiagram.Helpers
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
            catch
            {
                return null;
            }
        }

        public static string SerializeObject(object? value)
        {
            if (value == null) return string.Empty;
            return RawSerializer.Serialize(value);
        }

        public static Dictionary<object, object>? GetMapping(object? value)
        {
            return value switch
            {
                Dictionary<object, object> raw => raw,
                IDictionary dictionary => dictionary.Cast<DictionaryEntry>().ToDictionary(e => e.Key, e => e.Value!),
                _ => null
            };
        }

        public static object? GetValue(Dictionary<object, object>? map, string key)
        {
            if (map == null) return null;
            foreach (var entry in map)
            {
                if (string.Equals(entry.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
            }
            return null;
        }

        public static Dictionary<object, object>? GetServiceMap(Dictionary<object, object>? root, string serviceName)
        {
            var services = GetMapping(GetValue(root, "services"));
            return GetMapping(GetValue(services, serviceName));
        }

        public static string GetServiceYaml(Dictionary<object, object>? root, string serviceName)
        {
            var service = GetServiceMap(root, serviceName);
            return service == null ? string.Empty : SerializeObject(service);
        }

        public static List<string> ToStringList(object? value)
        {
            if (value == null) return new List<string>();
            if (value is string scalar) return new List<string> { scalar };

            if (value is IEnumerable enumerable && value is not IDictionary)
            {
                var list = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (IsScalar(item)) list.Add(item.ToString() ?? "");
                    else list.Add(SerializeObject(item).Trim());
                }
                return list.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }

            var map = GetMapping(value);
            if (map != null)
            {
                return map.Select(e => e.Value == null ? e.Key.ToString() ?? "" : $"{e.Key}={e.Value}")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }

            return new List<string> { value.ToString() ?? "" };
        }

        public static List<string> ToEnvironmentList(object? value)
        {
            var map = GetMapping(value);
            if (map != null)
            {
                return map.Select(e => e.Value == null ? e.Key.ToString() ?? "" : $"{e.Key}={e.Value}")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }

            return ToStringList(value);
        }

        public static List<string> ToPortBindingList(object? value)
        {
            if (value == null) return new List<string>();
            if (value is string scalar) return new List<string> { scalar };

            if (value is IEnumerable enumerable && value is not IDictionary)
            {
                var list = new List<string>();
                foreach (var item in enumerable)
                {
                    var port = PortToDisplayString(item);
                    if (!string.IsNullOrWhiteSpace(port)) list.Add(port);
                }
                return list;
            }

            var single = PortToDisplayString(value);
            return string.IsNullOrWhiteSpace(single) ? new List<string>() : new List<string> { single };
        }

        public static List<VolumeMountInfo> ToVolumeMounts(object? value)
        {
            var result = new List<VolumeMountInfo>();
            if (value == null) return result;

            if (value is string scalar)
            {
                var parsed = ParseShortVolume(scalar);
                if (parsed != null) result.Add(parsed);
                return result;
            }

            if (value is IEnumerable enumerable && value is not IDictionary)
            {
                foreach (var item in enumerable)
                {
                    var mount = ToVolumeMount(item);
                    if (mount != null) result.Add(mount);
                }
                return result;
            }

            var single = ToVolumeMount(value);
            if (single != null) result.Add(single);
            return result;
        }

        public static List<string> ToDependsOnServiceNames(object? value)
        {
            var map = GetMapping(value);
            if (map != null)
                return map.Keys.Select(k => k.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            return ToStringList(value);
        }

        public static List<string> ToNetworkNames(object? value)
        {
            var map = GetMapping(value);
            if (map != null)
                return map.Keys.Select(k => k.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            return ToStringList(value);
        }

        public static string? GetNetworkIpv4(object? networks, string networkName)
        {
            var map = GetMapping(networks);
            if (map == null) return null;

            var config = GetMapping(GetValue(map, networkName));
            return GetValue(config, "ipv4_address")?.ToString();
        }

        public static string? GetBuildLabel(object? build)
        {
            if (build == null) return null;
            if (build is string scalar) return $"build:{scalar}";

            var map = GetMapping(build);
            var context = GetValue(map, "context")?.ToString();
            return string.IsNullOrWhiteSpace(context) ? "build" : $"build:{context}";
        }

        public static Dictionary<object, object> ToMutableServiceMap(string? rawServiceYaml)
        {
            return ParseMapping(rawServiceYaml) ?? new Dictionary<object, object>();
        }

        public static Dictionary<object, object> ToMutableRootMap(string? rawYaml)
        {
            return ParseMapping(rawYaml) ?? new Dictionary<object, object>();
        }

        public static void SetValue(Dictionary<object, object> map, string key, object? value)
        {
            if (value == null) return;
            if (value is string str && string.IsNullOrWhiteSpace(str)) return;
            if (value is ICollection collection && collection.Count == 0) return;
            map[key] = value;
        }

        public static bool HasKey(Dictionary<object, object> map, string key)
        {
            return map.Keys.Any(k => string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase));
        }

        private static VolumeMountInfo? ToVolumeMount(object? item)
        {
            if (item == null) return null;
            if (item is string scalar) return ParseShortVolume(scalar);

            var map = GetMapping(item);
            if (map == null) return null;

            string? source = GetValue(map, "source")?.ToString();
            string? target = GetValue(map, "target")?.ToString();
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return null;

            return new VolumeMountInfo(source, target);
        }

        private static VolumeMountInfo? ParseShortVolume(string volume)
        {
            int lastColon = volume.LastIndexOf(':');
            if (lastColon <= 0 || lastColon == 1) return null;

            string source = volume.Substring(0, lastColon);
            string target = volume.Substring(lastColon + 1);

            int optionColon = target.IndexOf(':');
            if (optionColon > 0) target = target.Substring(0, optionColon);

            return string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)
                ? null
                : new VolumeMountInfo(source, target);
        }

        private static string? PortToDisplayString(object? item)
        {
            if (item == null) return null;
            if (IsScalar(item)) return item.ToString();

            var map = GetMapping(item);
            if (map == null) return SerializeObject(item).Trim();

            string? target = GetValue(map, "target")?.ToString();
            string? published = GetValue(map, "published")?.ToString();
            string? protocol = GetValue(map, "protocol")?.ToString();
            string? hostIp = GetValue(map, "host_ip")?.ToString();

            if (string.IsNullOrWhiteSpace(target)) return null;

            string left = string.IsNullOrWhiteSpace(published) ? target : $"{published}:{target}";
            if (!string.IsNullOrWhiteSpace(hostIp) && !string.IsNullOrWhiteSpace(published))
                left = $"{hostIp}:{left}";
            if (!string.IsNullOrWhiteSpace(protocol))
                left = $"{left}/{protocol}";
            return left;
        }

        private static bool IsScalar(object value)
        {
            return value is string || value is bool || value is char || value.GetType().IsPrimitive || value is decimal;
        }
    }

    internal record VolumeMountInfo(string Source, string Target);
}
