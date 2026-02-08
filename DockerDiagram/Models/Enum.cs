namespace DockerDiagram.Models
{
    public enum NodeType // 노드 타입 enum 클래스
    {
        Container,  // 기본 컨테이너
        Volume,     // 데이터 볼륨 (원통형)
        Internet
    }

    public enum GroupType
    {
        General,    // 일반 폴더/그룹
        Network     // 도커 네트워크
    }
}