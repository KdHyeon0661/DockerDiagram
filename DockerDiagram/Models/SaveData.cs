using System;
using DockerDiagram.Helpers;

namespace DockerDiagram.Models
{
    // 전체 파일 구조
    public class DiagramFile
    {
        public string Version { get; set; } = "1.0";
        public DateTime SavedAt { get; set; } = DateTime.Now;
        public List<SheetData> Sheets { get; set; } = new List<SheetData>();
        public int ActiveSheetIndex { get; set; } = 0;
    }

    // 시트 데이터
    public class SheetData
    {
        public string Title { get; set; } = "Sheet";

        // 맵 크기
        public double MapWidth { get; set; }
        public double MapHeight { get; set; }

        // 뷰포트 상태 (줌/팬)
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double Scale { get; set; }

        public List<NodeData> Nodes { get; set; } = new List<NodeData>();
        public List<ConnectionData> Connections { get; set; } = new List<ConnectionData>();
        public List<GroupData> Groups { get; set; } = new List<GroupData>();
    }

    // 노드 데이터 (컨테이너, 볼륨, 네트워크)
    public class NodeData
    {
        public string Id { get; set; } = string.Empty; // 다이어그램 내부 식별자 (Guid)
        public string DockerId { get; set; } = string.Empty; // 실제 Docker ID (매핑용)
        public string Name { get; set; } = string.Empty;
        public string ImageName { get; set; } = string.Empty;
        public NodeType Type { get; set; }

        // 레이아웃 정보
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    // 연결선 데이터
    public class ConnectionData
    {
        public string SourceNodeId { get; set; } = string.Empty;
        public string TargetNodeId { get; set; } = string.Empty;
        public PortDirection SourceDir { get; set; }
        public PortDirection TargetDir { get; set; }

        // 관계 타입
        public RelationType RelationType { get; set; }
    }

    // 그룹 데이터
    public class GroupData
    {
        public string Title { get; set; } = "Group";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        // 그룹에 속한 노드들의 다이어그램 ID 목록
        public List<string> ContainedNodeIds { get; set; } = new List<string>();
    }

    // RelationType 정의
    // ConnectorViewModel.cs에 있는 것과 동일한 구조입니다.
    public enum RelationType
    {
        Dependency,     // Container <-> Container
        VolumeMount,    // Container <-> Volume
        NetworkAttach   // Container <-> Network
    }
}