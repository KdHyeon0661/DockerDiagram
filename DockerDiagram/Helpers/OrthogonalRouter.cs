using System.Windows;
using System.Windows.Media;

namespace DockerDiagram.Helpers
{
    public enum PortDirection { Top, Bottom, Left, Right, None }

    public static class OrthogonalRouter
    {
        private const double MARGIN = 20;   // 노드로부터 떨어지는 거리 (Stub)
        private const int GRID_SIZE = 10;   // 격자 크기
        private const double COLLISION_PAD = 1.0;
        public static PointCollection GetRoute(Point start, PortDirection startDir,
                                             Point end, PortDirection endDir,
                                             Rect sourceRect, Rect targetRect)
        {
            PointCollection finalPath = new PointCollection();

            // 1. [Method 1] 단순 Z자 경로 시도 (충돌 체크 엄격하게 수행)
            // 성공하면 바로 리턴, 실패하면 아래 A* 로직으로 넘어감
            var simplePath = TryGetSimpleRoute(start, startDir, end, endDir, sourceRect, targetRect);
            if (simplePath != null)
            {
                return new PointCollection(simplePath);
            }

            // 2. A* 준비: 시작/끝점을 안전한 격자 위로 이동
            Point startStub = GetShiftedPoint(start, startDir, MARGIN);
            Point endStub = GetShiftedPoint(end, endDir, MARGIN);

            Point startNode = SnapToGrid(startStub);
            Point endNode = SnapToGrid(endStub);

            // 3. A* 탐색 (장애물을 피해서 우회)
            List<Point> pathPoints = FindPathAStar(startNode, endNode, sourceRect, targetRect, startDir);

            // 4. 경로 조립
            finalPath.Add(start);

            // Start -> A* 첫 점 보정
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

            // 5. 마지막 구간 직교화 (화살표 휨 방지)
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

            return finalPath;
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

            // 탐색 영역 설정
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

            // 장애물 판정 복구
            // 지난번에 -15 했던 것을 -2 정도로 줄여서, 
            // 시각적으로 겹치지 않게(Solid) 만들되, 부동소수점 오차만 허용합니다.
            Rect block1 = obstacle1; block1.Inflate(2, 2);
            Rect block2 = obstacle2; block2.Inflate(2, 2);

            int loopCount = 0;
            const int MAX_LOOPS = 4000;

            while (openList.Count > 0)
            {
                if (loopCount++ > MAX_LOOPS) break; // 무한루프 방지

                var current = openList.OrderBy(n => n.F).First();

                // 도착 확인
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

                    // 목적지 근처(1.5px)가 아니라면, 장애물 내부 진입 절대 불가
                    bool isTargetZone = (Math.Abs(neighbor.X - targetSnap.X) < 1.5 && Math.Abs(neighbor.Y - targetSnap.Y) < 1.5);

                    if (!isTargetZone)
                    {
                        // 벽을 통과하지 못하게 막음
                        if (block1.Contains(neighbor) || block2.Contains(neighbor)) continue;
                    }

                    double moveCost = GRID_SIZE;
                    PortDirection newDir = GetDirection(current.Point, neighbor);

                    // 방향 전환 페널티 (직진 선호)
                    if (current.ArrivalDirection != PortDirection.None && current.ArrivalDirection != newDir)
                        moveCost += 5;

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

            return new List<Point>(); // 실패 시 빈 리스트
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

        // --- Method 1: Simple Route (Collision Check 강화) ---
        private static List<Point>? TryGetSimpleRoute(Point start, PortDirection startDir, Point end, PortDirection endDir, Rect r1, Rect r2)
        {
            if (GetManhattanDistance(start, end) < MARGIN * 3) return null;

            List<Point> path = new List<Point> { start };

            // 중간 지점 계산 (격자 맞춤)
            double midX = Math.Round((start.X + end.X) / 2 / GRID_SIZE) * GRID_SIZE;
            double midY = Math.Round((start.Y + end.Y) / 2 / GRID_SIZE) * GRID_SIZE;

            Point p1, p2;

            // 1. 좌우 연결 시도 (ㅡ ㄹ ㅡ 형태)
            if ((startDir == PortDirection.Right && endDir == PortDirection.Left) ||
                (startDir == PortDirection.Left && endDir == PortDirection.Right))
            {
                p1 = new Point(midX, start.Y);
                p2 = new Point(midX, end.Y);
            }
            // 2. 상하 연결 시도 (ㅣ Z ㅣ 형태)
            else if ((startDir == PortDirection.Bottom && endDir == PortDirection.Top) ||
                     (startDir == PortDirection.Top && endDir == PortDirection.Bottom))
            {
                p1 = new Point(start.X, midY);
                p2 = new Point(end.X, midY);
            }
            else
            {
                // 방향이 엇갈리면(예: Right -> Top) 단순 경로 포기하고 A*로 넘김
                return null;
            }

            // ★ [핵심] 선분 충돌 검사 (Inflate 없이 엄격하게)
            // 만들어진 Z자 경로가 Source나 Target 박스를 조금이라도 건드리면 즉시 포기
            if (IsSegmentHitRect(start, p1, r2) ||
                IsSegmentHitRect(p1, p2, r1) ||
                IsSegmentHitRect(p1, p2, r2) ||
                IsSegmentHitRect(p2, end, r1))
            {
                return null; // A*로 우회
            }

            path.Add(p1);
            path.Add(p2);
            path.Add(end);
            return SimplifyPath(path);
        }

        // --- 유틸리티 (Strict Collision Check) ---
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

                return x >= r.Left && x <= r.Right &&
                       y2 >= r.Top && y1 <= r.Bottom;
            }

            if (horizontal)
            {
                double y = a.Y;
                double x1 = Math.Min(a.X, b.X);
                double x2 = Math.Max(a.X, b.X);

                return y >= r.Top && y <= r.Bottom &&
                       x2 >= r.Left && x1 <= r.Right;
            }

            // 혹시 모를 비정렬 선분 fallback
            Rect lineBox = new Rect(a, b);
            lineBox.Inflate(pad, pad);
            return lineBox.IntersectsWith(r);
        }

        private static List<Point> SimplifyPath(List<Point> rawPath)
        {
            if (rawPath.Count < 3) return rawPath;
            var simplified = new List<Point> { rawPath[0] };
            for (int i = 1; i < rawPath.Count - 1; i++)
            {
                Point prev = rawPath[i - 1];
                Point curr = rawPath[i];
                Point next = rawPath[i + 1];
                bool isVertical = (Math.Abs(prev.X - curr.X) < 0.5 && Math.Abs(curr.X - next.X) < 0.5);
                bool isHorizontal = (Math.Abs(prev.Y - curr.Y) < 0.5 && Math.Abs(curr.Y - next.Y) < 0.5);
                if (!isVertical && !isHorizontal) simplified.Add(curr);
            }
            simplified.Add(rawPath.Last());
            return simplified;
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