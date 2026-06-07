namespace DockerDiagram.Models
{
    /// <summary>
    /// 도화지(시트) 위에 배치되는 개별 노드(아이템)의 종류를 정의합니다.
    /// </summary>
    public enum NodeType
    {
        Container,  // 실행 중인 도커 컨테이너
        Volume,     // 데이터를 저장하는 도커 볼륨 (보통 원통형 아이콘으로 표현)
        Internet    // 외부 네트워크와의 연결을 나타내는 가상 노드
    }

    /// <summary>
    /// 여러 노드를 감싸는 그룹(박스)의 논리적, 시각적 종류를 정의합니다.
    /// </summary>
    public enum GroupType
    {
        General,    // 단순히 시각적으로 묶어두기 위한 일반 폴더/그룹
        Network     // 동일한 도커 네트워크(브릿지 등)에 속해 있음을 나타내는 네트워크 그룹
    }

    /// <summary>
    /// 다이어그램에서 노드와 노드를 연결하는 선(Connector)의 관계 종류를 정의합니다.
    /// </summary>
    public enum RelationType
    {
        Dependency,     // 컨테이너 간의 실행 순서/의존성 (Container <-> Container)
        VolumeMount,    // 컨테이너에 데이터를 저장하기 위한 마운트 (Container <-> Volume)
        NetworkAttach   // 컨테이너가 외부 통신을 하기 위한 연결 (Container <-> Internet)
    }

    /// <summary>
    /// 프로그램이 도커 엔진에 접속하는 방식(엔드포인트)을 정의합니다.
    /// </summary>
    public enum EndpointType
    {
        Local,          // 내 PC의 로컬 도커 프로세스
        SshRemote,      // SSH 터널링을 통해 연결된 원격 서버의 도커
        DockerContext,  // Docker CLI context의 Docker endpoint로 직접 연결
        Kubernetes      // (미래 확장용) 쿠버네티스 API 서버 연결
    }
}
