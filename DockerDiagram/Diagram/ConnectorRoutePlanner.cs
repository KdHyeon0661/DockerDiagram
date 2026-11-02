using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Diagram
{
    public sealed class ConnectorRoutePlan
    {
        public ConnectorRoutePlan(PortDirection sourceDirection, PortDirection targetDirection, PointCollection points)
        {
            SourceDirection = sourceDirection;
            TargetDirection = targetDirection;
            Points = points;
        }

        public PortDirection SourceDirection { get; }
        public PortDirection TargetDirection { get; }
        public PointCollection Points { get; }
    }


    public static class ConnectorRoutePlanner
    {
        private static readonly PortDirection[] Directions =
        {
            PortDirection.Top,
            PortDirection.Bottom,
            PortDirection.Left,
            PortDirection.Right
        };


        public static ConnectorRoutePlan Calculate(IConnectableItem source, IConnectableItem target)
        {
            var preferredPair = GetPreferredFacingPortPair(source, target);
            if (preferredPair.HasValue &&
                TryCalculateRoute(
                    source,
                    target,
                    preferredPair.Value.SourceDirection,
                    preferredPair.Value.TargetDirection,
                    out PointCollection preferredRoute))
            {
                return new ConnectorRoutePlan(
                    preferredPair.Value.SourceDirection,
                    preferredPair.Value.TargetDirection,
                    preferredRoute);
            }

            var candidates = GetClosestPortPairs(source, target, 4);
            PointCollection? bestRoute = null;
            double bestLength = double.MaxValue;
            PortDirection bestSourceDirection = candidates[0].SourceDirection;
            PortDirection bestTargetDirection = candidates[0].TargetDirection;

            foreach (var candidate in candidates)
            {
                if (!TryCalculateRoute(
                        source,
                        target,
                        candidate.SourceDirection,
                        candidate.TargetDirection,
                        out PointCollection route))
                {
                    continue;
                }

                double length = GetPathLength(route);
                if (length >= bestLength) continue;

                bestLength = length;
                bestRoute = route;
                bestSourceDirection = candidate.SourceDirection;
                bestTargetDirection = candidate.TargetDirection;
            }

            if (bestRoute == null)
            {
                Point start = GetBorderPoint(source, bestSourceDirection);
                Point end = GetBorderPoint(target, bestTargetDirection);
                bestRoute = [start, end];
            }

            return new ConnectorRoutePlan(bestSourceDirection, bestTargetDirection, bestRoute);
        }

        private static bool TryCalculateRoute(
            IConnectableItem source,
            IConnectableItem target,
            PortDirection sourceDirection,
            PortDirection targetDirection,
            out PointCollection route)
        {
            Point start = GetBorderPoint(source, sourceDirection);
            Point end = GetBorderPoint(target, targetDirection);
            Rect sourceBounds = source.UsePointRouting
                ? new Rect(start.X, start.Y, 0, 0)
                : new Rect(source.X, source.Y, source.Width, source.Height);
            Rect targetBounds = target.UsePointRouting
                ? new Rect(end.X, end.Y, 0, 0)
                : new Rect(target.X, target.Y, target.Width, target.Height);

            try
            {
                route = OrthogonalRouter.GetRoute(
                    start,
                    sourceDirection,
                    end,
                    targetDirection,
                    sourceBounds,
                    targetBounds);
                return route.Count >= 2;
            }
            catch
            {
                route = [];
                return false;
            }
        }

        /// <summary>
        /// 일반 노드가 한 축으로 명확히 떨어져 있고 반대 축의 영역이 겹치면
        /// 서로 마주 보는 포트를 우선합니다. 대각선 배치와 그룹 포인트 라우팅은
        /// 기존 최단 경로 탐색에 맡깁니다.
        /// </summary>
        private static (PortDirection SourceDirection, PortDirection TargetDirection)?
            GetPreferredFacingPortPair(IConnectableItem source, IConnectableItem target)
        {
            if (source.UsePointRouting || target.UsePointRouting)
                return null;

            double horizontalOverlap = Math.Min(source.X + source.Width, target.X + target.Width) -
                                       Math.Max(source.X, target.X);
            if (horizontalOverlap > 0)
            {
                if (source.Y + source.Height <= target.Y)
                    return (PortDirection.Bottom, PortDirection.Top);
                if (target.Y + target.Height <= source.Y)
                    return (PortDirection.Top, PortDirection.Bottom);
            }

            double verticalOverlap = Math.Min(source.Y + source.Height, target.Y + target.Height) -
                                     Math.Max(source.Y, target.Y);
            if (verticalOverlap > 0)
            {
                if (source.X + source.Width <= target.X)
                    return (PortDirection.Right, PortDirection.Left);
                if (target.X + target.Width <= source.X)
                    return (PortDirection.Left, PortDirection.Right);
            }

            return null;
        }

        public static Point GetBorderPoint(IConnectableItem item, PortDirection direction)
        {
            return direction switch
            {
                PortDirection.Left => new Point(item.X, item.CenterY),
                PortDirection.Right => new Point(item.X + item.Width, item.CenterY),
                PortDirection.Top => new Point(item.CenterX, item.Y),
                PortDirection.Bottom => new Point(item.CenterX, item.Y + item.Height),
                _ => new Point(item.CenterX, item.CenterY)
            };
        }

        private static List<(PortDirection SourceDirection, PortDirection TargetDirection)> GetClosestPortPairs(
            IConnectableItem source,
            IConnectableItem target,
            int count)
        {
            var pairs = new List<(PortDirection SourceDirection, PortDirection TargetDirection, double Distance)>();
            foreach (PortDirection sourceDirection in Directions)
            {
                Point sourcePoint = GetBorderPoint(source, sourceDirection);
                foreach (PortDirection targetDirection in Directions)
                {
                    Point targetPoint = GetBorderPoint(target, targetDirection);
                    double deltaX = sourcePoint.X - targetPoint.X;
                    double deltaY = sourcePoint.Y - targetPoint.Y;
                    pairs.Add((sourceDirection, targetDirection, deltaX * deltaX + deltaY * deltaY));
                }
            }

            return pairs.OrderBy(pair => pair.Distance)
                        .Take(count)
                        .Select(pair => (pair.SourceDirection, pair.TargetDirection))
                        .ToList();
        }

        private static double GetPathLength(PointCollection path)
        {
            double length = 0;
            for (int index = 0; index < path.Count - 1; index++)
            {
                length += Math.Abs(path[index].X - path[index + 1].X) +
                          Math.Abs(path[index].Y - path[index + 1].Y);
            }

            return length;
        }
    }
}
