using System.Collections.ObjectModel;
using System.Windows;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public class SheetViewModel : ViewModelBase
    {
        private string _title = "Sheet";
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        // --- 맵 데이터 ---
        public ObservableCollection<NodeViewModel> Nodes { get; set; } = new();
        public ObservableCollection<ConnectorViewModel> Connectors { get; set; } = new();
        public ObservableCollection<GroupViewModel> Groups { get; set; } = new();

        // --- 맵 설정 ---
        private double _mapWidth = 2000;
        public double MapWidth { get => _mapWidth; set { _mapWidth = value; OnPropertyChanged(); } }

        private double _mapHeight = 1500;
        public double MapHeight { get => _mapHeight; set { _mapHeight = value; OnPropertyChanged(); } }

        // --- 줌/팬 상태 ---
        private double _scale = 1.0;
        public double Scale { get => _scale; set { _scale = value; OnPropertyChanged(); } }

        private double _offsetX = 0;
        public double OffsetX { get => _offsetX; set { _offsetX = value; OnPropertyChanged(); } }

        private double _offsetY = 0;
        public double OffsetY { get => _offsetY; set { _offsetY = value; OnPropertyChanged(); } }

        public SheetViewModel(string title)
        {
            Title = title;
        }

        public void CreateNodeAt(DockerContainer container, double x, double y)
        {
            Nodes.Add(new NodeViewModel
            {
                Name = container.Name,
                ImageName = container.Image,
                StatusColor = container.StateColor,
                PortInfo = container.Ports,
                Type = container.Type,
                ContainerId = container.Id, // ID 저장 필수
                X = x,
                Y = y,
                Width = 160,
                Height = 80
            });
        }

        public void AddConnection(NodeViewModel source, NodeViewModel target, PortDirection sourceDir, PortDirection targetDir)
        {
            if (source == target) return;
            bool exists = Connectors.Any(c => (c.Source == source && c.Target == target) || (c.Source == target && c.Target == source));
            if (!exists) Connectors.Add(new ConnectorViewModel(source, target, sourceDir, targetDir));
        }

        // 그룹 추가 헬퍼 메서드
        // MainWindow.xaml.cs에서 Groups.Add 대신 이 메서드를 사용하면 ParentSheet가 자동 연결됩니다.
        public void AddGroup(GroupViewModel group)
        {
            group.ParentSheet = this; // 그룹이 시트의 연결 정보(Connectors)를 읽을 수 있게 함
            Groups.Add(group);
        }

        // [최종 알고리즘] 군집 분리 + 기하학적 중앙 정렬 + 화면 전체 중앙 배치
        public void AutoLayout()
        {
            if (Nodes.Count == 0) return;

            // 그룹 정보는 레이아웃 시 초기화 (선택 사항)
            // Groups.Clear(); 

            // 1. 군집(Cluster) 분리
            var clusters = GetConnectedClusters();

            // 2. 각 군집별 로컬 레이아웃 수행
            double currentY = 0;
            var clusterBounds = new List<Rect>();

            foreach (var cluster in clusters)
            {
                // 이 클러스터를 (0,0) 기준으로 예쁘게 정렬하고 사이즈를 반환받음
                Size size = LayoutCluster(cluster);

                // 클러스터의 바운딩 박스 저장 (나중에 전체 이동용)
                clusterBounds.Add(new Rect(0, currentY, size.Width, size.Height));

                // 실제 노드들을 해당 위치로 이동 (임시 배치)
                foreach (var node in cluster)
                {
                    node.Y += currentY;
                }

                // 다음 클러스터는 이 밑에 배치 (간격 100)
                currentY += size.Height + 100;
            }

            // 3. 전체 다이어그램의 중앙을 화면 중앙으로 이동 (Global Centering)
            if (clusterBounds.Count > 0)
            {
                // 전체 노드가 차지하는 영역 계산
                double totalMinX = Nodes.Min(n => n.X);
                double totalMinY = Nodes.Min(n => n.Y);
                double totalMaxX = Nodes.Max(n => n.X + n.Width);
                double totalMaxY = Nodes.Max(n => n.Y + n.Height);

                double contentWidth = totalMaxX - totalMinX;
                double contentHeight = totalMaxY - totalMinY;

                // 맵의 중앙 좌표
                double centerX = MapWidth / 2;
                double centerY = MapHeight / 2;

                // 이동해야 할 오프셋 계산 (화면중앙 - 콘텐츠중앙)
                double offsetX = centerX - (totalMinX + contentWidth / 2);
                double offsetY = centerY - (totalMinY + contentHeight / 2);

                // 모든 노드 일괄 이동
                foreach (var node in Nodes)
                {
                    node.X += offsetX;
                    node.Y += offsetY;
                }
            }

            // 4. 연결선 방향 강제 초기화 (Left ↔ Right)
            foreach (var conn in Connectors)
            {
                // 소스(부모)가 타겟(자식)보다 왼쪽에 있으면: Source(Right) -> Target(Left)
                if (conn.Source.X <= conn.Target.X)
                {
                    conn.UpdateConnection(conn.Source, PortDirection.Right, conn.Target, PortDirection.Left);
                }
                else
                {
                    conn.UpdateConnection(conn.Source, PortDirection.Left, conn.Target, PortDirection.Right);
                }
            }
        }

        // [Helper] Sugiyama 방식 레이아웃 (단일 클러스터용)
        private Size LayoutCluster(List<NodeViewModel> nodes)
        {
            if (nodes.Count == 0) return new Size(0, 0);

            // 1. 그래프 구성
            var adj = new Dictionary<NodeViewModel, List<NodeViewModel>>(); // 부모 -> 자식
            var inDegree = new Dictionary<NodeViewModel, int>();

            foreach (var n in nodes) { adj[n] = new List<NodeViewModel>(); inDegree[n] = 0; }
            foreach (var conn in Connectors)
            {
                if (nodes.Contains(conn.Source) && nodes.Contains(conn.Target))
                {
                    if (!adj[conn.Source].Contains(conn.Target))
                    {
                        adj[conn.Source].Add(conn.Target);
                        inDegree[conn.Target]++;
                    }
                }
            }

            // 2. 레벨 계산 (Rank)
            var levels = new Dictionary<NodeViewModel, int>();
            var queue = new Queue<NodeViewModel>();

            foreach (var n in nodes)
            {
                if (inDegree[n] == 0) { levels[n] = 0; queue.Enqueue(n); }
            }

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                foreach (var child in adj[curr])
                {
                    inDegree[child]--;
                    if (inDegree[child] == 0)
                    {
                        levels[child] = levels[curr] + 1;
                        queue.Enqueue(child);
                    }
                }
            }

            // 사이클 노드 처리
            int maxLevel = levels.Count > 0 ? levels.Values.Max() : 0;
            foreach (var n in nodes) if (!levels.ContainsKey(n)) levels[n] = maxLevel + 1;
            if (levels.Count > 0) maxLevel = levels.Values.Max();

            // 3. 레이어 구성
            var layers = new List<List<NodeViewModel>>();
            for (int i = 0; i <= maxLevel; i++) layers.Add(new List<NodeViewModel>());
            foreach (var kvp in levels) layers[kvp.Value].Add(kvp.Key);

            // 4. 좌표 할당
            // X축: 레벨에 따라 고정
            // Y축: Bottom-Up 방식으로 부모를 자식들의 '중간(Center)'에 배치

            double nodeHeight = 80;
            double verticalGap = 50;
            double levelGap = 250;

            // 4-1. 초기 Y값 할당 (각 레이어 별로 겹치지 않게)
            foreach (var layer in layers)
            {
                double y = 0;
                foreach (var node in layer)
                {
                    node.Y = y;
                    y += nodeHeight + verticalGap;
                }
            }

            // 4-2. 밸런싱 (Bottom-Up)
            // 가장 깊은 레벨부터 거꾸로 올라오면서 부모 위치 조정
            for (int l = maxLevel - 1; l >= 0; l--)
            {
                foreach (var parent in layers[l])
                {
                    var children = adj[parent].Where(c => levels[c] == l + 1).ToList();
                    if (children.Count > 0)
                    {
                        // 기하학적 중앙 (Geometric Center)
                        double minChildY = children.Min(c => c.Y);
                        double maxChildY = children.Max(c => c.Y);
                        parent.Y = (minChildY + maxChildY) / 2;
                    }
                }
                // 이동 후 겹침 방지 (필수)
                ResolveOverlaps(layers[l], nodeHeight, verticalGap);
            }

            // 5. 최종 X 좌표 적용 및 크기 계산
            double maxX = 0;
            double maxY = 0;

            for (int l = 0; l <= maxLevel; l++)
            {
                double x = l * levelGap;
                foreach (var node in layers[l])
                {
                    node.X = x;
                    maxX = Math.Max(maxX, node.X + node.Width);
                    maxY = Math.Max(maxY, node.Y + node.Height);
                }
            }

            // 전체 노드를 (0,0)에 딱 붙게 오프셋 조정 (빈 공간 제거)
            double minY = nodes.Min(n => n.Y);
            foreach (var n in nodes) n.Y -= minY;

            return new Size(maxX, maxY - minY);
        }

        // [Helper] 겹침 방지
        private void ResolveOverlaps(List<NodeViewModel> layer, double height, double gap)
        {
            if (layer.Count <= 1) return;
            layer.Sort((a, b) => a.Y.CompareTo(b.Y));

            for (int i = 1; i < layer.Count; i++)
            {
                double prevBottom = layer[i - 1].Y + height + gap;
                if (layer[i].Y < prevBottom)
                {
                    layer[i].Y = prevBottom;
                }
            }
        }

        // 군집 탐색 (BFS)
        private List<List<NodeViewModel>> GetConnectedClusters()
        {
            var clusters = new List<List<NodeViewModel>>();
            var visited = new HashSet<NodeViewModel>();
            var adj = new Dictionary<NodeViewModel, List<NodeViewModel>>();

            foreach (var n in Nodes) adj[n] = new List<NodeViewModel>();
            foreach (var c in Connectors)
            {
                // 양방향 연결 (그래프 탐색용)
                adj[c.Source].Add(c.Target);
                adj[c.Target].Add(c.Source);
            }

            foreach (var node in Nodes)
            {
                if (!visited.Contains(node))
                {
                    var cluster = new List<NodeViewModel>();
                    var q = new Queue<NodeViewModel>();
                    q.Enqueue(node);
                    visited.Add(node);
                    cluster.Add(node);

                    while (q.Count > 0)
                    {
                        var curr = q.Dequeue();
                        foreach (var neighbor in adj[curr])
                        {
                            if (!visited.Contains(neighbor))
                            {
                                visited.Add(neighbor);
                                cluster.Add(neighbor);
                                q.Enqueue(neighbor);
                            }
                        }
                    }
                    clusters.Add(cluster);
                }
            }
            return clusters;
        }

        public GroupViewModel? FindGroupAt(double x, double y, double w, double h)
        {
            // 노드의 중심점이 그룹 안에 들어오면 인정
            double centerX = x + w / 2;
            double centerY = y + h / 2;

            foreach (var group in Groups)
            {
                if (centerX >= group.X && centerX <= group.X + group.Width &&
                    centerY >= group.Y && centerY <= group.Y + group.Height)
                {
                    return group;
                }
            }
            return null;
        }

        public void RefreshGroupContainment(GroupViewModel group)
        {
            // 그룹의 현재 영역 (Rect)
            Rect groupRect = new Rect(group.X, group.Y, group.Width, group.Height);

            foreach (var node in Nodes)
            {
                // 노드의 중심점 (Center Point) 계산
                Point nodeCenter = new Point(node.X + node.Width / 2, node.Y + node.Height / 2);

                // 중심점이 그룹 박스 안에 포함되는가?
                if (groupRect.Contains(nodeCenter))
                {
                    group.AddNode(node); // 합류
                }
                else
                {
                    group.RemoveNode(node); // 이탈 (혹시 들어있었다면)
                }
            }
        }
    }
}