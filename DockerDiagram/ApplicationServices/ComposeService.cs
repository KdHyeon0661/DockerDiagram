using System.Collections.Generic;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// compose 파일 내 개별 컨테이너(서비스)의 설정 정보를 담는 모델입니다.
    /// </summary>
    public class ComposeService
    {
        public string? ContainerName { get; set; } // 명시적 컨테이너 이름 (container_name)
        public string? Image { get; set; } // 사용할 도커 이미지명 (image)
        public object? Build { get; set; } // build 문자열 또는 context/dockerfile 등이 포함된 맵
        public string? Restart { get; set; } // 재시작 정책 (restart - 예: always, unless-stopped)
        public object? Ports { get; set; } // 문자열 리스트 또는 long syntax 맵 목록
        public object? Environment { get; set; } // 리스트 또는 key/value 맵
        public object? Volumes { get; set; } // 문자열 리스트 또는 long syntax 맵 목록
        public object? DependsOn { get; set; } // 리스트 또는 condition 맵

        // 정적 IP가 있으면 딕셔너리, 없으면 단순 문자열 리스트로 처리하기 위해 object 타입 사용 (networks)
        public object? Networks { get; set; }

        public object? EnvFile { get; set; }
        public object? Command { get; set; }
        public object? Entrypoint { get; set; }
        public object? Healthcheck { get; set; }
        public object? Labels { get; set; }
        public object? Expose { get; set; }
        public object? ExtraHosts { get; set; }
        public object? Dns { get; set; }
        public string? Hostname { get; set; }
        public string? User { get; set; }
        public string? WorkingDir { get; set; }
        public bool? Privileged { get; set; }
        public object? CapAdd { get; set; }
        public object? CapDrop { get; set; }
        public object? Secrets { get; set; }
        public object? Configs { get; set; }
        public object? Profiles { get; set; }
        public object? Deploy { get; set; }
        public object? Logging { get; set; }
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
        public string? Name { get; set; }
        public Dictionary<string, ComposeService> Services { get; set; } = new(); // compose 파일의 'services' 블록
        public Dictionary<string, ComposeNetwork>? Networks { get; set; } // compose 파일의 'networks' 블록
        public Dictionary<string, object>? Volumes { get; set; } // compose 파일의 'volumes' 블록
        public Dictionary<string, object>? Secrets { get; set; }
        public Dictionary<string, object>? Configs { get; set; }
    }
}
