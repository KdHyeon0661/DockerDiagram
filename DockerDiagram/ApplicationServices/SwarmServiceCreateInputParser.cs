using DockerDiagram.Models;
using System.Globalization;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>Swarm 서비스 생성 화면의 텍스트 입력을 검증된 애플리케이션 모델로 변환합니다.</summary>
    public static class SwarmServiceCreateInputParser
    {
        public static SwarmServiceCreateOptions Parse(SwarmServiceCreateFormInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            SwarmServiceModeKind mode = ParseEnum<SwarmServiceModeKind>(input.Mode, "service mode");
            ulong? replicas = mode == SwarmServiceModeKind.Global
                ? null
                : ParseRequiredUlong(input.Replicas, "Replica 수");

            var options = new SwarmServiceCreateOptions
            {
                Spec = new SwarmServiceSpecOptions
                {
                    Name = input.Name.Trim(),
                    Image = input.Image.Trim(),
                    Mode = mode,
                    Replicas = replicas,
                    Command = SplitCommand(input.Command),
                    Arguments = Lines(input.Arguments),
                    EnvironmentVariables = Lines(input.EnvironmentVariables),
                    Labels = ParseLabels(input.Labels),
                    PublishedPorts = ParsePorts(input.PublishedPorts),
                    Networks = ParseNetworks(input.Networks),
                    Mounts = ParseMounts(input.Mounts),
                    RestartPolicy = new SwarmRestartPolicyOptions
                    {
                        Condition = ParseEnum<SwarmRestartCondition>(input.RestartCondition, "restart condition"),
                        Delay = ParseOptionalSeconds(input.RestartDelaySeconds, "Restart delay"),
                        MaxAttempts = ParseOptionalUlong(input.RestartMaxAttempts, "Restart max attempts"),
                        Window = ParseOptionalSeconds(input.RestartWindowSeconds, "Restart window")
                    },
                    UpdatePolicy = ParsePolicy(
                        input.UpdateParallelism,
                        input.UpdateDelaySeconds,
                        input.UpdateMonitorSeconds,
                        input.UpdateMaxFailureRatio,
                        input.UpdateFailureAction,
                        input.UpdateOrder,
                        allowRollbackAction: true,
                        "Update"),
                    RollbackPolicy = ParsePolicy(
                        input.RollbackParallelism,
                        input.RollbackDelaySeconds,
                        input.RollbackMonitorSeconds,
                        input.RollbackMaxFailureRatio,
                        input.RollbackFailureAction,
                        input.RollbackOrder,
                        allowRollbackAction: false,
                        "Rollback")
                }
            };

            options.Validate();
            return options;
        }

        private static SwarmUpdatePolicyOptions ParsePolicy(
            string parallelism,
            string delaySeconds,
            string monitorSeconds,
            string maxFailureRatio,
            string failureAction,
            string order,
            bool allowRollbackAction,
            string prefix)
        {
            SwarmUpdateFailureAction action = ParseEnum<SwarmUpdateFailureAction>(failureAction, $"{prefix} failure action");
            if (!allowRollbackAction && action == SwarmUpdateFailureAction.Rollback)
                throw new ArgumentException("Rollback policy의 failure action에는 rollback을 사용할 수 없습니다.");

            return new SwarmUpdatePolicyOptions
            {
                Parallelism = ParseRequiredUlong(parallelism, $"{prefix} parallelism"),
                Delay = ParseOptionalSeconds(delaySeconds, $"{prefix} delay") ?? TimeSpan.Zero,
                Monitor = ParseOptionalSeconds(monitorSeconds, $"{prefix} monitor"),
                MaxFailureRatio = ParseOptionalRatio(maxFailureRatio, $"{prefix} max failure ratio"),
                FailureAction = action,
                Order = ParseEnum<SwarmUpdateOrder>(order, $"{prefix} order")
            };
        }

        private static IReadOnlyList<string> SplitCommand(string text)
        {
            string value = text.Trim();
            return value.Length == 0 ? Array.Empty<string>() : new[] { value };
        }

        private static IReadOnlyList<string> Lines(string text) =>
            (text ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0)
                .ToArray();

        private static IReadOnlyDictionary<string, string> ParseLabels(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in Lines(text))
            {
                int separator = line.IndexOf('=');
                string key = separator < 0 ? line.Trim() : line[..separator].Trim();
                string value = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
                if (key.Length == 0)
                    throw new ArgumentException($"Label key가 비어 있습니다: {line}");
                if (!result.TryAdd(key, value))
                    throw new ArgumentException($"중복된 label key입니다: {key}");
            }
            return result;
        }

        private static IReadOnlyList<SwarmPublishedPortOptions> ParsePorts(string text)
        {
            var result = new List<SwarmPublishedPortOptions>();
            foreach (string line in Lines(text))
            {
                string[] modeParts = line.Split('@', 2, StringSplitOptions.TrimEntries);
                SwarmPublishMode publishMode = modeParts.Length == 2
                    ? ParseEnum<SwarmPublishMode>(modeParts[1], "publish mode")
                    : SwarmPublishMode.Ingress;

                string[] protocolParts = modeParts[0].Split('/', 2, StringSplitOptions.TrimEntries);
                SwarmPortProtocol protocol = protocolParts.Length == 2
                    ? ParseEnum<SwarmPortProtocol>(protocolParts[1], "port protocol")
                    : SwarmPortProtocol.Tcp;

                string[] portParts = protocolParts[0].Split(':', StringSplitOptions.TrimEntries);
                if (portParts.Length is < 1 or > 2)
                    throw new ArgumentException($"포트 형식이 올바르지 않습니다: {line}");

                uint? published = portParts.Length == 2
                    ? ParseRequiredPort(portParts[0], "Published port")
                    : null;
                uint target = ParseRequiredPort(portParts[^1], "Target port");
                result.Add(new SwarmPublishedPortOptions(target, published, protocol, publishMode));
            }
            return result;
        }

        private static IReadOnlyList<SwarmNetworkAttachmentOptions> ParseNetworks(string text)
        {
            var result = new List<SwarmNetworkAttachmentOptions>();
            foreach (string line in Lines(text))
            {
                string[] parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
                string target = parts[0].Trim();
                string[] aliases = parts.Length == 2
                    ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : Array.Empty<string>();
                result.Add(new SwarmNetworkAttachmentOptions(target, aliases));
            }
            return result;
        }

        private static IReadOnlyList<SwarmMountOptions> ParseMounts(string text)
        {
            var result = new List<SwarmMountOptions>();
            foreach (string line in Lines(text))
            {
                string[] parts = line.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length is < 3 or > 4)
                    throw new ArgumentException($"Mount 형식이 올바르지 않습니다: {line}");

                SwarmMountKind kind = ParseEnum<SwarmMountKind>(parts[0], "mount type");
                bool readOnly = false;
                if (parts.Length == 4)
                {
                    readOnly = parts[3].Equals("ro", StringComparison.OrdinalIgnoreCase);
                    if (!readOnly && !parts[3].Equals("rw", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"Mount 접근 모드는 ro 또는 rw여야 합니다: {line}");
                }
                result.Add(new SwarmMountOptions(kind, parts[1], parts[2], readOnly));
            }
            return result;
        }

        private static T ParseEnum<T>(string value, string fieldName) where T : struct, Enum
        {
            string normalized = (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
            if (!Enum.TryParse(normalized, true, out T result))
                throw new ArgumentException($"{fieldName} 값이 올바르지 않습니다: {value}");
            return result;
        }

        private static uint ParseRequiredPort(string value, string fieldName)
        {
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint result) || result is < 1 or > 65535)
                throw new ArgumentException($"{fieldName}는 1~65535 범위의 정수여야 합니다.");
            return result;
        }

        private static ulong ParseRequiredUlong(string value, string fieldName)
        {
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong result))
                throw new ArgumentException($"{fieldName}은(는) 0 이상의 정수여야 합니다.");
            return result;
        }

        private static ulong? ParseOptionalUlong(string value, string fieldName) =>
            string.IsNullOrWhiteSpace(value) ? null : ParseRequiredUlong(value, fieldName);

        private static TimeSpan? ParseOptionalSeconds(string value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0)
                throw new ArgumentException($"{fieldName}은(는) 0 이상의 초 단위 숫자여야 합니다.");
            return TimeSpan.FromSeconds(seconds);
        }

        private static double? ParseOptionalRatio(string value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) || result is < 0 or > 1)
                throw new ArgumentException($"{fieldName}은(는) 0~1 범위의 숫자여야 합니다.");
            return result;
        }
    }

    public sealed class SwarmServiceCreateFormInput
    {
        public string Name { get; init; } = string.Empty;
        public string Image { get; init; } = string.Empty;
        public string Mode { get; init; } = "replicated";
        public string Replicas { get; init; } = "1";
        public string Command { get; init; } = string.Empty;
        public string Arguments { get; init; } = string.Empty;
        public string EnvironmentVariables { get; init; } = string.Empty;
        public string Labels { get; init; } = string.Empty;
        public string PublishedPorts { get; init; } = string.Empty;
        public string Networks { get; init; } = string.Empty;
        public string Mounts { get; init; } = string.Empty;
        public string RestartCondition { get; init; } = "any";
        public string RestartDelaySeconds { get; init; } = "5";
        public string RestartMaxAttempts { get; init; } = string.Empty;
        public string RestartWindowSeconds { get; init; } = string.Empty;
        public string UpdateParallelism { get; init; } = "1";
        public string UpdateDelaySeconds { get; init; } = "0";
        public string UpdateMonitorSeconds { get; init; } = string.Empty;
        public string UpdateMaxFailureRatio { get; init; } = string.Empty;
        public string UpdateFailureAction { get; init; } = "pause";
        public string UpdateOrder { get; init; } = "stop-first";
        public string RollbackParallelism { get; init; } = "1";
        public string RollbackDelaySeconds { get; init; } = "0";
        public string RollbackMonitorSeconds { get; init; } = string.Empty;
        public string RollbackMaxFailureRatio { get; init; } = string.Empty;
        public string RollbackFailureAction { get; init; } = "pause";
        public string RollbackOrder { get; init; } = "stop-first";
    }
}
