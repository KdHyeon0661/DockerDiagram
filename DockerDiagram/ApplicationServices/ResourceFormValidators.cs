using DockerDiagram.Common;
using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public sealed record FormParseResult<T>(T? Value, string Message = "") where T : class
    {
        public bool IsValid => Value is not null && string.IsNullOrEmpty(Message);
    }

    internal static class FormParseResult
    {
        public static FormParseResult<T> Success<T>(T value) where T : class => new(value);
        public static FormParseResult<T> Failure<T>(string message) where T : class => new(null, message);
    }

    public static class ContainerCreateFormValidator
    {
        public static FormParseResult<IReadOnlyList<string>> ParseEnvironmentVariables(
            string? text,
            Func<string, bool> keyValidator)
        {
            ArgumentNullException.ThrowIfNull(keyValidator);
            if (!KeyValueTextParser.TryParse(
                    text,
                    out _,
                    out KeyValueTextError? error,
                    ignoreCommentLines: true,
                    keyValidator: keyValidator))
            {
                return FormParseResult.Failure<IReadOnlyList<string>>(
                    $"환경변수는 KEY=VALUE 형식이어야 합니다.\n입력값: {error!.Entry}");
            }

            IReadOnlyList<string> entries = KeyValueTextParser.ReadEntries(text)
                .Where(entry => !entry.StartsWith('#'))
                .ToArray();
            return FormParseResult.Success(entries);
        }
    }

    public sealed record NetworkCreateFormInput(
        string Name,
        string Driver,
        string Subnet,
        string Gateway,
        string IpRange,
        bool Internal,
        bool Attachable,
        bool EnableIPv6,
        bool External,
        string ComposeNetworkName,
        string LabelsText,
        string DriverOptionsText,
        string AuxAddressesText,
        string ParentInterface,
        string DriverMode);

    public static class NetworkCreateFormValidator
    {
        public static FormParseResult<NetworkCreateOptions> Parse(
            NetworkCreateFormInput input,
            bool isSwarmOverlay)
        {
            ArgumentNullException.ThrowIfNull(input);
            string name = input.Name.Trim();
            string driver = string.IsNullOrWhiteSpace(input.Driver) ? "bridge" : input.Driver.Trim();
            if (name.Length == 0)
                return FormParseResult.Failure<NetworkCreateOptions>("Network name is required.");
            if (isSwarmOverlay && DockerNetworkSafety.IsIngress(name))
            {
                return FormParseResult.Failure<NetworkCreateOptions>(
                    "'ingress' is reserved for Swarm routing mesh and is managed automatically.");
            }

            if (!TryParseKeyValue(input.AuxAddressesText, "Aux Addresses", out var auxAddresses, out string error) ||
                !TryParseKeyValue(input.LabelsText, "Labels", out var labels, out error) ||
                !TryParseKeyValue(input.DriverOptionsText, "Driver Options", out var driverOptions, out error))
            {
                return FormParseResult.Failure<NetworkCreateOptions>(error);
            }

            bool requiresParent = driver.Equals("macvlan", StringComparison.OrdinalIgnoreCase) ||
                                  driver.Equals("ipvlan", StringComparison.OrdinalIgnoreCase);
            string parent = input.ParentInterface.Trim();
            if (requiresParent && !input.External && parent.Length == 0)
            {
                return FormParseResult.Failure<NetworkCreateOptions>(
                    $"{driver} requires a parent interface, e.g. eth0.");
            }

            if (requiresParent && parent.Length > 0)
                driverOptions["parent"] = parent;
            string mode = input.DriverMode.Trim();
            if (mode.Length > 0 && driver.Equals("macvlan", StringComparison.OrdinalIgnoreCase))
                driverOptions["macvlan_mode"] = mode;
            else if (mode.Length > 0 && driver.Equals("ipvlan", StringComparison.OrdinalIgnoreCase))
                driverOptions["ipvlan_mode"] = mode;

            return FormParseResult.Success(new NetworkCreateOptions
            {
                Name = name,
                Driver = driver,
                Subnet = input.Subnet.Trim(),
                Gateway = input.Gateway.Trim(),
                IpRange = input.IpRange.Trim(),
                Internal = input.Internal,
                Attachable = input.Attachable,
                EnableIPv6 = input.EnableIPv6,
                External = input.External,
                ComposeNetworkName = input.ComposeNetworkName.Trim(),
                Labels = labels,
                DriverOptions = driverOptions,
                AuxAddresses = auxAddresses
            });
        }

        private static bool TryParseKeyValue(
            string? text,
            string fieldName,
            out Dictionary<string, string> values,
            out string message)
        {
            if (KeyValueTextParser.TryParse(text, out values, out KeyValueTextError? error))
            {
                message = string.Empty;
                return true;
            }

            message = $"{fieldName} must use key=value format.\n\nInvalid line: {error!.Entry}";
            return false;
        }
    }

    public sealed record VolumeCreateFormInput(
        string Name,
        string DockerVolumeName,
        string Driver,
        bool External,
        string LabelsText,
        string DriverOptionsText);

    public static class VolumeCreateFormValidator
    {
        public static FormParseResult<VolumeCreateOptions> Parse(VolumeCreateFormInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            string name = input.Name.Trim();
            if (name.Length == 0)
                return FormParseResult.Failure<VolumeCreateOptions>("볼륨 이름을 입력하세요.");

            if (!TryParseKeyValue(input.LabelsText, "Labels", out var labels, out string error) ||
                !TryParseKeyValue(input.DriverOptionsText, "Driver Options", out var driverOptions, out error))
            {
                return FormParseResult.Failure<VolumeCreateOptions>(error);
            }

            return FormParseResult.Success(new VolumeCreateOptions
            {
                Name = name,
                DockerVolumeName = input.DockerVolumeName.Trim(),
                Driver = string.IsNullOrWhiteSpace(input.Driver) ? "local" : input.Driver.Trim(),
                External = input.External,
                Labels = labels,
                DriverOptions = driverOptions
            });
        }

        private static bool TryParseKeyValue(
            string? text,
            string title,
            out Dictionary<string, string> values,
            out string message)
        {
            if (KeyValueTextParser.TryParse(text, out values, out KeyValueTextError? error))
            {
                message = string.Empty;
                return true;
            }

            message = $"{title} 항목은 key=value 형식이어야 합니다.\n문제 항목: {error!.Entry}";
            return false;
        }
    }
}
