using System;

namespace DockerDiagram.Models
{
    /// <summary>
    /// Docker Engine 연결에 필요한 프로필 정보를 담습니다.
    /// 로컬 PC 접속뿐만 아니라 SSH 원격 접속에 필요한 네트워크 정보를 모두 포함합니다.
    /// </summary>
    public class ConnectionProfile
    {
        public string ProfileId { get; set; } = Guid.NewGuid().ToString(); // 프로필 고유 식별자
        public string Name { get; set; } = "Local Docker"; // UI 탭 등에 표시될 연결 이름
        public EndpointType Type { get; set; } = EndpointType.Local; // 접속 방식 (Local, SshRemote 등)
        public RuntimeKind RuntimeKind { get; set; } = RuntimeKind.DockerEngine; // 이 연결에서 보는 리소스 런타임

        // =====================================
        // [SSH 원격 접속 전용 데이터]
        // =====================================
        public string? HostIp { get; set; } // 원격 서버 IP 주소
        public string? SshUsername { get; set; } // 원격 서버 SSH 로그인 계정명 (예: ubuntu, root)
        public int SshPort { get; set; } = 22; // SSH 접속 포트 (기본값 22)

        public int LocalTunnelPort { get; set; } // SSH 터널이 로컬에서 수신하는 포트
        public string? SshKeyFilePath { get; set; } // 자동 재접속을 위한 SSH 프라이빗 키(.pem) 파일 경로

        // =====================================
        // [Docker CLI context 직접 연결 전용 데이터]
        // =====================================
        public string? DockerEndpoint { get; set; } // npipe://, unix://, tcp:// 등 Docker context의 endpoint
    }
}
