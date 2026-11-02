using System;

namespace DockerDiagram.Common
{
    /// <summary>
    /// Docker가 직접 관리하므로 일반 리소스 삭제 흐름에서 제거하면 안 되는 네트워크를 판별합니다.
    /// </summary>
    public static class DockerNetworkSafety
    {
        public const string Bridge = "bridge";
        public const string Host = "host";
        public const string None = "none";
        public const string Ingress = "ingress";
        public const string GatewayBridge = "docker_gwbridge";

        public static bool IsBuiltInContainerNetwork(string? name) =>
            EqualsName(name, Bridge) || EqualsName(name, Host) || EqualsName(name, None);

        public static bool IsSwarmManaged(string? name, bool isIngress = false) =>
            isIngress || EqualsName(name, Ingress) || EqualsName(name, GatewayBridge);

        public static bool IsIngress(string? name) => EqualsName(name, Ingress);

        public static bool DisallowsEndpointOptions(string? driver) =>
            EqualsName(driver, Host) || EqualsName(driver, None);

        public static bool ShouldCreateDiagramGroup(string? name) =>
            !string.IsNullOrWhiteSpace(name) && !IsBuiltInContainerNetwork(name) && !IsSwarmManaged(name);

        public static bool IsUserSelectable(string? name) =>
            !string.IsNullOrWhiteSpace(name) &&
            !EqualsName(name, None) &&
            !EqualsName(name, Host) &&
            !IsSwarmManaged(name);

        public static bool IsProtected(string? name, bool isIngress = false)
        {
            return IsBuiltInContainerNetwork(name) || IsSwarmManaged(name, isIngress);
        }

        private static bool EqualsName(string? left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
