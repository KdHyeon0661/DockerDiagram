namespace DockerDiagram.Models
{
    public class DockerImage // 도커 이미지 정보를 담는 클래스
    {
        public string Id { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public long Size { get; set; }
    }
}