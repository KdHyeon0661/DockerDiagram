using System;

namespace DockerDiagram.Models
{
    // 접속 방식을 정의하는 열거형 (나중에 K8s 등 추가 가능)
    public enum EndpointType
    {
        Local,          // 내 PC의 도커
        SshRemote,      // 원격 서버의 도커 (SSH 터널링)
        Kubernetes      // (미래 확장용) K8s API 서버
    }

    public class ConnectionProfile
    {
        public string ProfileId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Local Docker";
        public EndpointType Type { get; set; } = EndpointType.Local;

        // =====================================
        // [SSH 원격 접속 전용 데이터]
        // =====================================
        public string? HostIp { get; set; }
        public string? SshUsername { get; set; }
        public int SshPort { get; set; } = 22;

        public int LocalTunnelPort { get; set; }
        public string? SshKeyFilePath { get; set; }
    }
}