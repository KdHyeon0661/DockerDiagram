namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// Swarm 초기화 화면의 주소 기본값을 Docker Engine 실행 환경에 맞게 결정합니다.
    /// Docker Desktop의 Linux Engine은 Windows 호스트 어댑터 주소를 소유하지 않으므로
    /// VM 내부 인터페이스를 사용한 한 노드 데모 설정을 별도로 제공합니다.
    /// </summary>
    public sealed record SwarmInitializationDefaults(
        IReadOnlyList<string> AdvertiseCandidates,
        string AdvertiseAddress,
        string ListenAddress,
        bool IsDockerDesktopLocalDemo)
    {
        public static SwarmInitializationDefaults Create(
            bool suggestLocalAddresses,
            string? osType,
            string? operatingSystem,
            IReadOnlyList<string>? hostCandidates = null,
            IReadOnlyList<string>? daemonCandidates = null)
        {
            if (!suggestLocalAddresses)
            {
                return new SwarmInitializationDefaults(
                    Array.Empty<string>(),
                    string.Empty,
                    "0.0.0.0:2377",
                    IsDockerDesktopLocalDemo: false);
            }

            bool dockerDesktopLinux =
                string.Equals(osType?.Trim(), "linux", StringComparison.OrdinalIgnoreCase) &&
                operatingSystem?.Contains("Docker Desktop", StringComparison.OrdinalIgnoreCase) == true;

            if (dockerDesktopLinux)
            {
                string[] daemonOwnedCandidates = (daemonCandidates ?? Array.Empty<string>())
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Select(candidate => candidate.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string advertiseAddress = daemonOwnedCandidates.Length == 0
                    ? string.Empty
                    : $"{daemonOwnedCandidates[0]}:2377";
                return new SwarmInitializationDefaults(
                    daemonOwnedCandidates.Select(candidate => $"{candidate}:2377").ToArray(),
                    advertiseAddress,
                    "0.0.0.0:2377",
                    IsDockerDesktopLocalDemo: true);
            }

            IReadOnlyList<string> candidates = hostCandidates ?? SwarmAdvertiseAddressDiscovery.GetIpv4Candidates();
            return new SwarmInitializationDefaults(
                candidates,
                candidates.FirstOrDefault() ?? string.Empty,
                "0.0.0.0:2377",
                IsDockerDesktopLocalDemo: false);
        }
    }
}
