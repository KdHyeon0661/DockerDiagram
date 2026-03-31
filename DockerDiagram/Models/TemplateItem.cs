namespace DockerDiagram.Models
{
    /// <summary>
    /// UI의 템플릿 목록(사이드바 등)에 표시될 템플릿 아이템을 정의하는 데이터 모델입니다.
    /// 사용자가 자주 사용하는 도커 리소스(컨테이너, 볼륨 등)를 미리 정의해두고 쉽게 생성할 수 있도록 돕습니다.
    /// </summary>
    public class TemplateItem
    {
        public string Name { get; set; } = string.Empty; // 템플릿의 이름 (예: "MySQL Database", "Nginx Server")
        public string Image { get; set; } = string.Empty; // 기반이 될 도커 이미지 이름 및 태그 (예: "mysql:8.0")
        public NodeType Type { get; set; } = NodeType.Container; // 생성될 노드의 종류 (Container, Volume, Internet 등)
        public bool IsDefault { get; set; } = false; // 프로그램에서 기본적으로 제공하는 내장 템플릿인지 여부

        public string DisplayName => IsDefault ? $"[Basic] {Name}" : Name; // UI 리스트에 표시될 문자열 (기본 제공 시 [Basic] 표시)
    }
}