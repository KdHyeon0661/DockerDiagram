using System;
using System.Collections.Generic;
using DockerDiagram.Helpers;

namespace DockerDiagram.Models
{
    // 전체 파일 구조
    public class DiagramFile
    {
        public string Version { get; set; } = "1.14";
        public DateTime SavedAt { get; set; } = DateTime.Now;
        public List<SheetData> Sheets { get; set; } = new List<SheetData>();
        public int ActiveSheetIndex { get; set; } = 0;
    }

    // 시트 데이터
    public class SheetData
    {
        public string Title { get; set; } = "Sheet";
        public double MapWidth { get; set; }
        public double MapHeight { get; set; }
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
        public string Id { get; set; } = string.Empty;
        public string DockerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ImageName { get; set; } = string.Empty;

        public NodeType Type { get; set; }

        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public List<string> PortBindings { get; set; } = new List<string>();
        public List<string> EnvironmentVariables { get; set; } = new List<string>();
        public string RestartPolicy { get; set; } = "no";
    }

    // 연결선 데이터
    public class ConnectionData
    {
        public string SourceNodeId { get; set; } = string.Empty;
        public string TargetNodeId { get; set; } = string.Empty;
        public PortDirection SourceDir { get; set; }
        public PortDirection TargetDir { get; set; }
        public RelationType RelationType { get; set; }

        public string? MountPath { get; set; }
        public string? IpAddress { get; set; }
    }

    // 그룹 데이터
    public class GroupData
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = "Group";
        public GroupType Type { get; set; } = GroupType.General;

        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public List<string> ContainedNodeIds { get; set; } = new List<string>();
    }
}