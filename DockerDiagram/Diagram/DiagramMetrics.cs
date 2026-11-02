using System;

namespace DockerDiagram.Diagram
{
    /// <summary>
    /// 다이어그램 전체에서 공유하는 좌표 및 격자 규칙입니다.
    /// </summary>
    public static class DiagramMetrics
    {
        public const double GridSize = 10.0;

        public static double SnapToGrid(double value) =>
            Math.Round(value / GridSize) * GridSize;
    }
}
