namespace DockerDiagram.Common
{
    /// <summary>
    /// 런타임 리소스 상태를 표현하는 공통 색상입니다.
    /// 리소스 종류나 장식용 강조 색상에는 사용하지 않습니다.
    /// </summary>
    public static class StatusColors
    {
        public const string Running = "#28A745";
        public const string Failed = "#DC3545";
        public const string Unknown = "#808080";
        public const string Pending = "#E67E22";
        public const string InProgress = "#FFC107";
        public const string Progress = "#0D6EFD";
        public const string Busy = "#17A2B8";
        public const string Warning = "#E6A700";
        public const string Historical = "#7A858D";
        public const string NotChecked = "#888888";
    }
}
