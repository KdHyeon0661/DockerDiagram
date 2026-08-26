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
            var candidates = GetClosestPortPairs(source, target, 4);
            PointCollection? bestRoute = null;
            double bestLength = double.MaxValue;
            PortDirection bestSourceDirection = candidates[0].SourceDirection;
            PortDirection bestTargetDirection = candidates[0].TargetDirection;

            foreach (var candidate in candidates)
            {
                Point start = GetBorderPoint(source, candidate.SourceDirection);
                Point end = GetBorderPoint(target, candidate.TargetDirection);
                Rect sourceBounds = source.UsePointRouting
                    ? new Rect(start.X, start.Y, 0, 0)
                    : new Rect(source.X, source.Y, source.Width, source.Height);
                Rect targetBounds = target.UsePointRouting
                    ? new Rect(end.X, end.Y, 0, 0)
                    : new Rect(target.X, target.Y, target.Width, target.Height);

                try
                {
                    PointCollection route = OrthogonalRouter.GetRoute(
                        start,
                        candidate.SourceDirection,
                        end,
                        candidate.TargetDirection,
                        sourceBounds,
                        targetBounds);
                    if (route.Count < 2) continue;

                    double length = GetPathLength(route);
                    if (length >= bestLength) continue;

                    bestLength = length;
                    bestRoute = route;
                    bestSourceDirection = candidate.SourceDirection;
                    bestTargetDirection = candidate.TargetDirection;
                }
                catch
                {
                    // Try the next candidate. A straight fallback is used if all candidates fail.
                }
            }

            if (bestRoute == null)
            {
                Point start = GetBorderPoint(source, bestSourceDirection);
                Point end = GetBorderPoint(target, bestTargetDirection);
                bestRoute = new PointCollection { start, end };
            }

            return new ConnectorRoutePlan(bestSourceDirection, bestTargetDirection, bestRoute);
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