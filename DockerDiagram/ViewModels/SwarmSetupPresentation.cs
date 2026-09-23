using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public enum SwarmSetupPanelKind
    {
        Inactive,
        Manager,
        Worker,
        Waiting,
        Error
    }

    /// <summary>
    /// Swarm 상태 모델을 설정 창의 한 가지 화면 상태로 변환합니다.
    /// XAML이나 이벤트 핸들러에 RuntimeKind/상태 if문이 흩어지지 않도록 한곳에서 결정합니다.
    /// </summary>
    public sealed record SwarmSetupPresentation(
        SwarmSetupPanelKind Panel,
        string StatusTitle,
        string StatusDescription)
    {
        public static SwarmSetupPresentation FromState(SwarmClusterState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            return state.Membership switch
            {
                SwarmMembershipState.Inactive => new(
                    SwarmSetupPanelKind.Inactive,
                    "Swarm 미가입",
                    "이 Docker Engine은 아직 Swarm에 가입되어 있지 않습니다."),

                SwarmMembershipState.Manager => new(
                    SwarmSetupPanelKind.Manager,
                    "Swarm Manager",
                    "이 Docker Engine에서 클러스터를 관리할 수 있습니다."),

                SwarmMembershipState.Worker => new(
                    SwarmSetupPanelKind.Worker,
                    "Swarm Worker",
                    "클러스터 변경은 연결된 Manager에서 수행해야 합니다."),

                SwarmMembershipState.Pending => new(
                    SwarmSetupPanelKind.Waiting,
                    "Swarm 참가 처리 중",
                    "Docker Engine이 클러스터 참가를 완료할 때까지 기다린 뒤 새로고침해 주세요."),

                SwarmMembershipState.Locked => new(
                    SwarmSetupPanelKind.Error,
                    "Swarm 잠김",
                    "Manager 잠금을 해제한 뒤 다시 시도해야 합니다."),

                SwarmMembershipState.Error => new(
                    SwarmSetupPanelKind.Error,
                    "Swarm 오류",
                    string.IsNullOrWhiteSpace(state.ErrorMessage)
                        ? "Docker Engine이 Swarm 오류를 보고했습니다."
                        : state.ErrorMessage),

                _ => new(
                    SwarmSetupPanelKind.Error,
                    "Swarm 상태 확인 불가",
                    "Docker Engine의 Swarm 상태를 판별할 수 없습니다.")
            };
        }
    }
}
