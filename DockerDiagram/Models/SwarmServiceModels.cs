using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DockerDiagram.Models
{
    public enum SwarmServiceModeKind { Replicated, Global }
    public enum SwarmPortProtocol { Tcp, Udp, Sctp }
    public enum SwarmPublishMode { Ingress, Host }
    public enum SwarmMountKind { Volume, Bind, Tmpfs }
    public enum SwarmRestartCondition { None, OnFailure, Any }
    public enum SwarmUpdateFailureAction { Pause, Continue, Rollback }
    public enum SwarmUpdateOrder { StopFirst, StartFirst }

    public sealed record SwarmPublishedPortOptions(
        uint TargetPort,
        uint? PublishedPort = null,
        SwarmPortProtocol Protocol = SwarmPortProtocol.Tcp,
        SwarmPublishMode PublishMode = SwarmPublishMode.Ingress)
    {
        public void Validate()
        {
            if (TargetPort is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(TargetPort), "Target port는 1~65535 범위여야 합니다.");
            if (PublishedPort is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(PublishedPort), "Published port는 1~65535 범위여야 합니다.");
        }
    }

    public sealed record SwarmMountOptions(
        SwarmMountKind Kind,
        string Source,
        string Target,
        bool ReadOnly = false,
        string? VolumeDriver = null,
        IReadOnlyDictionary<string, string>? VolumeDriverOptions = null,
        IReadOnlyDictionary<string, string>? VolumeLabels = null)
    {
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Target))
                throw new ArgumentException("Mount target이 비어 있습니다.", nameof(Target));
            if (Kind == SwarmMountKind.Bind && string.IsNullOrWhiteSpace(Source))
                throw new ArgumentException("Bind mount source가 비어 있습니다.", nameof(Source));
            if (VolumeDriverOptions?.Any(option => string.IsNullOrWhiteSpace(option.Key)) == true)
                throw new ArgumentException("Volume driver option key가 비어 있습니다.", nameof(VolumeDriverOptions));
            if (VolumeLabels?.Any(label => string.IsNullOrWhiteSpace(label.Key)) == true)
                throw new ArgumentException("Volume label key가 비어 있습니다.", nameof(VolumeLabels));
        }
    }

    public sealed record SwarmNetworkAttachmentOptions(
        string Target,
        IReadOnlyList<string>? Aliases = null,
        IReadOnlyDictionary<string, string>? DriverOptions = null)
    {
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Target))
                throw new ArgumentException("Network ID 또는 이름이 비어 있습니다.", nameof(Target));
        }
    }

    public sealed class SwarmRestartPolicyOptions
    {
        public SwarmRestartCondition Condition { get; init; } = SwarmRestartCondition.Any;
        public TimeSpan? Delay { get; init; }
        public ulong? MaxAttempts { get; init; }
        public TimeSpan? Window { get; init; }

        public void Validate()
        {
            ValidateNonNegative(Delay, nameof(Delay));
            ValidateNonNegative(Window, nameof(Window));
        }

        private static void ValidateNonNegative(TimeSpan? value, string parameterName)
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(parameterName, "시간 값은 음수일 수 없습니다.");
        }
    }

    public sealed class SwarmUpdatePolicyOptions
    {
        public ulong Parallelism { get; init; } = 1;
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;
        public TimeSpan? Monitor { get; init; }
        public double? MaxFailureRatio { get; init; }
        public SwarmUpdateFailureAction FailureAction { get; init; } = SwarmUpdateFailureAction.Pause;
        public SwarmUpdateOrder Order { get; init; } = SwarmUpdateOrder.StopFirst;

        public void Validate()
        {
            if (Delay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(Delay), "Update delay는 음수일 수 없습니다.");
            if (Monitor < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(Monitor), "Monitor 시간은 음수일 수 없습니다.");
            if (MaxFailureRatio is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(MaxFailureRatio), "실패 비율은 0~1 범위여야 합니다.");
        }
    }

    /// <summary>Docker.DotNet 형식과 UI를 분리한 Swarm service 애플리케이션 계약입니다.</summary>
    public sealed class SwarmServiceSpecOptions
    {
        private static readonly Regex ValidNamePattern = new(
            "^[A-Za-z0-9][A-Za-z0-9_.-]*$",
            RegexOptions.CultureInvariant);

        public string Name { get; init; } = string.Empty;
        public string Image { get; init; } = string.Empty;
        public SwarmServiceModeKind Mode { get; init; } = SwarmServiceModeKind.Replicated;
        public ulong? Replicas { get; init; } = 1;
        public IReadOnlyList<string> Command { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> EnvironmentVariables { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<SwarmPublishedPortOptions> PublishedPorts { get; init; } = Array.Empty<SwarmPublishedPortOptions>();
        public IReadOnlyList<SwarmMountOptions> Mounts { get; init; } = Array.Empty<SwarmMountOptions>();
        public IReadOnlyList<SwarmNetworkAttachmentOptions> Networks { get; init; } = Array.Empty<SwarmNetworkAttachmentOptions>();
        public SwarmRestartPolicyOptions RestartPolicy { get; init; } = new();
        public SwarmUpdatePolicyOptions UpdatePolicy { get; init; } = new();
        public SwarmUpdatePolicyOptions RollbackPolicy { get; init; } = new();

        public void Validate()
        {
            string normalizedName = (Name ?? string.Empty).Trim();
            if (normalizedName.Length == 0)
                throw new ArgumentException("Swarm service 이름이 비어 있습니다.", nameof(Name));
            if (normalizedName.Length > 255 || !ValidNamePattern.IsMatch(normalizedName))
                throw new ArgumentException("Service 이름은 영문자 또는 숫자로 시작하고 영문자, 숫자, 점, 밑줄, 하이픈만 포함해야 합니다.", nameof(Name));
            if (string.IsNullOrWhiteSpace(Image))
                throw new ArgumentException("Swarm service 이미지가 비어 있습니다.", nameof(Image));
            if (Mode == SwarmServiceModeKind.Replicated && Replicas is null)
                throw new ArgumentException("Replicated service에는 replica 수가 필요합니다.", nameof(Replicas));
            if (Mode == SwarmServiceModeKind.Global && Replicas is not null)
                throw new ArgumentException("Global service에는 replica 수를 지정할 수 없습니다.", nameof(Replicas));

            ValidateNonEmptyValues(Command, nameof(Command));
            ValidateNonEmptyValues(Arguments, nameof(Arguments));
            ValidateNonEmptyValues(EnvironmentVariables, nameof(EnvironmentVariables));
            if (Labels.Any(label => string.IsNullOrWhiteSpace(label.Key)))
                throw new ArgumentException("Label key가 비어 있습니다.", nameof(Labels));

            foreach (SwarmPublishedPortOptions port in PublishedPorts) port.Validate();
            foreach (SwarmMountOptions mount in Mounts) mount.Validate();
            foreach (SwarmNetworkAttachmentOptions network in Networks) network.Validate();

            if (PublishedPorts.Where(port => port.PublishedPort.HasValue)
                .GroupBy(port => new { port.PublishedPort, port.Protocol, port.PublishMode })
                .Any(group => group.Count() > 1))
                throw new ArgumentException("같은 publish mode와 protocol에 중복된 published port가 있습니다.", nameof(PublishedPorts));
            if (Mounts.GroupBy(mount => mount.Target.Trim(), StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new ArgumentException("중복된 mount target이 있습니다.", nameof(Mounts));
            if (Networks.GroupBy(network => network.Target.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new ArgumentException("중복된 network 연결이 있습니다.", nameof(Networks));

            RestartPolicy.Validate();
            UpdatePolicy.Validate();
            RollbackPolicy.Validate();
        }

        private static void ValidateNonEmptyValues(IEnumerable<string> values, string parameterName)
        {
            if (values.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("빈 항목을 포함할 수 없습니다.", parameterName);
        }
    }

    public sealed class SwarmServiceCreateOptions
    {
        public SwarmServiceSpecOptions Spec { get; init; } = new();
        public IReadOnlyList<string> PlacementConstraints { get; init; } = Array.Empty<string>();
        public IReadOnlyList<SwarmServiceResourceReferenceOptions> Secrets { get; init; } = Array.Empty<SwarmServiceResourceReferenceOptions>();
        public IReadOnlyList<SwarmServiceResourceReferenceOptions> Configs { get; init; } = Array.Empty<SwarmServiceResourceReferenceOptions>();
        public void Validate()
        {
            ArgumentNullException.ThrowIfNull(Spec);
            Spec.Validate();
            if (PlacementConstraints.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Placement constraint에 빈 항목이 있습니다.", nameof(PlacementConstraints));
            foreach (SwarmServiceResourceReferenceOptions reference in Secrets) reference.Validate();
            foreach (SwarmServiceResourceReferenceOptions reference in Configs) reference.Validate();
            if (Secrets.GroupBy(reference => reference.FileName, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new ArgumentException("중복된 Secret target 이름이 있습니다.", nameof(Secrets));
            if (Configs.GroupBy(reference => reference.FileName, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new ArgumentException("중복된 Config target 이름이 있습니다.", nameof(Configs));
        }
    }

    public sealed class SwarmServiceUpdateOptions
    {
        public string ServiceId { get; init; } = string.Empty;
        public ulong? Version { get; init; }
        public SwarmServiceSpecOptions Spec { get; init; } = new();

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ServiceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(ServiceId));
            if (Version is null)
                throw new ArgumentException("Swarm service version이 필요합니다.", nameof(Version));
            ArgumentNullException.ThrowIfNull(Spec);
            Spec.Validate();
        }
    }

    public sealed record SwarmServiceMutationResult(string ServiceId, ulong Version, IReadOnlyList<string> Warnings)
    {
        public static SwarmServiceMutationResult Create(string serviceId, ulong version = 0, IReadOnlyList<string>? warnings = null)
        {
            if (string.IsNullOrWhiteSpace(serviceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(serviceId));
            return new(serviceId.Trim(), version, warnings ?? Array.Empty<string>());
        }
    }

    public enum SwarmServiceLifecycleState { Draft, Creating, Bound, Failed, Offline }

    /// <summary>기존 저장 enum 값을 바꾸지 않고 노드 상태를 Swarm 수명주기로 투영합니다.</summary>
    public static class SwarmServiceLifecycle
    {
        public static SwarmServiceLifecycleState Resolve(
            RuntimeBindingState bindingState,
            bool isCreating,
            bool isCreationFailed,
            bool isRuntimeAvailable,
            bool isBoundToEngine)
        {
            if (isCreationFailed || bindingState == RuntimeBindingState.Error) return SwarmServiceLifecycleState.Failed;
            if (isCreating || bindingState == RuntimeBindingState.Applying) return SwarmServiceLifecycleState.Creating;
            if (bindingState == RuntimeBindingState.Draft) return SwarmServiceLifecycleState.Draft;
            if (!isRuntimeAvailable || !isBoundToEngine || bindingState == RuntimeBindingState.Missing)
                return SwarmServiceLifecycleState.Offline;
            return SwarmServiceLifecycleState.Bound;
        }

        public static bool CanTransition(SwarmServiceLifecycleState current, SwarmServiceLifecycleState next) =>
            current == next || (current, next) switch
            {
                (SwarmServiceLifecycleState.Draft, SwarmServiceLifecycleState.Creating) => true,
                (SwarmServiceLifecycleState.Creating, SwarmServiceLifecycleState.Bound) => true,
                (SwarmServiceLifecycleState.Creating, SwarmServiceLifecycleState.Failed) => true,
                (SwarmServiceLifecycleState.Creating, SwarmServiceLifecycleState.Draft) => true,
                (SwarmServiceLifecycleState.Failed, SwarmServiceLifecycleState.Creating) => true,
                (SwarmServiceLifecycleState.Failed, SwarmServiceLifecycleState.Draft) => true,
                (SwarmServiceLifecycleState.Bound, SwarmServiceLifecycleState.Creating) => true,
                (SwarmServiceLifecycleState.Bound, SwarmServiceLifecycleState.Offline) => true,
                (SwarmServiceLifecycleState.Offline, SwarmServiceLifecycleState.Bound) => true,
                (SwarmServiceLifecycleState.Offline, SwarmServiceLifecycleState.Creating) => true,
                _ => false
            };
    }
}
