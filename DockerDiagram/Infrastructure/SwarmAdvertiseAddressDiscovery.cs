using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Docker.DotNet.Models;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// 다른 Swarm 노드가 접근할 수 있는 로컬 IPv4 후보를 찾습니다.
    /// 자동 선택은 제안일 뿐이며 설정 창에서 사용자가 직접 수정할 수 있습니다.
    /// </summary>
    public static class SwarmAdvertiseAddressDiscovery
    {
        /// <summary>
        /// Docker daemon이 실제로 소유하는 기본 bridge 주소를 반환합니다.
        /// Docker Desktop에서는 Windows/WSL 호스트 주소와 dockerd의 네트워크
        /// 네임스페이스가 다르므로 로컬 단일 노드 Swarm에 이 주소를 사용합니다.
        /// </summary>
        public static IReadOnlyList<string> GetDaemonBridgeIpv4Candidates(NetworkResponse? bridgeNetwork)
        {
            return bridgeNetwork?.IPAM?.Config?
                .Select(config => config.Gateway)
                .Where(gateway =>
                    IPAddress.TryParse(gateway, out IPAddress? address) &&
                    IsUsableIpv4(address))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? Array.Empty<string>();
        }

        public static IReadOnlyList<string> GetIpv4Candidates()
        {
            var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                try
                {
                    foreach (UnicastIPAddressInformation unicast in network.GetIPProperties().UnicastAddresses)
                    {
                        if (IsUsableIpv4(unicast.Address))
                            addresses.Add(unicast.Address.ToString());
                    }
                }
                catch (NetworkInformationException)
                {
                    // 개별 어댑터 조회 실패는 다른 어댑터 후보 검색을 막지 않습니다.
                }
            }

            return addresses
                .OrderByDescending(address => IsPrivateIpv4(IPAddress.Parse(address)))
                .ThenBy(address => address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static bool IsUsableIpv4(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                return false;

            byte[] bytes = address.GetAddressBytes();
            return !address.Equals(IPAddress.Any) &&
                   !(bytes[0] == 169 && bytes[1] == 254);
        }

        private static bool IsPrivateIpv4(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }
    }
}
