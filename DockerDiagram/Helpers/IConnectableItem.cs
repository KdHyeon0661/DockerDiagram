using System;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 다이어그램 도화지 위에서 선(Connector)으로 연결될 수 있는 모든 시각적 요소(노드, 그룹 등)가 공통으로 가져야 할 규격을 정의합니다.
    /// 연결선은 대상의 구체적인 타입(컨테이너, 볼륨 등)을 몰라도 이 인터페이스의 좌표 정보만 보고 선을 정확히 그릴 수 있습니다.
    /// </summary>
    public interface IConnectableItem
    {
        string Id { get; }      // 요소의 고유 식별자 (선이 대상을 기억할 때 사용)
        string Name { get; }    // UI나 로그에 표시될 요소의 이름

        double X { get; }       // 도화지 상의 좌측 상단 X 좌표
        double Y { get; }       // 도화지 상의 좌측 상단 Y 좌표
        double Width { get; }   // 요소의 너비
        double Height { get; }  // 요소의 높이

        double CenterX { get; } // 요소의 정중앙 X 좌표 (주로 선이 연결되는 기준점)
        double CenterY { get; } // 요소의 정중앙 Y 좌표 (주로 선이 연결되는 기준점)
        bool UsePointRouting { get; } // 그룹처럼 경계 상자 대신 연결점을 기준으로 라우팅할지 여부

        // UI에서 마우스 드래그로 요소의 위치나 크기가 변할 때, 자신에게 연결된 선들에게 "나 움직였으니 선 위치 다시 계산해!" 라고 알려주는 이벤트
        event EventHandler? OnPositionChanged;
    }
}
