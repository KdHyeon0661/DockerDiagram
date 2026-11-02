using System.Globalization;
using System.Text.RegularExpressions;
using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static partial class ContainerMountParser
    {
        public static List<ContainerMountSpec> ParseLegacyMany(IEnumerable<string>? values) =>
            values?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(ParseLegacy)
                .ToList() ?? [];

        public static ContainerMountSpec ParseLegacy(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("마운트 값이 비어 있습니다.", nameof(value));

            string text = value.Trim();
            bool readOnly = false;
            if (text.EndsWith(":ro", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(":rw", StringComparison.OrdinalIgnoreCase))
            {
                readOnly = text.EndsWith(":ro", StringComparison.OrdinalIgnoreCase);
                text = text[..^3];
            }

            int separator = FindSourceTargetSeparator(text);
            if (separator < 0 && (text.StartsWith('/') || WindowsPathRegex().IsMatch(text)))
            {
                return new ContainerMountSpec
                {
                    Kind = MountKind.Volume,
                    Target = text,
                    ReadOnly = readOnly
                };
            }
            if (separator <= 0 || separator >= text.Length - 1)
                throw new FormatException($"마운트는 Source:Target 형식이어야 합니다: {value}");

            string source = text[..separator].Trim();
            string target = text[(separator + 1)..].Trim();
            return new ContainerMountSpec
            {
                Kind = LooksLikeBindSource(source) ? MountKind.Bind : MountKind.Volume,
                Source = source,
                Target = target,
                ReadOnly = readOnly
            };
        }

        public static ContainerMountSpec ParseLongMount(string value)
        {
            var options = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
                .ToDictionary(
                    parts => parts[0].ToLowerInvariant(),
                    parts => parts.Length > 1 ? parts[1] : "true",
                    StringComparer.OrdinalIgnoreCase);

            string type = options.GetValueOrDefault("type", "volume").Trim();
            MountKind kind = type.ToLowerInvariant() switch
            {
                "bind" => MountKind.Bind,
                "tmpfs" => MountKind.Tmpfs,
                "volume" => MountKind.Volume,
                _ => throw new FormatException($"지원하지 않는 mount type입니다: {type}")
            };
            string source = options.GetValueOrDefault("source", options.GetValueOrDefault("src", string.Empty));
            string target = options.GetValueOrDefault("target", options.GetValueOrDefault("dst", options.GetValueOrDefault("destination", string.Empty)));
            bool readOnly = options.ContainsKey("readonly") || options.ContainsKey("ro");

            if (!IsAbsoluteContainerPath(target))
                throw new FormatException($"mount target은 절대 컨테이너 경로여야 합니다: {target}");
            if (kind == MountKind.Bind && !IsAbsoluteHostPath(source))
                throw new FormatException($"bind source는 절대 Docker 호스트 경로여야 합니다: {source}");

            return new ContainerMountSpec
            {
                Kind = kind,
                Source = source,
                Target = target,
                ReadOnly = readOnly,
                BindPropagation = options.GetValueOrDefault("bind-propagation", "rprivate"),
                VolumeNoCopy = options.ContainsKey("volume-nocopy"),
                TmpfsSizeBytes = ParseByteSize(options.GetValueOrDefault("tmpfs-size", "0")),
                TmpfsMode = ParseOctalMode(options.GetValueOrDefault("tmpfs-mode", "1777"))
            };
        }

        public static ContainerMountSpec ParseTmpfs(string value)
        {
            string[] targetAndOptions = value.Split(':', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(targetAndOptions[0]))
                throw new FormatException("tmpfs 대상 경로가 비어 있습니다.");
            if (!IsAbsoluteContainerPath(targetAndOptions[0]))
                throw new FormatException($"tmpfs 대상은 절대 컨테이너 경로여야 합니다: {targetAndOptions[0]}");

            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (targetAndOptions.Length > 1)
            {
                foreach (string option in targetAndOptions[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] pair = option.Split('=', 2, StringSplitOptions.TrimEntries);
                    options[pair[0]] = pair.Length > 1 ? pair[1] : "true";
                }
            }

            return new ContainerMountSpec
            {
                Kind = MountKind.Tmpfs,
                Target = targetAndOptions[0],
                ReadOnly = options.ContainsKey("ro") || options.ContainsKey("readonly"),
                TmpfsSizeBytes = ParseByteSize(options.GetValueOrDefault("size", "0")),
                TmpfsMode = ParseOctalMode(options.GetValueOrDefault("mode", "1777"))
            };
        }

        public static List<ContainerMountSpec> ParseDockerRunTokens(IReadOnlyList<string> tokens)
        {
            var result = new List<ContainerMountSpec>();
            for (int index = 0; index < tokens.Count; index++)
            {
                string token = tokens[index];
                if (token is "-v" or "--volume")
                {
                    if (++index >= tokens.Count) throw new FormatException($"{token} 다음에 마운트 값이 필요합니다.");
                    result.Add(ParseLegacy(tokens[index]));
                }
                else if (token.StartsWith("--volume=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ParseLegacy(token["--volume=".Length..]));
                }
                else if (token.StartsWith("-v=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ParseLegacy(token[3..]));
                }
                else if (token.StartsWith("-v", StringComparison.Ordinal) && token.Length > 2)
                {
                    result.Add(ParseLegacy(token[2..]));
                }
                else if (token is "--mount")
                {
                    if (++index >= tokens.Count) throw new FormatException("--mount 다음에 설정값이 필요합니다.");
                    result.Add(ParseLongMount(tokens[index]));
                }
                else if (token.StartsWith("--mount=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ParseLongMount(token["--mount=".Length..]));
                }
                else if (token is "--tmpfs")
                {
                    if (++index >= tokens.Count) throw new FormatException("--tmpfs 다음에 대상 경로가 필요합니다.");
                    result.Add(ParseTmpfs(tokens[index]));
                }
                else if (token.StartsWith("--tmpfs=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ParseTmpfs(token["--tmpfs=".Length..]));
                }
            }

            foreach (ContainerMountSpec mount in result)
            {
                if (!IsAbsoluteContainerPath(mount.Target))
                    throw new FormatException($"mount target은 절대 컨테이너 경로여야 합니다: {mount.Target}");
                if (mount.Kind == MountKind.Bind && !IsAbsoluteHostPath(mount.Source))
                    throw new FormatException($"bind source는 절대 Docker 호스트 경로여야 합니다: {mount.Source}");
            }

            string? duplicateTarget = result
                .GroupBy(mount => mount.Target, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1)
                ?.Key;
            if (!string.IsNullOrWhiteSpace(duplicateTarget))
                throw new FormatException($"중복된 컨테이너 마운트 경로입니다: {duplicateTarget}");
            return result;
        }

        public static bool LooksLikeBindSource(string source) =>
            WindowsPathRegex().IsMatch(source) ||
            WindowsExtendedPathRegex().IsMatch(source) ||
            source.StartsWith("\\\\", StringComparison.Ordinal) ||
            source.StartsWith('/') ||
            source.StartsWith("./", StringComparison.Ordinal) ||
            source.StartsWith("../", StringComparison.Ordinal) ||
            source.StartsWith("~/", StringComparison.Ordinal);

        public static bool IsBindSource(string source) => LooksLikeBindSource(source);

        public static bool IsAbsoluteHostPath(string source) =>
            WindowsPathRegex().IsMatch(source) ||
            WindowsExtendedPathRegex().IsMatch(source) ||
            source.StartsWith("\\\\", StringComparison.Ordinal) ||
            source.StartsWith('/');

        public static bool IsAbsoluteContainerPath(string target) =>
            WindowsPathRegex().IsMatch(target) ||
            WindowsExtendedPathRegex().IsMatch(target) ||
            target.StartsWith("\\\\", StringComparison.Ordinal) ||
            target.StartsWith('/');

        public static bool IsWindowsHostPath(string source) =>
            WindowsPathRegex().IsMatch(source) ||
            WindowsExtendedPathRegex().IsMatch(source) ||
            source.StartsWith("\\\\", StringComparison.Ordinal);

        public static bool HostPathsEqual(string left, string right)
        {
            StringComparison comparison = IsWindowsHostPath(left) || IsWindowsHostPath(right)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(left.TrimEnd('/', '\\'), right.TrimEnd('/', '\\'), comparison);
        }

        public static long ParseByteSize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            string text = value.Trim();
            Match match = ByteSizeRegex().Match(text);
            if (!match.Success)
                throw new FormatException($"tmpfs 크기가 올바르지 않습니다: {value}");

            double amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            long multiplier = match.Groups[2].Value.ToLowerInvariant() switch
            {
                "k" or "kb" or "kib" => 1024L,
                "m" or "mb" or "mib" => 1024L * 1024,
                "g" or "gb" or "gib" => 1024L * 1024 * 1024,
                "t" or "tb" or "tib" => 1024L * 1024 * 1024 * 1024,
                _ => 1L
            };
            return checked((long)(amount * multiplier));
        }

        public static uint ParseOctalMode(string? value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "1777" : value.Trim();
            if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            try
            {
                return Convert.ToUInt32(text, 8);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new FormatException($"tmpfs mode는 8진수여야 합니다: {value}", ex);
            }
        }

        private static int FindSourceTargetSeparator(string value)
        {
            int start = WindowsPathRegex().IsMatch(value)
                ? 2
                : WindowsExtendedPathRegex().IsMatch(value)
                    ? 6
                    : 0;
            return value.IndexOf(':', start);
        }

        [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
        private static partial Regex WindowsPathRegex();

        [GeneratedRegex(@"^\\\\\?\\[A-Za-z]:[\\/]")]
        private static partial Regex WindowsExtendedPathRegex();

        [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([kmgt]?i?b?)?$", RegexOptions.IgnoreCase)]
        private static partial Regex ByteSizeRegex();
    }
}
