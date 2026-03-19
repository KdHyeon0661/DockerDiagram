using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DockerDiagram.Helpers
{
    public enum PortDirection { Top, Bottom, Left, Right, None }

    public static class OrthogonalRouter
    {
        private const double MARGIN = 20;   // 노드로부터 떨어지는 기본 거리 (여유 공간)
        private const int GRID_SIZE = 10;   // 격자 크기
        private const double COLLISION_PAD = 1.0;

        public static PointCollection GetRoute(Point start, PortDirection startDir,
                                             Point end, PortDirection endDir,
                                             Rect sourceRect, Rect targetRect)
        {
            PointCollection finalPath = new PointCollection();

            // 1. 기하학적 스마트 라우팅 먼저 시도 (틈새 관통, Z/L자, 크게 우회하기 모두 포함)
            var smartPath = TryGetSmartRoute(start, startDir, end, endDir, sourceRect, targetRect);
            if (smartPath != null)
            {
                return new PointCollection(smartPath);
            }

            // 2. A* 준비 (모든 스마트 라우팅이 실패했을 때만 최후의 수단으로 작동)
            Point startStub = GetShiftedPoint(start, startDir, MARGIN);
            Point endStub = GetShiftedPoint(end, endDir, MARGIN);

            Point startNode = SnapToGrid(startStub);
            Point endNode = SnapToGrid(endStub);

            // 3. A* 탐색 (장애물 회피)
            List<Point> pathPoints = FindPathAStar(startNode, endNode, sourceRect, targetRect, startDir);

            // 4. 경로 조립
            finalPath.Add(start);

            if (pathPoints.Count > 0)
            {
                if (!IsRectilinear(start, pathPoints[0]))
                {
                    finalPath.Add(AlignPoint(start, startStub, startDir));
                }
            }

            foreach (var pt in pathPoints)
            {
                finalPath.Add(pt);
            }

            // 5. 마지막 구간 직교화
            Point lastPoint = finalPath.Count > 0 ? finalPath.Last() : start;
            if (!IsRectilinear(lastPoint, end))
            {
                Point corner = GetCornerPoint(lastPoint, end, endDir);
                finalPath.Add(corner);
            }

            if (finalPath.Last() != end)
            {
                finalPath.Add(end);
            }

            // 대각선 방지 및 중복 좌표 제거를 위한 최종 정리
            return new PointCollection(SimplifyPath(finalPath.ToList()));
        }

        // --- A* Algorithm ---

        private class PathNode
        {
            public Point Point { get; set; }
            public PathNode? Parent { get; set; }
            public double G { get; set; }
            public double H { get; set; }
            public double F => G + H;
            public PortDirection ArrivalDirection { get; set; }
        }

        private static List<Point> FindPathAStar(Point start, Point end, Rect obstacle1, Rect obstacle2, PortDirection startDir)
        {
            if (GetManhattanDistance(start, end) < GRID_SIZE) return new List<Point> { start, end };

            Rect searchArea = new Rect(start, end);
            searchArea.Union(obstacle1);
            searchArea.Union(obstacle2);
            searchArea.Inflate(200, 200);

            var openList = new List<PathNode>();
            var closedSet = new HashSet<Point>();
            Point targetSnap = SnapToGrid(end);

            openList.Add(new PathNode
            {
                Point = SnapToGrid(start),
                Parent = null,
                G = 0,
                H = GetManhattanDistance(start, end),
                ArrivalDirection = startDir
            });

            Rect block1 = obstacle1; block1.Inflate(2, 2);
            Rect block2 = obstacle2; block2.Inflate(2, 2);

            Rect danger1 = obstacle1; danger1.Inflate(15, 15);
            Rect danger2 = obstacle2; danger2.Inflate(15, 15);

            int loopCount = 0;
            const int MAX_LOOPS = 4000;

            while (openList.Count > 0)
            {
                if (loopCount++ > MAX_LOOPS) break;

                var current = openList.OrderBy(n => n.F).First();

                if (Math.Abs(current.Point.X - targetSnap.X) < 1.5 && Math.Abs(current.Point.Y - targetSnap.Y) < 1.5)
                {
                    return ReconstructPath(current, end);
                }

                openList.Remove(current);
                closedSet.Add(current.Point);

                foreach (var neighbor in GetNeighbors(current.Point))
                {
                    if (!searchArea.Contains(neighbor)) continue;
                    if (closedSet.Contains(neighbor)) continue;

                    bool isTargetZone = (Math.Abs(neighbor.X - targetSnap.X) < 1.5 && Math.Abs(neighbor.Y - targetSnap.Y) < 1.5);

                    if (!isTargetZone)
                    {
                        if (block1.Contains(neighbor) || block2.Contains(neighbor)) continue;
                    }

                    double moveCost = GRID_SIZE;
                    PortDirection newDir = GetDirection(current.Point, neighbor);

                    if (current.ArrivalDirection != PortDirection.None && current.ArrivalDirection != newDir)
                        moveCost += 5;

                    if (!isTargetZone)
                    {
                        if (danger1.Contains(neighbor) || danger2.Contains(neighbor))
                        {
                            moveCost += 20;
                        }
                    }

                    double newG = current.G + moveCost;
                    var existingNode = openList.FirstOrDefault(n => n.Point == neighbor);

                    if (existingNode == null)
                    {
                        openList.Add(new PathNode
                        {
                            Point = neighbor,
                            Parent = current,
                            G = newG,
                            H = GetManhattanDistance(neighbor, end),
                            ArrivalDirection = newDir
                        });
                    }
                    else if (newG < existingNode.G)
                    {
                        existingNode.G = newG;
                        existingNode.Parent = current;
                        existingNode.ArrivalDirection = newDir;
                    }
                }
            }

            return new List<Point>();
        }

        private static List<Point> ReconstructPath(PathNode? node, Point actualEnd)
        {
            var rawPath = new List<Point>();
            while (node != null)
            {
                rawPath.Add(node.Point);
                node = node.Parent;
            }

            rawPath.Reverse();
            if (rawPath.Count > 0) rawPath[rawPath.Count - 1] = actualEnd;
            return SimplifyPath(rawPath);
        }

        // --- Smart Route (단계별 라우팅 + 새로운 '크게 우회하기' 로직 추가) ---
        private static List<Point>? TryGetSmartRoute(Point start, PortDirection startDir, Point end, PortDirection endDir, Rect r1, Rect r2)
        {
            Point sStub = GetShiftedPoint(start, startDir, MARGIN);
            Point eStub = GetShiftedPoint(end, endDir, MARGIN);

            // [1단계] 일반적인 Z자, L자 경로 생성
            var basicCandidates = new List<List<Point>>();
            double ptMidX = Math.Round((sStub.X + eStub.X) / 2 / GRID_SIZE) * GRID_SIZE;
            double ptMidY = Math.Round((sStub.Y + eStub.Y) / 2 / GRID_SIZE) * GRID_SIZE;

            // Z자 1: 좌/우 방향으로 벌어졌을 때 X축 중앙에서 꺾기
            var zShapeXMid = new List<Point> { start, sStub, new Point(ptMidX, sStub.Y), new Point(ptMidX, eStub.Y), eStub, end };
            // Z자 2: 상/하 방향으로 벌어졌을 때 Y축 중앙에서 꺾기
            var zShapeYMid = new List<Point> { start, sStub, new Point(sStub.X, ptMidY), new Point(eStub.X, ptMidY), eStub, end };

            // L자 1, 2
            var lShape1 = new List<Point> { start, sStub, new Point(sStub.X, eStub.Y), eStub, end };
            var lShape2 = new List<Point> { start, sStub, new Point(eStub.X, sStub.Y), eStub, end };

            // ★ [핵심] 방향에 따라 가장 예쁜 선을 먼저 시도하도록 순서(우선순위) 결정!
            bool isStartVertical = (startDir == PortDirection.Top || startDir == PortDirection.Bottom);
            bool isEndVertical = (endDir == PortDirection.Top || endDir == PortDirection.Bottom);

            if (isStartVertical && isEndVertical)
            {
                // 상-하 끼리 연결될 때는 Y축 정중앙에서 꺾이는 Z자가 가장 예쁨
                basicCandidates.Add(zShapeYMid);
                basicCandidates.Add(lShape1);
                basicCandidates.Add(lShape2);
                basicCandidates.Add(zShapeXMid);
            }
            else if (!isStartVertical && !isEndVertical)
            {
                // 좌-우 끼리 연결될 때는 X축 정중앙에서 꺾이는 Z자가 가장 예쁨
                basicCandidates.Add(zShapeXMid);
                basicCandidates.Add(lShape1);
                basicCandidates.Add(lShape2);
                basicCandidates.Add(zShapeYMid);
            }
            else
            {
                // 상/하 - 좌/우 끼리 수직으로 엇갈릴 때는 깔끔한 L자가 최우선
                basicCandidates.Add(lShape1);
                basicCandidates.Add(lShape2);
                basicCandidates.Add(zShapeYMid);
                basicCandidates.Add(zShapeXMid);
            }

            // 우선순위대로 하나씩 검사해서 안 부딪히면 바로 채택!
            foreach (var path in basicCandidates)
                if (IsValidPath(path, r1, r2)) return SimplifyPath(path);


            // [2단계] 틈새(Gap) 정중앙 관통
            var gapCandidates = new List<List<Point>>();

            bool hasHorzGap = r1.Right < r2.Left || r2.Right < r1.Left;
            if (hasHorzGap)
            {
                double gapX = r1.Right < r2.Left ? (r1.Right + r2.Left) / 2 : (r2.Right + r1.Left) / 2;
                gapX = Math.Round(gapX / GRID_SIZE) * GRID_SIZE;

                Point sH = start;
                if (startDir == PortDirection.Right && gapX >= start.X) sH.X = Math.Min(start.X + MARGIN, gapX);
                else if (startDir == PortDirection.Left && gapX <= start.X) sH.X = Math.Max(start.X - MARGIN, gapX);
                else sH = GetShiftedPoint(start, startDir, MARGIN);

                Point eH = end;
                if (endDir == PortDirection.Left && gapX <= end.X) eH.X = Math.Max(end.X - MARGIN, gapX);
                else if (endDir == PortDirection.Right && gapX >= end.X) eH.X = Math.Min(end.X + MARGIN, gapX);
                else eH = GetShiftedPoint(end, endDir, MARGIN);

                gapCandidates.Add(new List<Point> { start, sH, new Point(gapX, sH.Y), new Point(gapX, eH.Y), eH, end });
            }

            bool hasVertGap = r1.Bottom < r2.Top || r2.Bottom < r1.Top;
            if (hasVertGap)
            {
                double gapY = r1.Bottom < r2.Top ? (r1.Bottom + r2.Top) / 2 : (r2.Bottom + r1.Top) / 2;
                gapY = Math.Round(gapY / GRID_SIZE) * GRID_SIZE;

                Point sV = start;
                if (startDir == PortDirection.Bottom && gapY >= start.Y) sV.Y = Math.Min(start.Y + MARGIN, gapY);
                else if (startDir == PortDirection.Top && gapY <= start.Y) sV.Y = Math.Max(start.Y - MARGIN, gapY);
                else sV = GetShiftedPoint(start, startDir, MARGIN);

                Point eV = end;
                if (endDir == PortDirection.Top && gapY <= end.Y) eV.Y = Math.Max(end.Y - MARGIN, gapY);
                else if (endDir == PortDirection.Bottom && gapY >= end.Y) eV.Y = Math.Min(end.Y + MARGIN, gapY);
                else eV = GetShiftedPoint(end, endDir, MARGIN);

                gapCandidates.Add(new List<Point> { start, sV, new Point(sV.X, gapY), new Point(eV.X, gapY), eV, end });
            }

            foreach (var path in gapCandidates)
                if (IsValidPath(path, r1, r2)) return SimplifyPath(path);


            // [3단계] 두 노드 전체를 감싸고 '크게 우회하기 (Perimeter Detour)'
            var detourCandidates = new List<List<Point>>();

            double minTop = Math.Min(Math.Min(r1.Top, r2.Top), Math.Min(sStub.Y, eStub.Y)) - MARGIN * 1.5;
            double maxBottom = Math.Max(Math.Max(r1.Bottom, r2.Bottom), Math.Max(sStub.Y, eStub.Y)) + MARGIN * 1.5;
            double minLeft = Math.Min(Math.Min(r1.Left, r2.Left), Math.Min(sStub.X, eStub.X)) - MARGIN * 1.5;
            double maxRight = Math.Max(Math.Max(r1.Right, r2.Right), Math.Max(sStub.X, eStub.X)) + MARGIN * 1.5;

            minTop = Math.Round(minTop / GRID_SIZE) * GRID_SIZE;
            maxBottom = Math.Round(maxBottom / GRID_SIZE) * GRID_SIZE;
            minLeft = Math.Round(minLeft / GRID_SIZE) * GRID_SIZE;
            maxRight = Math.Round(maxRight / GRID_SIZE) * GRID_SIZE;

            detourCandidates.Add(new List<Point> { start, sStub, new Point(sStub.X, minTop), new Point(eStub.X, minTop), eStub, end });
            detourCandidates.Add(new List<Point> { start, sStub, new Point(sStub.X, maxBottom), new Point(eStub.X, maxBottom), eStub, end });
            detourCandidates.Add(new List<Point> { start, sStub, new Point(minLeft, sStub.Y), new Point(minLeft, eStub.Y), eStub, end });
            detourCandidates.Add(new List<Point> { start, sStub, new Point(maxRight, sStub.Y), new Point(maxRight, eStub.Y), eStub, end });

            var validDetours = detourCandidates.Where(p => IsValidPath(p, r1, r2)).ToList();
            if (validDetours.Any())
            {
                var bestDetour = validDetours.OrderBy(p => GetPathLength(p)).First();
                return SimplifyPath(bestDetour);
            }

            return null; // 여기까지 다 실패하면 비로소 A* 알고리즘 출동
        }

        // 경로의 총 길이를 구하는 헬퍼 함수
        private static double GetPathLength(List<Point> path)
        {
            double len = 0;
            for (int i = 0; i < path.Count - 1; i++)
                len += Math.Abs(path[i].X - path[i + 1].X) + Math.Abs(path[i].Y - path[i + 1].Y);
            return len;
        }

        private static bool IsValidPath(List<Point> path, Rect r1, Rect r2)
        {
            for (int i = 0; i < path.Count - 1; i++)
            {
                Point a = path[i];
                Point b = path[i + 1];

                if (i == 0)
                {
                    if (IsSegmentHitRect(a, b, r2)) return false;
                }
                else if (i == path.Count - 2)
                {
                    if (IsSegmentHitRect(a, b, r1)) return false;
                }
                else
                {
                    if (IsSegmentHitRect(a, b, r1) || IsSegmentHitRect(a, b, r2)) return false;
                }
            }
            return true;
        }

        private static bool IsSegmentHitRect(Point a, Point b, Rect rect, double pad = COLLISION_PAD)
        {
            Rect r = rect;
            if (pad != 0) r.Inflate(pad, pad);

            bool vertical = Math.Abs(a.X - b.X) < 0.5;
            bool horizontal = Math.Abs(a.Y - b.Y) < 0.5;

            if (vertical)
            {
                double x = a.X;
                double y1 = Math.Min(a.Y, b.Y);
                double y2 = Math.Max(a.Y, b.Y);
                return x >= r.Left && x <= r.Right && y2 >= r.Top && y1 <= r.Bottom;
            }

            if (horizontal)
            {
                double y = a.Y;
                double x1 = Math.Min(a.X, b.X);
                double x2 = Math.Max(a.X, b.X);
                return y >= r.Top && y <= r.Bottom && x2 >= r.Left && x1 <= r.Right;
            }

            Rect lineBox = new Rect(a, b);
            lineBox.Inflate(pad, pad);
            return lineBox.IntersectsWith(r);
        }

        private static List<Point> SimplifyPath(List<Point> rawPath)
        {
            if (rawPath == null || rawPath.Count < 2) return rawPath ?? new List<Point>();

            var distinctPath = new List<Point>();
            foreach (var pt in rawPath)
            {
                if (distinctPath.Count == 0)
                {
                    distinctPath.Add(pt);
                }
                else
                {
                    var last = distinctPath.Last();
                    if (Math.Abs(last.X - pt.X) > 0.5 || Math.Abs(last.Y - pt.Y) > 0.5)
                    {
                        distinctPath.Add(pt);
                    }
                }
            }

            if (distinctPath.Count < 3) return distinctPath;

            var result = new List<Point> { distinctPath[0] };
            for (int i = 1; i < distinctPath.Count - 1; i++)
            {
                Point prev = result.Last();
                Point curr = distinctPath[i];
                Point next = distinctPath[i + 1];

                bool isVertical = Math.Abs(prev.X - curr.X) < 0.5 && Math.Abs(curr.X - next.X) < 0.5;
                bool isHorizontal = Math.Abs(prev.Y - curr.Y) < 0.5 && Math.Abs(curr.Y - next.Y) < 0.5;

                if (!isVertical && !isHorizontal)
                {
                    result.Add(curr);
                }
            }

            result.Add(distinctPath.Last());
            return result;
        }

        private static Point GetCornerPoint(Point from, Point to, PortDirection toDir)
        {
            if (toDir == PortDirection.Left || toDir == PortDirection.Right) return new Point(from.X, to.Y);
            return new Point(to.X, from.Y);
        }

        private static Point AlignPoint(Point anchor, Point target, PortDirection dir)
        {
            if (dir == PortDirection.Left || dir == PortDirection.Right) return new Point(target.X, anchor.Y);
            return new Point(anchor.X, target.Y);
        }

        private static bool IsRectilinear(Point a, Point b) => Math.Abs(a.X - b.X) < 0.5 || Math.Abs(a.Y - b.Y) < 0.5;

        private static IEnumerable<Point> GetNeighbors(Point p)
        {
            yield return new Point(p.X + GRID_SIZE, p.Y);
            yield return new Point(p.X - GRID_SIZE, p.Y);
            yield return new Point(p.X, p.Y + GRID_SIZE);
            yield return new Point(p.X, p.Y - GRID_SIZE);
        }

        private static PortDirection GetDirection(Point from, Point to)
        {
            if (Math.Abs(from.X - to.X) > Math.Abs(from.Y - to.Y)) return to.X > from.X ? PortDirection.Right : PortDirection.Left;
            else return to.Y > from.Y ? PortDirection.Bottom : PortDirection.Top;
        }

        private static double GetManhattanDistance(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        private static Point SnapToGrid(Point p) => new Point(Math.Round(p.X / GRID_SIZE) * GRID_SIZE, Math.Round(p.Y / GRID_SIZE) * GRID_SIZE);
        private static Point GetShiftedPoint(Point p, PortDirection dir, double dist)
        {
            switch (dir)
            {
                case PortDirection.Left: return new Point(p.X - dist, p.Y);
                case PortDirection.Right: return new Point(p.X + dist, p.Y);
                case PortDirection.Top: return new Point(p.X, p.Y - dist);
                case PortDirection.Bottom: return new Point(p.X, p.Y + dist);
                default: return p;
            }
        }
    }
}