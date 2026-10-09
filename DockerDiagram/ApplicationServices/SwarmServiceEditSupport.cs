using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace DockerDiagram.ApplicationServices
{
    public sealed record SwarmServiceEditSnapshot(
        string ServiceId,
        ulong Version,
        SwarmServiceSpecOptions Spec,
        string RawJson);

    /// <summary>Docker inspect 응답을 편집 가능한 Swarm service 모델로 역변환합니다.</summary>
    public static class SwarmServiceSpecReader
    {
        public static SwarmServiceEditSnapshot Read(object raw)
        {
            ArgumentNullException.ThrowIfNull(raw);
            JObject root = raw as JObject ?? JObject.FromObject(raw);
            string serviceId = root["ID"]?.Value<string>()?.Trim() ?? string.Empty;
            if (serviceId.Length == 0)
                throw new InvalidOperationException("Swarm service inspect 응답에 ID가 없습니다.");

            ulong version = root["Version"]?["Index"]?.Value<ulong>()
                ?? throw new InvalidOperationException("Swarm service inspect 응답에 Version.Index가 없습니다.");
            JObject spec = root["Spec"] as JObject
                ?? throw new InvalidOperationException("Swarm service inspect 응답에 Spec이 없습니다.");
            JObject taskTemplate = spec["TaskTemplate"] as JObject ?? new JObject();
            JObject container = taskTemplate["ContainerSpec"] as JObject ?? new JObject();
            SwarmServiceModeKind mode = SwarmServiceSpecMapper.ReadCurrentMode(spec);

            var result = new SwarmServiceSpecOptions
            {
                Name = RequiredString(spec, "Name", "Service 이름"),
                Image = RequiredString(container, "Image", "Service 이미지"),
                Mode = mode,
                Replicas = mode == SwarmServiceModeKind.Global
                    ? null
                    : spec["Mode"]?["Replicated"]?["Replicas"]?.Value<ulong?>() ?? 1,
                Command = ReadStrings(container["Command"]),
                Arguments = ReadStrings(container["Args"]),
                EnvironmentVariables = ReadStrings(container["Env"]),
                Labels = ReadStringMap(spec["Labels"]),
                Mounts = ReadMounts(container["Mounts"]),
                Networks = ReadNetworks(spec["Networks"]),
                PublishedPorts = ReadPorts(spec["EndpointSpec"]?["Ports"]),
                RestartPolicy = ReadRestartPolicy(taskTemplate["RestartPolicy"] as JObject),
                UpdatePolicy = ReadUpdatePolicy(spec["UpdateConfig"] as JObject, allowRollback: true),
                RollbackPolicy = ReadUpdatePolicy(spec["RollbackConfig"] as JObject, allowRollback: false)
            };

            result.Validate();
            return new SwarmServiceEditSnapshot(serviceId, version, result, root.ToString());
        }

        private static string RequiredString(JObject source, string propertyName, string displayName)
        {
            string value = source[propertyName]?.Value<string>()?.Trim() ?? string.Empty;
            if (value.Length == 0)
                throw new InvalidOperationException($"현재 {displayName} 정보를 찾을 수 없습니다.");
            return value;
        }

        private static IReadOnlyList<string> ReadStrings(JToken? token) =>
            token is JArray values
                ? values.Values<string>().OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                : Array.Empty<string>();

        private static IReadOnlyDictionary<string, string> ReadStringMap(JToken? token)
        {
            if (token is not JObject map) return new Dictionary<string, string>();
            return map.Properties().ToDictionary(
                property => property.Name,
                property => property.Value.Value<string>() ?? string.Empty,
                StringComparer.Ordinal);
        }

        private static IReadOnlyList<SwarmMountOptions> ReadMounts(JToken? token)
        {
            if (token is not JArray mounts) return Array.Empty<SwarmMountOptions>();
            return mounts.OfType<JObject>().Select(mount =>
            {
                JObject? volumeOptions = mount["VolumeOptions"] as JObject;
                JObject? driverConfig = volumeOptions?["DriverConfig"] as JObject;
                return new SwarmMountOptions(
                    ParseEnum<SwarmMountKind>(mount["Type"]?.Value<string>(), SwarmMountKind.Volume),
                    mount["Source"]?.Value<string>() ?? string.Empty,
                    mount["Target"]?.Value<string>() ?? string.Empty,
                    mount["ReadOnly"]?.Value<bool>() ?? false,
                    driverConfig?["Name"]?.Value<string>(),
                    ReadStringMap(driverConfig?["Options"]),
                    ReadStringMap(volumeOptions?["Labels"]));
            }).ToArray();
        }

        private static IReadOnlyList<SwarmNetworkAttachmentOptions> ReadNetworks(JToken? token)
        {
            if (token is not JArray networks) return Array.Empty<SwarmNetworkAttachmentOptions>();
            return networks.OfType<JObject>().Select(network => new SwarmNetworkAttachmentOptions(
                network["Target"]?.Value<string>() ?? string.Empty,
                ReadStrings(network["Aliases"]),
                ReadStringMap(network["DriverOpts"]))).ToArray();
        }

        private static IReadOnlyList<SwarmPublishedPortOptions> ReadPorts(JToken? token)
        {
            if (token is not JArray ports) return Array.Empty<SwarmPublishedPortOptions>();
            return ports.OfType<JObject>().Select(port => new SwarmPublishedPortOptions(
                port["TargetPort"]?.Value<uint>() ?? 0,
                port["PublishedPort"]?.Value<uint?>(),
                ParseEnum<SwarmPortProtocol>(port["Protocol"]?.Value<string>(), SwarmPortProtocol.Tcp),
                ParseEnum<SwarmPublishMode>(port["PublishMode"]?.Value<string>(), SwarmPublishMode.Ingress))).ToArray();
        }

        private static SwarmRestartPolicyOptions ReadRestartPolicy(JObject? policy) => new()
        {
            Condition = ParseEnum<SwarmRestartCondition>(policy?["Condition"]?.Value<string>(), SwarmRestartCondition.Any),
            Delay = ReadDuration(policy?["Delay"]),
            MaxAttempts = policy?["MaxAttempts"]?.Value<ulong?>(),
            Window = ReadDuration(policy?["Window"])
        };

        private static SwarmUpdatePolicyOptions ReadUpdatePolicy(JObject? policy, bool allowRollback)
        {
            SwarmUpdateFailureAction action = ParseEnum<SwarmUpdateFailureAction>(
                policy?["FailureAction"]?.Value<string>(),
                SwarmUpdateFailureAction.Pause);
            if (!allowRollback && action == SwarmUpdateFailureAction.Rollback)
                action = SwarmUpdateFailureAction.Pause;

            return new SwarmUpdatePolicyOptions
            {
                Parallelism = policy?["Parallelism"]?.Value<ulong?>() ?? 1,
                Delay = ReadDuration(policy?["Delay"]) ?? TimeSpan.Zero,
                Monitor = ReadDuration(policy?["Monitor"]),
                MaxFailureRatio = policy?["MaxFailureRatio"]?.Value<double?>(),
                FailureAction = action,
                Order = ParseEnum<SwarmUpdateOrder>(policy?["Order"]?.Value<string>(), SwarmUpdateOrder.StopFirst)
            };
        }

        private static TimeSpan? ReadDuration(JToken? token)
        {
            long? nanoseconds = token?.Value<long?>();
            if (!nanoseconds.HasValue) return null;
            if (nanoseconds.Value < 0)
                throw new InvalidOperationException("Docker duration 값은 음수일 수 없습니다.");
            return TimeSpan.FromTicks(nanoseconds.Value / 100L);
        }

        private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string normalized = value.Replace("-", string.Empty, StringComparison.Ordinal);
            return Enum.TryParse(normalized, true, out T parsed) ? parsed : fallback;
        }
    }

    public static class SwarmServiceEditFormatter
    {
        public static SwarmServiceCreateFormInput ToFormInput(SwarmServiceSpecOptions spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            return new SwarmServiceCreateFormInput
            {
                Name = spec.Name,
                Image = spec.Image,
                Mode = DockerValue(spec.Mode),
                Replicas = spec.Replicas?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Command = spec.Command.FirstOrDefault() ?? string.Empty,
                Arguments = JoinLines(spec.Arguments),
                EnvironmentVariables = JoinLines(spec.EnvironmentVariables),
                Labels = JoinLines(spec.Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")),
                PublishedPorts = JoinLines(spec.PublishedPorts.Select(FormatPort)),
                Networks = JoinLines(spec.Networks.Select(FormatNetwork)),
                Mounts = JoinLines(spec.Mounts.Select(FormatMount)),
                RestartCondition = DockerValue(spec.RestartPolicy.Condition),
                RestartDelaySeconds = Seconds(spec.RestartPolicy.Delay),
                RestartMaxAttempts = spec.RestartPolicy.MaxAttempts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                RestartWindowSeconds = Seconds(spec.RestartPolicy.Window),
                UpdateParallelism = spec.UpdatePolicy.Parallelism.ToString(CultureInfo.InvariantCulture),
                UpdateDelaySeconds = Seconds(spec.UpdatePolicy.Delay),
                UpdateMonitorSeconds = Seconds(spec.UpdatePolicy.Monitor),
                UpdateMaxFailureRatio = Ratio(spec.UpdatePolicy.MaxFailureRatio),
                UpdateFailureAction = DockerValue(spec.UpdatePolicy.FailureAction),
                UpdateOrder = DockerValue(spec.UpdatePolicy.Order),
                RollbackParallelism = spec.RollbackPolicy.Parallelism.ToString(CultureInfo.InvariantCulture),
                RollbackDelaySeconds = Seconds(spec.RollbackPolicy.Delay),
                RollbackMonitorSeconds = Seconds(spec.RollbackPolicy.Monitor),
                RollbackMaxFailureRatio = Ratio(spec.RollbackPolicy.MaxFailureRatio),
                RollbackFailureAction = DockerValue(spec.RollbackPolicy.FailureAction),
                RollbackOrder = DockerValue(spec.RollbackPolicy.Order)
            };
        }

        public static SwarmServiceSpecOptions PreserveHiddenSettings(
            SwarmServiceSpecOptions original,
            SwarmServiceSpecOptions edited)
        {
            ArgumentNullException.ThrowIfNull(original);
            ArgumentNullException.ThrowIfNull(edited);

            IReadOnlyList<string> command = original.Command.Count > 1 &&
                                            edited.Command.Count == 1 &&
                                            string.Equals(original.Command[0], edited.Command[0], StringComparison.Ordinal)
                ? original.Command
                : edited.Command;

            var originalNetworks = original.Networks.ToDictionary(network => network.Target, StringComparer.OrdinalIgnoreCase);
            var networks = edited.Networks.Select(network =>
            {
                IReadOnlyDictionary<string, string>? driverOptions = network.DriverOptions;
                if ((driverOptions == null || driverOptions.Count == 0) &&
                    originalNetworks.TryGetValue(network.Target, out SwarmNetworkAttachmentOptions? previous))
                {
                    driverOptions = previous.DriverOptions;
                }
                return new SwarmNetworkAttachmentOptions(network.Target, network.Aliases, driverOptions);
            }).ToArray();

            var mounts = edited.Mounts.Select(mount =>
            {
                SwarmMountOptions? previous = original.Mounts.FirstOrDefault(candidate =>
                    candidate.Kind == mount.Kind &&
                    string.Equals(candidate.Source, mount.Source, StringComparison.Ordinal) &&
                    string.Equals(candidate.Target, mount.Target, StringComparison.Ordinal));
                if (previous == null) return mount;

                return mount with
                {
                    VolumeDriver = string.IsNullOrWhiteSpace(mount.VolumeDriver)
                        ? previous.VolumeDriver
                        : mount.VolumeDriver,
                    VolumeDriverOptions = mount.VolumeDriverOptions is not { Count: > 0 }
                        ? previous.VolumeDriverOptions
                        : mount.VolumeDriverOptions,
                    VolumeLabels = mount.VolumeLabels is not { Count: > 0 }
                        ? previous.VolumeLabels
                        : mount.VolumeLabels
                };
            }).ToArray();

            return new SwarmServiceSpecOptions
            {
                Name = edited.Name,
                Image = edited.Image,
                Mode = edited.Mode,
                Replicas = edited.Replicas,
                Command = command,
                Arguments = edited.Arguments,
                EnvironmentVariables = edited.EnvironmentVariables,
                Labels = edited.Labels,
                PublishedPorts = edited.PublishedPorts,
                Mounts = mounts,
                Networks = networks,
                RestartPolicy = edited.RestartPolicy,
                UpdatePolicy = edited.UpdatePolicy,
                RollbackPolicy = edited.RollbackPolicy
            };
        }

        private static string FormatPort(SwarmPublishedPortOptions port)
        {
            string prefix = port.PublishedPort.HasValue ? $"{port.PublishedPort.Value}:" : string.Empty;
            return $"{prefix}{port.TargetPort}/{DockerValue(port.Protocol)}@{DockerValue(port.PublishMode)}";
        }

        private static string FormatNetwork(SwarmNetworkAttachmentOptions network) =>
            network.Aliases is { Count: > 0 }
                ? $"{network.Target}|{string.Join(",", network.Aliases)}"
                : network.Target;

        private static string FormatMount(SwarmMountOptions mount) =>
            $"{DockerValue(mount.Kind)}|{mount.Source}|{mount.Target}|{(mount.ReadOnly ? "ro" : "rw")}";

        private static string JoinLines(IEnumerable<string> values) => string.Join(Environment.NewLine, values);
        private static string Seconds(TimeSpan? value) => value?.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture) ?? string.Empty;
        private static string Ratio(double? value) => value?.ToString("0.#########", CultureInfo.InvariantCulture) ?? string.Empty;
        private static string DockerValue(SwarmServiceModeKind value) => value == SwarmServiceModeKind.Global ? "global" : "replicated";
        private static string DockerValue(SwarmMountKind value) => value.ToString().ToLowerInvariant();
        private static string DockerValue(SwarmPortProtocol value) => value.ToString().ToLowerInvariant();
        private static string DockerValue(SwarmPublishMode value) => value.ToString().ToLowerInvariant();
        private static string DockerValue(SwarmRestartCondition value) => value == SwarmRestartCondition.OnFailure ? "on-failure" : value.ToString().ToLowerInvariant();
        private static string DockerValue(SwarmUpdateFailureAction value) => value.ToString().ToLowerInvariant();
        private static string DockerValue(SwarmUpdateOrder value) => value == SwarmUpdateOrder.StartFirst ? "start-first" : "stop-first";
    }
}
