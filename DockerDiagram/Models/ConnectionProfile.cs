using System;

namespace DockerDiagram.Models
{
    /// <summary>
    /// 도커 엔진에 연결하기 위한 접속 정보(신분증)를 담는 데이터 모델입니다.
    /// 로컬 PC 접속뿐만 아니라 SSH 원격 접속에 필요한 네트워크 정보를 모두 포함합니다.
    /// </summary>
    public class ConnectionProfile
    {
        public string ProfileId { get; set; } = Guid.NewGuid().ToString(); // 프로필 고유 식별자
        public string Name { get; set; } = "Local Docker"; // UI 탭 등에 표시될 연결 이름
        public EndpointType Type { get; set; } = EndpointType.Local; // 접속 방식 (Local, SshRemote 등)

        // =====================================
        // [SSH 원격 접속 전용 데이터]
        // =====================================
        public string? HostIp { get; set; } // 원격 서버 IP 주소
        public string? SshUsername { get; set; } // 원격 서버 SSH 로그인 계정명 (예: ubuntu, root)
        public int SshPort { get; set; } = 22; // SSH 접속 포트 (기본값 22)

        public int LocalTunnelPort { get; set; } // 백그라운드에서 뚫어놓은 내 PC의 비밀 통로 포트 (예: 23750)
        public string? SshKeyFilePath { get; set; } // 자동 재접속을 위한 SSH 프라이빗 키(.pem) 파일 경로
    }
}