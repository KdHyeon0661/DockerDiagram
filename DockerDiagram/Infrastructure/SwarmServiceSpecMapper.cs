using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// UI/애플리케이션 모델을 Docker Engine ServiceSpec JSON으로 변환합니다.
    /// Update에서는 현재 spec을 복제한 뒤 지원 필드만 바꿔 아직 UI에 없는 설정을 보존합니다.
    /// </summary>
    public static class SwarmServiceSpecMapper
    {
        public static JObject BuildForCreate(SwarmServiceSpecOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return BuildForCreate(new SwarmServiceCreateOptions { Spec = options });
        }

        public static JObject BuildForCreate(SwarmServiceCreateOptions createOptions)
        {
            ArgumentNullException.ThrowIfNull(createOptions);
            createOptions.Validate();
            SwarmServiceSpecOptions options = createOptions.Spec;

            JObject taskTemplate = BuildTaskTemplate(options);
            if (createOptions.PlacementConstraints.Count > 0)
            {
                taskTemplate["Placement"] = new JObject
                {
                    ["Constraints"] = new JArray(createOptions.PlacementConstraints.Select(value => value.Trim()))
                };
            }

            JObject containerSpec = (JObject)taskTemplate["ContainerSpec"]!;
            if (createOptions.Secrets.Count > 0)
                containerSpec["Secrets"] = BuildReferences(createOptions.Secrets, "Secret");
            if (createOptions.Configs.Count > 0)
                containerSpec["Configs"] = BuildReferences(createOptions.Configs, "Config");

            return new JObject
            {
                ["Name"] = options.Name.Trim(),
                ["Labels"] = BuildStringMap(options.Labels),
                ["TaskTemplate"] = taskTemplate,
                ["Mode"] = BuildMode(options),
                ["UpdateConfig"] = BuildUpdatePolicy(options.UpdatePolicy, allowRollback: true),
                ["RollbackConfig"] = BuildUpdatePolicy(options.RollbackPolicy, allowRollback: false),
                ["Networks"] = BuildNetworks(options.Networks),
                ["EndpointSpec"] = new JObject
                {
                    ["Ports"] = BuildPorts(options.PublishedPorts)
                }
            };
        }

        public static JObject MergeForUpdate(JObject currentSpec, SwarmServiceSpecOptions options)
        {
            ArgumentNullException.ThrowIfNull(currentSpec);
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();

            JObject merged = (JObject)currentSpec.DeepClone();
            ValidateImmutableFields(merged, options);

            merged["Name"] = options.Name.Trim();
            merged["Labels"] = BuildStringMap(options.Labels);

            JObject taskTemplate = GetOrCreateObject(merged, "TaskTemplate");
            JObject containerSpec = GetOrCreateObject(taskTemplate, "ContainerSpec");
            ApplyContainerSpec(containerSpec, options);
            taskTemplate["RestartPolicy"] = BuildRestartPolicy(options.RestartPolicy);

            merged["Mode"] = BuildMode(options);
            merged["UpdateConfig"] = BuildUpdatePolicy(options.UpdatePolicy, allowRollback: true);
            merged["RollbackConfig"] = BuildUpdatePolicy(options.RollbackPolicy, allowRollback: false);
            merged["Networks"] = BuildNetworks(options.Networks);

            JObject endpointSpec = GetOrCreateObject(merged, "EndpointSpec");
            endpointSpec["Ports"] = BuildPorts(options.PublishedPorts);
            return merged;
        }

        private static JObject BuildTaskTemplate(SwarmServiceSpecOptions options)
        {
            var containerSpec = new JObject();
            ApplyContainerSpec(containerSpec, options);
            return new JObject
            {
                ["ContainerSpec"] = containerSpec,
                ["RestartPolicy"] = BuildRestartPolicy(options.RestartPolicy)
            };
        }

        private static void ApplyContainerSpec(JObject containerSpec, SwarmServiceSpecOptions options)
        {
            containerSpec["Image"] = options.Image.Trim();
            containerSpec["Command"] = new JArray(options.Command);
            containerSpec["Args"] = new JArray(options.Arguments);
            containerSpec["Env"] = new JArray(options.EnvironmentVariables);
            containerSpec["Mounts"] = new JArray(options.Mounts.Select(BuildMount));
        }

        private static JObject BuildMount(SwarmMountOptions mount)
        {
            var result = new JObject
            {
                ["Type"] = ToDockerValue(mount.Kind),
                ["Source"] = mount.Source?.Trim() ?? string.Empty,
                ["Target"] = mount.Target.Trim(),
                ["ReadOnly"] = mount.ReadOnly
            };
            if (mount.Kind != SwarmMountKind.Volume) return result;

            var volumeOptions = new JObject();
            if (mount.VolumeLabels is { Count: > 0 })
                volumeOptions["Labels"] = BuildStringMap(mount.VolumeLabels);

            bool hasDriver = !string.IsNullOrWhiteSpace(mount.VolumeDriver);
            if (hasDriver || mount.VolumeDriverOptions is { Count: > 0 })
            {
                var driverConfig = new JObject
                {
                    ["Name"] = hasDriver ? mount.VolumeDriver!.Trim() : "local"
                };
                if (mount.VolumeDriverOptions is { Count: > 0 })
                    driverConfig["Options"] = BuildStringMap(mount.VolumeDriverOptions);
                volumeOptions["DriverConfig"] = driverConfig;
            }

            if (volumeOptions.HasValues) result["VolumeOptions"] = volumeOptions;
            return result;
        }

        private static JObject BuildMode(SwarmServiceSpecOptions options) =>
            options.Mode switch
            {
                SwarmServiceModeKind.Replicated => new JObject
                {
                    ["Replicated"] = new JObject { ["Replicas"] = options.Replicas!.Value }
                },
                SwarmServiceModeKind.Global => new JObject { ["Global"] = new JObject() },
                _ => throw new ArgumentOutOfRangeException(nameof(options.Mode), options.Mode, "지원하지 않는 Swarm service mode입니다.")
            };

        private static JObject BuildRestartPolicy(SwarmRestartPolicyOptions policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            policy.Validate();
            var result = new JObject { ["Condition"] = ToDockerValue(policy.Condition) };
            AddDuration(result, "Delay", policy.Delay);
            if (policy.MaxAttempts.HasValue) result["MaxAttempts"] = policy.MaxAttempts.Value;
            AddDuration(result, "Window", policy.Window);
            return result;
        }

        private static JObject BuildUpdatePolicy(SwarmUpdatePolicyOptions policy, bool allowRollback)
        {
            ArgumentNullException.ThrowIfNull(policy);
            policy.Validate();
            if (!allowRollback && policy.FailureAction == SwarmUpdateFailureAction.Rollback)
                throw new ArgumentException("Rollback policy의 failure action에는 rollback을 사용할 수 없습니다.", nameof(policy));

            var result = new JObject
            {
                ["Parallelism"] = policy.Parallelism,
                ["Delay"] = ToNanoseconds(policy.Delay),
                ["FailureAction"] = ToDockerValue(policy.FailureAction),
                ["Order"] = ToDockerValue(policy.Order)
            };
            AddDuration(result, "Monitor", policy.Monitor);
            if (policy.MaxFailureRatio.HasValue) result["MaxFailureRatio"] = policy.MaxFailureRatio.Value;
            return result;
        }

        private static JArray BuildNetworks(IEnumerable<SwarmNetworkAttachmentOptions> networks) =>
            new(networks.Select(network =>
            {
                var result = new JObject { ["Target"] = network.Target.Trim() };
                if (network.Aliases is { Count: > 0 })
                    result["Aliases"] = new JArray(network.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Select(alias => alias.Trim()));
                if (network.DriverOptions is { Count: > 0 })
                    result["DriverOpts"] = BuildStringMap(network.DriverOptions);
                return result;
            }));

        private static JArray BuildPorts(IEnumerable<SwarmPublishedPortOptions> ports) =>
            new(ports.Select(port =>
            {
                var result = new JObject
                {
                    ["Protocol"] = ToDockerValue(port.Protocol),
                    ["TargetPort"] = port.TargetPort,
                    ["PublishMode"] = ToDockerValue(port.PublishMode)
                };
                if (port.PublishedPort.HasValue) result["PublishedPort"] = port.PublishedPort.Value;
                return result;
            }));

        private static JArray BuildReferences(
            IEnumerable<SwarmServiceResourceReferenceOptions> references,
            string prefix) =>
            new(references.Select(reference => new JObject
            {
                [$"{prefix}ID"] = reference.ResourceId.Trim(),
                [$"{prefix}Name"] = reference.ResourceName.Trim(),
                ["File"] = new JObject
                {
                    ["Name"] = reference.FileName.Trim(),
                    ["UID"] = reference.Uid,
                    ["GID"] = reference.Gid,
                    ["Mode"] = reference.Mode
                }
            }));

        private static JObject BuildStringMap(IEnumerable<KeyValuePair<string, string>> values)
        {
            var result = new JObject();
            foreach (KeyValuePair<string, string> entry in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                result[entry.Key] = entry.Value ?? string.Empty;
            return result;
        }

        private static void ValidateImmutableFields(JObject currentSpec, SwarmServiceSpecOptions options)
        {
            string currentName = currentSpec["Name"]?.Value<string>()?.Trim() ?? string.Empty;
            if (currentName.Length > 0 && !currentName.Equals(options.Name.Trim(), StringComparison.Ordinal))
                throw new NotSupportedException("Docker Swarm service 이름은 생성 후 변경할 수 없습니다.");

            SwarmServiceModeKind currentMode = ReadCurrentMode(currentSpec);
            if (currentMode != options.Mode)
                throw new NotSupportedException("Docker Swarm service의 replicated/global mode는 생성 후 변경할 수 없습니다.");
        }

        public static SwarmServiceModeKind ReadCurrentMode(JObject serviceSpec)
        {
            ArgumentNullException.ThrowIfNull(serviceSpec);
            JObject mode = serviceSpec["Mode"] as JObject
                ?? throw new InvalidOperationException("현재 Swarm service mode 정보를 찾을 수 없습니다.");
            if (mode["Replicated"] != null) return SwarmServiceModeKind.Replicated;
            if (mode["Global"] != null) return SwarmServiceModeKind.Global;
            throw new NotSupportedException("ReplicatedJob/GlobalJob service는 이 편집 계약에서 지원하지 않습니다.");
        }

        public static long ToNanoseconds(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), "Docker duration은 음수일 수 없습니다.");
            return checked(value.Ticks * 100L);
        }

        private static void AddDuration(JObject target, string propertyName, TimeSpan? value)
        {
            if (value.HasValue) target[propertyName] = ToNanoseconds(value.Value);
        }

        private static JObject GetOrCreateObject(JObject parent, string propertyName)
        {
            if (parent[propertyName] is JObject existing) return existing;
            var created = new JObject();
            parent[propertyName] = created;
            return created;
        }

        private static string ToDockerValue(SwarmMountKind value) => value switch
        {
            SwarmMountKind.Volume => "volume",
            SwarmMountKind.Bind => "bind",
            SwarmMountKind.Tmpfs => "tmpfs",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

        private static string ToDockerValue(SwarmRestartCondition value) => value switch
        {
            SwarmRestartCondition.None => "none",
            SwarmRestartCondition.OnFailure => "on-failure",
            SwarmRestartCondition.Any => "any",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

        private static string ToDockerValue(SwarmUpdateFailureAction value) => value switch
        {
            SwarmUpdateFailureAction.Pause => "pause",
            SwarmUpdateFailureAction.Continue => "continue",
            SwarmUpdateFailureAction.Rollback => "rollback",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

        private static string ToDockerValue(SwarmUpdateOrder value) => value switch
        {
            SwarmUpdateOrder.StopFirst => "stop-first",
            SwarmUpdateOrder.StartFirst => "start-first",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

        private static string ToDockerValue(SwarmPortProtocol value) => value switch
        {
            SwarmPortProtocol.Tcp => "tcp",
            SwarmPortProtocol.Udp => "udp",
            SwarmPortProtocol.Sctp => "sctp",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

        private static string ToDockerValue(SwarmPublishMode value) => value switch
        {
            SwarmPublishMode.Ingress => "ingress",
            SwarmPublishMode.Host => "host",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
    }

    public sealed record SwarmServiceCreateApiResponse(string ServiceId, IReadOnlyList<string> Warnings);

    public static class SwarmServiceMutationResponseParser
    {
        public static SwarmServiceCreateApiResponse ParseCreate(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                throw new InvalidOperationException("Docker Engine의 service 생성 응답이 비어 있습니다.");

            JObject response = JObject.Parse(responseBody);
            string serviceId = response["ID"]?.Value<string>()?.Trim() ?? string.Empty;
            if (serviceId.Length == 0)
                throw new InvalidOperationException("Docker Engine의 service 생성 응답에 ID가 없습니다.");
            return new(serviceId, ReadWarnings(response));
        }

        public static IReadOnlyList<string> ParseUpdateWarnings(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return Array.Empty<string>();
            return ReadWarnings(JObject.Parse(responseBody));
        }

        private static IReadOnlyList<string> ReadWarnings(JObject response)
        {
            if (response["Warnings"] is not JArray warnings)
                return Array.Empty<string>();

            return warnings.Values<string>()
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Select(warning => warning!.Trim())
                .ToArray();
        }
    }
}
