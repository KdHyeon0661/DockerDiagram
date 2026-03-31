using System.Collections.Generic;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// compose 파일 내 개별 컨테이너(서비스)의 설정 정보를 담는 모델입니다.
    /// </summary>
    public class ComposeService
    {
        public string? ContainerName { get; set; } // 명시적 컨테이너 이름 (container_name)
        public string? Image { get; set; } // 사용할 도커 이미지명 (image)
        public string? Restart { get; set; } // 재시작 정책 (restart - 예: always, unless-stopped)
        public List<string>? Ports { get; set; } // 호스트와 연결할 포트 매핑 목록 (ports)
        public List<string>? Environment { get; set; } // 환경 변수 목록 (environment)
        public List<string>? Volumes { get; set; } // 볼륨 마운트 목록 (volumes)
        public List<string>? DependsOn { get; set; } // 실행 의존성 목록 (depends_on)

        // 정적 IP가 있으면 딕셔너리, 없으면 단순 문자열 리스트로 처리하기 위해 object 타입 사용 (networks)
        public object? Networks { get; set; }
    }

    /// <summary>
    /// 특정 서비스가 네트워크에 연결될 때 부여받는 세부 설정(예: 정적 IP)을 담는 모델입니다.
    /// </summary>
    public class ComposeServiceNetwork
    {
        public string? Ipv4Address { get; set; } // 컨테이너에 할당할 고정 IPv4 주소 (ipv4_address)
    }

    /// <summary>
    /// compose 파일의 최상위 네트워크 정의(설정)를 담는 모델입니다.
    /// </summary>
    public class ComposeNetwork
    {
        public string? Driver { get; set; } // 네트워크 드라이버 종류 (driver - 예: bridge, overlay)
        public ComposeIpam? Ipam { get; set; } // IP 할당 관리(IPAM) 블록 설정
    }

    /// <summary>
    /// 네트워크의 IP 할당 방식 및 범위를 정의하는 IPAM(IP Address Management) 모델입니다.
    /// </summary>
    public class ComposeIpam
    {
        public List<ComposeIpamConfig>? Config { get; set; } // 세부 서브넷 설정 목록 (config)
    }

    /// <summary>
    /// IPAM의 세부 설정(서브넷 등)을 담는 모델입니다.
    /// </summary>
    public class ComposeIpamConfig
    {
        public string? Subnet { get; set; } // 네트워크의 서브넷 대역 (subnet - 예: 172.20.0.0/16)
    }

    /// <summary>
    /// docker-compose.yml 파일의 전체 구조를 C# 객체로 매핑하기 위한 최상위 데이터 모델입니다.
    /// </summary>
    public class ComposeFileModel
    {
        public Dictionary<string, ComposeService> Services { get; set; } = new(); // compose 파일의 'services' 블록
        public Dictionary<string, ComposeNetwork>? Networks { get; set; } // compose 파일의 'networks' 블록
        public Dictionary<string, object>? Volumes { get; set; } // compose 파일의 'volumes' 블록
    }
}