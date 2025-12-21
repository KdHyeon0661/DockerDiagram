namespace DockerDiagram.Models
{
    public class DockerContainer // 도커 컨테이너 정보(id, 이름, 이미지, 상태, 포트)와 색상 정보를 담는 클래스
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Ports { get; set; } = string.Empty;
        public string StateColor { get; set; } = "#FFFFFF";

        public NodeType Type { get; set; }
    }
}