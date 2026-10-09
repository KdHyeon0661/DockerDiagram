using DockerDiagram.Models;

namespace DockerDiagram.Diagram
{
    /// <summary>
    /// Manager Docker Engine에서 조회한 일반 리소스 중 Swarm 화면에 표시할 항목을 고릅니다.
    /// Overlay driver 또는 swarm scope인 네트워크만 클러스터 네트워크로 취급합니다.
    /// </summary>
    public static class SwarmResourceFilter
    {
        public static bool IsOverlayNetwork(DockerNetworkGroup network)
        {
            ArgumentNullException.ThrowIfNull(network);

            // ingress belongs to Swarm's routing mesh. Users do not create, attach,
            // or delete it as an application overlay network.
            if (network.Name.Equals("ingress", StringComparison.OrdinalIgnoreCase))
                return false;

            return network.Driver.Equals("overlay", StringComparison.OrdinalIgnoreCase) ||
                   network.Scope.Equals("swarm", StringComparison.OrdinalIgnoreCase);
        }
    }
}
