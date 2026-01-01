using System.Collections.ObjectModel;
using System.Windows;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System.Collections.Generic;
using System.Linq;

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

        // ★ [DI] 서비스 주입 필드
        private readonly IDockerService _dockerService;
        private readonly IDialogService _dialogService; // 추가됨 (자식에게 넘겨주기 위해 필요)

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

        // ★ [DI] 생성자 수정: IDialogService 추가
        public SheetViewModel(string title, IDockerService dockerService, IDialogService dialogService)
        {
            Title = title;
            _dockerService = dockerService;
            _dialogService = dialogService;
        }

        public void CreateNodeAt(DockerContainer container, double x, double y)
        {
            // ★ [DI] 자식(NodeViewModel) 생성 시 서비스 전달
            Nodes.Add(new NodeViewModel(_dockerService, _dialogService)
            {
                Name = container.Name,
                ImageName = container.Image,
                StatusColor = container.StateColor,
                Type = container.Type,
                ContainerId = container.Id,
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

            // ★ [DI] 자식(ConnectorViewModel) 생성 시 서비스 전달
            // (ConnectorViewModel도 IP 설정 시 MessageBox를 쓰므로 dialogService가 필요합니다)
            if (!exists)
            {
                Connectors.Add(new ConnectorViewModel(source, target, sourceDir, targetDir, _dockerService, _dialogService));
            }
        }

        public void AddGroup(GroupViewModel group)
        {
            group.ParentSheet = this;
            Groups.Add(group);
        }

        // ... (AutoLayout, LayoutCluster, GetConnectedClusters 등 나머지 로직은 변경 없음) ...
        // [기존 코드 그대로 유지]
        public void AutoLayout()
        {
            if (Nodes.Count == 0) return;

            var clusters = GetConnectedClusters();
            double currentY = 0;
            var clusterBounds = new List<Rect>();

            foreach (var cluster in clusters)
            {
                Size size = LayoutCluster(cluster);
                clusterBounds.Add(new Rect(0, currentY, size.Width, size.Height));

                foreach (var node in cluster)
                {
                    node.Y += currentY;
                }
                currentY += size.Height + 100;
            }

            if (clusterBounds.Count > 0)
            {
                double totalMinX = Nodes.Min(n => n.X);
                double totalMinY = Nodes.Min(n => n.Y);
                double totalMaxX = Nodes.Max(n => n.X + n.Width);
                double totalMaxY = Nodes.Max(n => n.Y + n.Height);

                double contentWidth = totalMaxX - totalMinX;
                double contentHeight = totalMaxY - totalMinY;

                double centerX = MapWidth / 2;
                double centerY = MapHeight / 2;

                double offsetX = centerX - (totalMinX + contentWidth / 2);
                double offsetY = centerY - (totalMinY + contentHeight / 2);

                foreach (var node in Nodes)
                {
                    node.X += offsetX;
                    node.Y += offsetY;
                }
            }

            foreach (var conn in Connectors)
            {
                if (conn.Source.X <= conn.Target.X)
                    conn.UpdateConnection(conn.Source, PortDirection.Right, conn.Target, PortDirection.Left);
                else
                    conn.UpdateConnection(conn.Source, PortDirection.Left, conn.Target, PortDirection.Right);
            }
        }

        private Size LayoutCluster(List<NodeViewModel> nodes)
        {
            if (nodes.Count == 0) return new Size(0, 0);

            var adj = new Dictionary<NodeViewModel, List<NodeViewModel>>();
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

            int maxLevel = levels.Count > 0 ? levels.Values.Max() : 0;
            foreach (var n in nodes) if (!levels.ContainsKey(n)) levels[n] = maxLevel + 1;
            if (levels.Count > 0) maxLevel = levels.Values.Max();

            var layers = new List<List<NodeViewModel>>();
            for (int i = 0; i <= maxLevel; i++) layers.Add(new List<NodeViewModel>());
            foreach (var kvp in levels) layers[kvp.Value].Add(kvp.Key);

            double nodeHeight = 80;
            double verticalGap = 50;
            double levelGap = 250;

            foreach (var layer in layers)
            {
                double y = 0;
                foreach (var node in layer)
                {
                    node.Y = y;
                    y += nodeHeight + verticalGap;
                }
            }

            for (int l = maxLevel - 1; l >= 0; l--)
            {
                foreach (var parent in layers[l])
                {
                    var children = adj[parent].Where(c => levels[c] == l + 1).ToList();
                    if (children.Count > 0)
                    {
                        double minChildY = children.Min(c => c.Y);
                        double maxChildY = children.Max(c => c.Y);
                        parent.Y = (minChildY + maxChildY) / 2;
                    }
                }
                ResolveOverlaps(layers[l], nodeHeight, verticalGap);
            }

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

            double minY = nodes.Min(n => n.Y);
            foreach (var n in nodes) n.Y -= minY;

            return new Size(maxX, maxY - minY);
        }

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

        private List<List<NodeViewModel>> GetConnectedClusters()
        {
            var clusters = new List<List<NodeViewModel>>();
            var visited = new HashSet<NodeViewModel>();
            var adj = new Dictionary<NodeViewModel, List<NodeViewModel>>();

            foreach (var n in Nodes) adj[n] = new List<NodeViewModel>();
            foreach (var c in Connectors)
            {
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
            Rect groupRect = new Rect(group.X, group.Y, group.Width, group.Height);

            foreach (var node in Nodes)
            {
                Point nodeCenter = new Point(node.X + node.Width / 2, node.Y + node.Height / 2);
                if (groupRect.Contains(nodeCenter)) group.AddNode(node);
                else group.RemoveNode(node);
            }
        }
    }
}