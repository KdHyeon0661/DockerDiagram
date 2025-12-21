namespace DockerDiagram.Models
{
    public enum NodeType // 노드 타입 enum 클래스
    {
        Container,  // 기본 컨테이너
        Volume,     // 데이터 볼륨 (원통형)
        Network,    // 네트워크 (다이아몬드)
    }
}