using System.Net;
using System.Net.Sockets;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// 사용자가 복사할 docker swarm join 명령을 안전한 단일 형식으로 만듭니다.
    /// 셸 메타문자가 포함된 주소나 Token은 명령으로 만들지 않습니다.
    /// </summary>
    public static class SwarmJoinCommandBuilder
    {
        public const int DefaultManagerPort = 2377;

        public static string Build(string joinToken, string managerAddress)
        {
            string token = NormalizeToken(joinToken);
            string address = NormalizeManagerAddress(managerAddress);
            return $"docker swarm join --token {token} {address}";
        }

        public static string BuildRedacted(string managerAddress) =>
            $"docker swarm join --token <hidden> {NormalizeManagerAddress(managerAddress)}";

        public static string NormalizeManagerAddress(string managerAddress)
        {
            string value = (managerAddress ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Swarm Manager 주소가 비어 있습니다.", nameof(managerAddress));
            if (value.Any(character => !IsSafeAddressCharacter(character)))
                throw new ArgumentException("Manager 주소에 사용할 수 없는 문자가 포함되어 있습니다.", nameof(managerAddress));

            if (IPAddress.TryParse(value, out IPAddress? plainIp))
            {
                return plainIp.AddressFamily == AddressFamily.InterNetworkV6
                    ? $"[{plainIp}]:{DefaultManagerPort}"
                    : $"{plainIp}:{DefaultManagerPort}";
            }

            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closingBracket = value.IndexOf(']');
                if (closingBracket <= 1)
                    throw new ArgumentException("IPv6 Manager 주소 형식이 올바르지 않습니다.", nameof(managerAddress));

                string host = value[1..closingBracket];
                if (!IPAddress.TryParse(host, out IPAddress? ipv6) ||
                    ipv6.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    throw new ArgumentException("IPv6 Manager 주소 형식이 올바르지 않습니다.", nameof(managerAddress));
                }

                string remainder = value[(closingBracket + 1)..];
                int port = string.IsNullOrEmpty(remainder)
                    ? DefaultManagerPort
                    : ParsePort(remainder.StartsWith(":", StringComparison.Ordinal) ? remainder[1..] : string.Empty);
                return $"[{ipv6}]:{port}";
            }

            int colonIndex = value.LastIndexOf(':');
            if (colonIndex > 0)
            {
                string host = value[..colonIndex];
                string portText = value[(colonIndex + 1)..];
                ValidateHost(host, nameof(managerAddress));
                return $"{host}:{ParsePort(portText)}";
            }

            ValidateHost(value, nameof(managerAddress));
            return $"{value}:{DefaultManagerPort}";
        }

        private static string NormalizeToken(string joinToken)
        {
            string value = (joinToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Swarm Join Token이 비어 있습니다.", nameof(joinToken));
            if (value.Any(character =>
                    !char.IsLetterOrDigit(character) &&
                    character is not '-' and not '_' and not '.'))
            {
                throw new ArgumentException("Swarm Join Token 형식이 올바르지 않습니다.", nameof(joinToken));
            }
            return value;
        }

        private static void ValidateHost(string host, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(host) ||
                host.Any(character =>
                    !char.IsLetterOrDigit(character) &&
                    character is not '.' and not '-' and not '_'))
            {
                throw new ArgumentException("Swarm Manager 호스트 이름이 올바르지 않습니다.", parameterName);
            }
        }

        private static int ParsePort(string portText)
        {
            if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
                throw new ArgumentException("Swarm Manager 포트는 1~65535 범위여야 합니다.", nameof(portText));
            return port;
        }

        private static bool IsSafeAddressCharacter(char character) =>
            char.IsLetterOrDigit(character) ||
            character is '.' or '-' or '_' or ':' or '[' or ']';
    }
}
