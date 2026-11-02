using DockerDiagram.Common;
using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// Swarm routing mesh가 필요한 작업과 Docker 관리 ingress 네트워크를 판별합니다.
    /// </summary>
    public static class SwarmIngressNetworkSupport
    {
        public static bool RequiresIngress(SwarmServiceSpecOptions spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            return spec.PublishedPorts.Any(port => port.PublishMode == SwarmPublishMode.Ingress);
        }

        public static bool IsIngressNetwork(DockerNetworkGroup network)
        {
            ArgumentNullException.ThrowIfNull(network);
            return network.Ingress ||
                   (DockerNetworkSafety.IsIngress(network.Name) &&
                    network.Driver.Equals("overlay", StringComparison.OrdinalIgnoreCase) &&
                    network.Scope.Equals("swarm", StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasIngressNetwork(IEnumerable<DockerNetworkGroup> networks)
        {
            ArgumentNullException.ThrowIfNull(networks);
            return networks.Any(IsIngressNetwork);
        }
    }
}
