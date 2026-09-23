using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public enum RuntimeSidebarKind
    {
        Standalone,
        Swarm,
        Unsupported
    }

    /// <summary>
    /// 활성 런타임이 메인 화면에 제공하는 UI 구성을 나타냅니다.
    /// MainWindow 곳곳에서 RuntimeKind를 직접 분기하지 않고 이 프로필만 바인딩합니다.
    /// </summary>
    public sealed record RuntimeUiProfile(
        RuntimeKind RuntimeKind,
        string DisplayName,
        RuntimeSidebarKind SidebarKind,
        string PrimaryMenuToolTip,
        string ImageMenuToolTip,
        bool ShowComposeCommands,
        bool ShowImageMenu,
        bool IsAvailable)
    {
        public bool ShowStandaloneSidebar => SidebarKind == RuntimeSidebarKind.Standalone;
        public bool ShowSwarmSidebar => SidebarKind == RuntimeSidebarKind.Swarm;
        public bool ShowUnsupportedSidebar => SidebarKind == RuntimeSidebarKind.Unsupported;
        public bool ShowSwarmCommands => RuntimeKind == RuntimeKind.DockerSwarm;
        public bool ShowSwarmSetup => RuntimeKind is RuntimeKind.DockerEngine or RuntimeKind.DockerSwarm;
        public bool ShowDockerHistoryOptions => RuntimeKind == RuntimeKind.DockerEngine;
        public bool ShowDestructiveDockerMaintenance => RuntimeKind == RuntimeKind.DockerEngine;
    }

    /// <summary>
    /// RuntimeKind에 따른 UI 분기를 한곳에서만 수행합니다.
    /// 각 런타임의 기존 화면 구성을 보존하면서 공용 메뉴 표시만 중앙에서 결정합니다.
    /// </summary>
    public static class RuntimeUiProfiles
    {
        public static RuntimeUiProfile For(RuntimeKind runtimeKind) => runtimeKind switch
        {
            RuntimeKind.DockerEngine => new RuntimeUiProfile(
                runtimeKind,
                "Standalone Docker",
                RuntimeSidebarKind.Standalone,
                "파일 / 시트 / Compose",
                "이미지 / Docker",
                ShowComposeCommands: true,
                ShowImageMenu: true,
                IsAvailable: true),

            RuntimeKind.DockerSwarm => new RuntimeUiProfile(
                runtimeKind,
                "Docker Swarm",
                RuntimeSidebarKind.Swarm,
                "파일 / 시트 / Stack",
                "이미지 / Manager 호스트",
                ShowComposeCommands: false,
                ShowImageMenu: true,
                IsAvailable: true),

            RuntimeKind.Kubernetes => new RuntimeUiProfile(
                runtimeKind,
                "Kubernetes",
                RuntimeSidebarKind.Standalone,
                "파일 / 시트",
                "이미지 참조",
                ShowComposeCommands: false,
                ShowImageMenu: false,
                IsAvailable: true),

            _ => new RuntimeUiProfile(
                runtimeKind,
                runtimeKind.ToString(),
                RuntimeSidebarKind.Unsupported,
                "파일 / 시트",
                "런타임",
                ShowComposeCommands: false,
                ShowImageMenu: false,
                IsAvailable: false)
        };
    }
}
