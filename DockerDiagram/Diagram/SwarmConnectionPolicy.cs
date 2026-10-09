using DockerDiagram.Models;

namespace DockerDiagram.Diagram
{
    public readonly record struct SwarmConnectionDecision(
        bool IsAllowed,
        RelationType RelationType,
        bool ReverseDirection,
        string ErrorMessage);

    /// <summary>
    /// Swarm 리소스 조합을 다이어그램 관계로 변환하는 순수 정책입니다.
    /// UI 이벤트와 분리되어 새 리소스 종류가 추가되어도 한곳에서 규칙을 확장할 수 있습니다.
    /// </summary>
    public static class SwarmConnectionPolicy
    {
        public static SwarmConnectionDecision Resolve(
            RuntimeResourceKind sourceKind,
            RuntimeResourceKind targetKind)
        {
            if (sourceKind == RuntimeResourceKind.SwarmOverlayNetwork ||
                targetKind == RuntimeResourceKind.SwarmOverlayNetwork)
            {
                return Deny("Overlay Network는 선으로 연결하지 않습니다. Service를 네트워크 영역 안에 배치해 소속을 표현해 주세요.");
            }

            if (sourceKind == RuntimeResourceKind.SwarmVisualGroup ||
                targetKind == RuntimeResourceKind.SwarmVisualGroup)
            {
                return Deny("Visual Group은 화면 정리용이며 Swarm ServiceSpec에 적용되지 않습니다. 리소스를 그룹 영역 안에 배치해 주세요.");
            }

            if (sourceKind == RuntimeResourceKind.SwarmService &&
                targetKind == RuntimeResourceKind.SwarmService)
            {
                return Deny("Swarm에는 Service 시작 순서를 보장하는 dependency 설정이 없습니다. 실제로 적용되지 않는 연결선은 만들지 않습니다.");
            }

            if (IsPair(sourceKind, targetKind, RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmVolume))
            {
                return Allow(RelationType.VolumeMount, sourceKind != RuntimeResourceKind.SwarmService);
            }

            if (IsPair(sourceKind, targetKind, RuntimeResourceKind.SwarmExternalTraffic, RuntimeResourceKind.SwarmService))
            {
                return Allow(RelationType.SwarmPublishedPort, sourceKind != RuntimeResourceKind.SwarmExternalTraffic);
            }

            if (IsPair(sourceKind, targetKind, RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmSecret))
            {
                return Allow(RelationType.SwarmSecretReference, sourceKind != RuntimeResourceKind.SwarmService);
            }

            if (IsPair(sourceKind, targetKind, RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmConfig))
            {
                return Allow(RelationType.SwarmConfigReference, sourceKind != RuntimeResourceKind.SwarmService);
            }

            return Deny(
                "이 Swarm 리소스 조합은 연결할 수 없습니다.\n" +
                "허용: Service–Volume, Published Port–Service, Service–Secret, Service–Config.");
        }

        private static SwarmConnectionDecision Allow(RelationType relationType, bool reverse = false) =>
            new(true, relationType, reverse, string.Empty);

        private static SwarmConnectionDecision Deny(string message) =>
            new(false, RelationType.Dependency, false, message);

        private static bool IsPair(
            RuntimeResourceKind first,
            RuntimeResourceKind second,
            RuntimeResourceKind expectedA,
            RuntimeResourceKind expectedB) =>
            (first == expectedA && second == expectedB) ||
            (first == expectedB && second == expectedA);
    }
}
