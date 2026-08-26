using Docker.DotNet.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// Generates a best-effort Dockerfile from image-level container settings.
    /// Host-specific port bindings, networks and restart policies intentionally
    /// remain outside the Dockerfile.
    /// </summary>
    public static class DockerfileGenerator
    {
        public static string Build(ContainerInspectResponse container, Config? baseConfig = null)
        {
            ArgumentNullException.ThrowIfNull(container);
            ArgumentNullException.ThrowIfNull(container.Config);

            Config config = container.Config;
            var lines = new List<string>
            {
                $"FROM {config.Image}",
                string.Empty
            };

            AddIfChanged(lines, "WORKDIR", config.WorkingDir, baseConfig?.WorkingDir);
            AddIfChanged(lines, "USER", config.User, baseConfig?.User);

            AddEnvironment(lines, config.Env, baseConfig?.Env);
            AddLabels(lines, config.Labels, baseConfig?.Labels);
            AddExposedPorts(lines, config.ExposedPorts, baseConfig?.ExposedPorts);
            AddVolumes(lines, container, baseConfig);

            if (!HealthchecksEqual(config.Healthcheck, baseConfig?.Healthcheck))
                AddHealthcheck(lines, config.Healthcheck);

            AddIfChanged(lines, "STOPSIGNAL", config.StopSignal, baseConfig?.StopSignal);

            if (!SequenceEqual(config.Shell, baseConfig?.Shell))
                lines.Add($"SHELL {ToJsonArray(config.Shell)}");

            if (!SequenceEqual(config.Entrypoint, baseConfig?.Entrypoint))
                lines.Add($"ENTRYPOINT {ToJsonArray(config.Entrypoint)}");

            if (!SequenceEqual(config.Cmd, baseConfig?.Cmd))
                lines.Add($"CMD {ToJsonArray(config.Cmd)}");

            while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1]))
                lines.RemoveAt(lines.Count - 1);

            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        private static void AddIfChanged(
            ICollection<string> lines,
            string instruction,
            string? value,
            string? baseValue)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, baseValue, StringComparison.Ordinal))
            {
                return;
            }

            lines.Add($"{instruction} {value}");
        }

        private static void AddEnvironment(
            ICollection<string> lines,
            IList<string>? environment,
            IList<string>? baseEnvironment)
        {
            var baseValues = ToEnvironmentMap(baseEnvironment);
            if (environment == null)
                return;

            foreach (string item in environment)
            {
                (string key, string value) = SplitEnvironment(item);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (baseValues.TryGetValue(key, out string? baseValue) &&
                    string.Equals(value, baseValue, StringComparison.Ordinal))
                {
                    continue;
                }

                lines.Add($"ENV {key}=\"{EscapeQuotedValue(value)}\"");
            }
        }

        private static Dictionary<string, string> ToEnvironmentMap(IList<string>? values)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (values == null)
                return result;

            foreach (string item in values)
            {
                (string key, string value) = SplitEnvironment(item);
                if (!string.IsNullOrWhiteSpace(key))
                    result[key] = value;
            }

            return result;
        }

        private static (string Key, string Value) SplitEnvironment(string item)
        {
            int separator = item.IndexOf('=');
            return separator < 0
                ? (item, string.Empty)
                : (item[..separator], item[(separator + 1)..]);
        }

        private static void AddLabels(
            ICollection<string> lines,
            IDictionary<string, string>? labels,
            IDictionary<string, string>? baseLabels)
        {
            if (labels == null)
                return;

            foreach ((string key, string value) in labels.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                if (baseLabels != null &&
                    baseLabels.TryGetValue(key, out string? baseValue) &&
                    string.Equals(value, baseValue, StringComparison.Ordinal))
                {
                    continue;
                }

                lines.Add($"LABEL {key}=\"{EscapeQuotedValue(value)}\"");
            }
        }

        private static void AddExposedPorts(
            ICollection<string> lines,
            IDictionary<string, EmptyStruct>? ports,
            IDictionary<string, EmptyStruct>? basePorts)
        {
            if (ports == null)
                return;

            foreach (string port in ports.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (basePorts?.ContainsKey(port) == true)
                    continue;

                lines.Add($"EXPOSE {port}");
            }
        }

        private static void AddVolumes(
            ICollection<string> lines,
            ContainerInspectResponse container,
            Config? baseConfig)
        {
            var baseVolumes = new HashSet<string>(
                baseConfig?.Volumes?.Keys ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            var volumes = new SortedSet<string>(StringComparer.Ordinal);

            if (container.Config.Volumes != null)
            {
                foreach (string path in container.Config.Volumes.Keys)
                    volumes.Add(path);
            }

            if (container.Mounts != null)
            {
                foreach (MountPoint mount in container.Mounts)
                {
                    if (!string.IsNullOrWhiteSpace(mount.Destination))
                        volumes.Add(mount.Destination);
                }
            }

            foreach (string path in volumes.Where(path => !baseVolumes.Contains(path)))
                lines.Add($"VOLUME {ToJsonArray(new[] { path })}");
        }

        private static void AddHealthcheck(ICollection<string> lines, HealthConfig? healthcheck)
        {
            if (healthcheck?.Test == null || healthcheck.Test.Count == 0)
                return;

            string kind = healthcheck.Test[0];
            if (string.Equals(kind, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add("HEALTHCHECK NONE");
                return;
            }

            var options = new List<string>();
            if (healthcheck.Interval > TimeSpan.Zero)
                options.Add($"--interval={FormatDuration(healthcheck.Interval)}");
            if (healthcheck.Timeout > TimeSpan.Zero)
                options.Add($"--timeout={FormatDuration(healthcheck.Timeout)}");
            if (healthcheck.StartPeriod > 0)
                options.Add($"--start-period={FormatNanoseconds(healthcheck.StartPeriod)}");
            if (healthcheck.Retries > 0)
                options.Add($"--retries={healthcheck.Retries.ToString(CultureInfo.InvariantCulture)}");

            string prefix = options.Count == 0
                ? "HEALTHCHECK"
                : $"HEALTHCHECK {string.Join(" ", options)}";

            if (string.Equals(kind, "CMD", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{prefix} CMD {ToJsonArray(healthcheck.Test.Skip(1))}");
            }
            else if (string.Equals(kind, "CMD-SHELL", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{prefix} CMD {string.Join(" ", healthcheck.Test.Skip(1))}");
            }
        }

        private static bool HealthchecksEqual(HealthConfig? left, HealthConfig? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            return SequenceEqual(left.Test, right.Test) &&
                   left.Interval == right.Interval &&
                   left.Timeout == right.Timeout &&
                   left.StartPeriod == right.StartPeriod &&
                   left.Retries == right.Retries;
        }

        private static bool SequenceEqual(IEnumerable<string>? left, IEnumerable<string>? right)
        {
            return (left ?? Enumerable.Empty<string>())
                .SequenceEqual(right ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        }

        private static string ToJsonArray(IEnumerable<string>? values)
        {
            return JsonSerializer.Serialize(values?.ToArray() ?? Array.Empty<string>());
        }

        private static string EscapeQuotedValue(string value)
        {
            return value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("$", "\\$", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.Ticks % TimeSpan.TicksPerHour == 0)
                return $"{value.Ticks / TimeSpan.TicksPerHour}h";
            if (value.Ticks % TimeSpan.TicksPerMinute == 0)
                return $"{value.Ticks / TimeSpan.TicksPerMinute}m";
            if (value.Ticks % TimeSpan.TicksPerSecond == 0)
                return $"{value.Ticks / TimeSpan.TicksPerSecond}s";
            if (value.Ticks % TimeSpan.TicksPerMillisecond == 0)
                return $"{value.Ticks / TimeSpan.TicksPerMillisecond}ms";

            return FormatNanoseconds(checked(value.Ticks * 100));
        }

        private static string FormatNanoseconds(long nanoseconds)
        {
            const long nanosecondsPerHour = 3_600_000_000_000;
            const long nanosecondsPerMinute = 60_000_000_000;
            const long nanosecondsPerSecond = 1_000_000_000;
            const long nanosecondsPerMillisecond = 1_000_000;

            if (nanoseconds % nanosecondsPerHour == 0)
                return $"{nanoseconds / nanosecondsPerHour}h";
            if (nanoseconds % nanosecondsPerMinute == 0)
                return $"{nanoseconds / nanosecondsPerMinute}m";
            if (nanoseconds % nanosecondsPerSecond == 0)
                return $"{nanoseconds / nanosecondsPerSecond}s";
            if (nanoseconds % nanosecondsPerMillisecond == 0)
                return $"{nanoseconds / nanosecondsPerMillisecond}ms";

            return $"{nanoseconds}ns";
        }
    }
}
