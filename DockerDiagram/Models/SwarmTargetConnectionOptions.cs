using System;

namespace DockerDiagram.Models
{
    /// <summary>
    /// 아직 Swarm에 가입하지 않은 원격 Docker Engine을 검증할 때만 사용하는 입력입니다.
    /// 저장 모델이 아니며 SSH 키 경로는 문자열 표현에 포함하지 않습니다.
    /// </summary>
    public sealed class SwarmTargetConnectionOptions
    {
        public string Host { get; init; } = string.Empty;
        public int SshPort { get; init; } = 22;
        public string Username { get; init; } = string.Empty;
        public string SshKeyFilePath { get; init; } = string.Empty;
        public string RemoteDockerSocketPath { get; init; } = "/var/run/docker.sock";
        public SwarmJoinRole Role { get; init; } = SwarmJoinRole.Worker;
        public string AdvertiseAddress { get; init; } = string.Empty;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Host))
                throw new ArgumentException("대상 PC 주소가 비어 있습니다.", nameof(Host));
            if (SshPort is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(SshPort), "SSH 포트는 1~65535 범위여야 합니다.");
            if (string.IsNullOrWhiteSpace(Username))
                throw new ArgumentException("SSH 사용자 이름이 비어 있습니다.", nameof(Username));
            if (string.IsNullOrWhiteSpace(SshKeyFilePath))
                throw new ArgumentException("SSH 키 파일을 선택해 주세요.", nameof(SshKeyFilePath));
            if (string.IsNullOrWhiteSpace(RemoteDockerSocketPath))
                throw new ArgumentException("원격 Docker 소켓 경로가 비어 있습니다.", nameof(RemoteDockerSocketPath));
            if (string.IsNullOrWhiteSpace(AdvertiseAddress))
                throw new ArgumentException("대상 노드의 Advertise Address가 비어 있습니다.", nameof(AdvertiseAddress));
        }

        public override string ToString() =>
            $"{Role} target · {Host}:{SshPort} · advertise {AdvertiseAddress}";
    }
}
