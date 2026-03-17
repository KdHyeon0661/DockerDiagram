using System;

namespace DockerDiagram.ViewModels
{
    public interface IConnectableItem
    {
        string Id { get; }
        string Name { get; }
        double X { get; }
        double Y { get; }
        double Width { get; }
        double Height { get; }
        double CenterX { get; }
        double CenterY { get; }

        // 위치나 크기가 변할 때 선(Connector)에게 알려주는 이벤트
        event EventHandler? OnPositionChanged;
    }
}