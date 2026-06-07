using System;
using System.Collections.Generic;
using DockerDiagram.Helpers;

namespace DockerDiagram.Models
{
    /// <summary>
    /// 다이어그램 전체 데이터를 저장하고 불러오기 위한 최상위 파일 모델입니다.
    /// 파일(.json, .xml 등)의 루트(Root) 역할을 합니다.
    /// </summary>
    public class DiagramFile
    {
        public string Version { get; set; } = "1.33"; // 파일 구조의 버전 (하위 호환성 체크용)
        public DateTime SavedAt { get; set; } = DateTime.Now; // 파일이 마지막으로 저장된 시간
        public List<SheetData> Sheets { get; set; } = new List<SheetData>(); // 파일에 포함된 모든 시트(도화지) 목록
        public int ActiveSheetIndex { get; set; } = 0; // 파일을 다시 열었을 때 포커스를 맞출 시트의 인덱스
    }

    /// <summary>
    /// 개별 시트(도화지) 탭 하나의 모든 정보와 상태를 담는 데이터 모델입니다.
    /// </summary>
    public class SheetData
    {
        public string Title { get; set; } = "Sheet"; // 시트의 이름
        public ConnectionProfile Profile { get; set; } = new ConnectionProfile(); // 해당 시트가 사용하던 도커 접속 정보

        // --- 화면 뷰(줌/팬) 상태 ---
        public double MapWidth { get; set; } // 도화지 전체 가로 길이
        public double MapHeight { get; set; } // 도화지 전체 세로 길이
        public double OffsetX { get; set; } // 화면 가로 스크롤 위치
        public double OffsetY { get; set; } // 화면 세로 스크롤 위치
        public double Scale { get; set; } // 화면 확대/축소 비율
        public string ComposeRawYaml { get; set; } = string.Empty; // Compose import 원본 YAML

        // --- 다이어그램 구성 요소 데이터 ---
        public List<NodeData> Nodes { get; set; } = new List<NodeData>(); // 시트 위에 배치된 모든 노드 목록
        public List<ConnectionData> Connections { get; set; } = new List<ConnectionData>(); // 노드들을 잇는 선 목록
        public List<GroupData> Groups { get; set; } = new List<GroupData>(); // 노드들을 묶는 그룹 목록
    }

    /// <summary>
    /// 개별 노드(컨테이너, 볼륨, 네트워크 등)의 위치, 크기, 도커 설정 정보를 담는 데이터 모델입니다.
    /// </summary>
    public class NodeData
    {
        public string Id { get; set; } = string.Empty; // 다이어그램 내부에서 사용하는 고유 ID
        public string DockerId { get; set; } = string.Empty; // 실제 도커 엔진에 생성된 컨테이너/볼륨의 ID
        public string Name { get; set; } = string.Empty; // 노드(도커 리소스)의 이름
        public string ImageName { get; set; } = string.Empty; // 컨테이너인 경우 사용된 이미지 이름

        public NodeType Type { get; set; } // 노드의 종류 (컨테이너, 볼륨, 인터넷 등)

        // --- 시각적 위치 및 크기 정보 ---
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        // --- 도커 세부 설정 정보 ---
        public List<string> PortBindings { get; set; } = new List<string>(); // 포트 매핑 정보
        public List<string> EnvironmentVariables { get; set; } = new List<string>(); // 환경 변수 정보
        public Dictionary<string, string> NetworkStaticIps { get; set; } = new Dictionary<string, string>(); // 네트워크별 정적 IPv4
        public Dictionary<string, ContainerNetworkOptions> NetworkOptions { get; set; } = new Dictionary<string, ContainerNetworkOptions>(); // 네트워크별 연결 옵션
        public string DockerVolumeName { get; set; } = string.Empty; // 실제 Docker 볼륨 이름
        public bool VolumeExternal { get; set; } // 앱이 생성하지 않고 참조하는 외부 볼륨
        public Dictionary<string, string> VolumeLabels { get; set; } = new Dictionary<string, string>(); // 볼륨 라벨
        public Dictionary<string, string> VolumeDriverOptions { get; set; } = new Dictionary<string, string>(); // 볼륨 드라이버 옵션
        public string RestartPolicy { get; set; } = "no"; // 재시작 정책 (no, always 등)
        public string ComposeServiceName { get; set; } = string.Empty; // 원본 compose service 키
        public string ComposeRawServiceYaml { get; set; } = string.Empty; // 원본 service YAML
        public string ComposeRawVolumeYaml { get; set; } = string.Empty; // 원본 volume YAML
    }

    /// <summary>
    /// 두 노드를 이어주는 연결선(의존성, 네트워크 연결, 볼륨 마운트 등)의 정보를 담는 데이터 모델입니다.
    /// </summary>
    public class ConnectionData
    {
        public string SourceNodeId { get; set; } = string.Empty; // 시작점 노드의 ID
        public string TargetNodeId { get; set; } = string.Empty; // 도착점 노드의 ID
        public PortDirection SourceDir { get; set; } // 시작점 노드에서 선이 빠져나온 방향 (Top, Bottom 등)
        public PortDirection TargetDir { get; set; } // 도착점 노드에 선이 들어간 방향

        public RelationType RelationType { get; set; } // 연결의 논리적 의미 (네트워크 연결, 볼륨 마운트 등)

        public string? MountPath { get; set; } // 볼륨 연결인 경우 컨테이너 내부의 마운트 경로
        public string? IpAddress { get; set; } // 네트워크 연결인 경우 할당된 정적 IP (있을 경우)
    }

    /// <summary>
    /// 여러 노드를 묶는 그룹(일반 폴더 또는 도커 네트워크)의 정보를 담는 데이터 모델입니다.
    /// </summary>
    public class GroupData
    {
        public string Id { get; set; } = string.Empty; // 그룹의 고유 ID

        public string Title { get; set; } = "Group"; // 그룹의 이름
        public GroupType Type { get; set; } = GroupType.General; // 그룹의 종류 (일반 폴더, 도커 네트워크 등)
        public string Driver { get; set; } = "bridge";
        public string Subnet { get; set; } = string.Empty;
        public string Gateway { get; set; } = string.Empty;
        public string IpRange { get; set; } = string.Empty;
        public bool Internal { get; set; }
        public bool Attachable { get; set; }
        public bool EnableIPv6 { get; set; }
        public bool External { get; set; }
        public string ComposeNetworkName { get; set; } = string.Empty;
        public string ComposeRawNetworkYaml { get; set; } = string.Empty;
        public Dictionary<string, string> Labels { get; set; } = new();
        public Dictionary<string, string> DriverOptions { get; set; } = new();
        public Dictionary<string, string> AuxAddresses { get; set; } = new();

        // --- 시각적 위치 및 크기 정보 ---
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public List<string> ContainedNodeIds { get; set; } = new List<string>(); // 이 그룹 안에 포함된 노드들의 ID 목록
    }
}
