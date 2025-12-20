namespace DockerDiagram.Models
{
    public enum NodeType
    {
        Container,  // 기본 컨테이너
        Volume,     // 데이터 볼륨 (원통형)
        Network,    // 네트워크 (다이아몬드)
    }
}