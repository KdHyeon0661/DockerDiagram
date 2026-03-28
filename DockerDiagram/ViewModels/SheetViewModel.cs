using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public class SheetViewModel : ViewModelBase
    {
        public ConnectionProfile Profile { get; set; }
        public IDockerService DockerService { get; set; }

        private string _title = "Sheet";
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        private readonly IContainerService _containerService;
        private readonly IVolumeService _volumeService;
        private readonly IDialogService _dialogService;

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

        private NodeViewModel? _selectedNode;
        public NodeViewModel? SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (_selectedNode != value)
                {
                    if (_selectedNode != null) _selectedNode.IsSelected = false;
                    _selectedNode = value;
                    if (_selectedNode != null)
                    {
                        _selectedNode.IsSelected = true;
                        _selectedNode.RefreshConnections();
                    }
                    OnPropertyChanged();
                    if (_selectedNode != null) { SelectedGroup = null; SelectedConnector = null; }
                }
            }
        }

        private GroupViewModel? _selectedGroup;
        public GroupViewModel? SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (_selectedGroup != value)
                {
                    _selectedGroup = value;
                    OnPropertyChanged();
                    if (_selectedGroup != null) { SelectedNode = null; SelectedConnector = null; }
                }
            }
        }

        private ConnectorViewModel? _selectedConnector;
        public ConnectorViewModel? SelectedConnector
        {
            get => _selectedConnector;
            set
            {
                if (_selectedConnector != value)
                {
                    if (_selectedConnector != null) _selectedConnector.IsSelected = false;
                    _selectedConnector = value;
                    if (_selectedConnector != null) _selectedConnector.IsSelected = true;
                    OnPropertyChanged();
                    if (_selectedConnector != null) { SelectedNode = null; SelectedGroup = null; }
                }
            }
        }

        // =================================================================
        // ★ [생성자 수정 부분] Profile과 DockerService를 받아서 초기화합니다.
        // =================================================================
        public SheetViewModel(string title, ConnectionProfile profile, IDockerService dockerService, IDialogService dialogService)
        {
            Title = title;
            Profile = profile;
            DockerService = dockerService;

            _containerService = dockerService;
            _volumeService = dockerService;
            _dialogService = dialogService;
        }

        public void CreateContainerAt(DockerContainer container, double x, double y)
        {
            var nodeVm = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = container.Name,
                ParentSheet = this,
                ImageName = container.Image,
                StatusColor = container.StateColor,
                Type = NodeType.Container,
                ContainerId = container.Id,
                X = x,
                Y = y,
                Width = 160,
                Height = 80
            };
            Nodes.Add(nodeVm);
        }

        public void CreateVolumeAt(DockerVolume volume, double x, double y)
        {
            var nodeVm = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = volume.Name,
                ParentSheet = this,
                StatusColor = "#E67E22",
                Type = NodeType.Volume,
                ContainerId = "",
                X = x,
                Y = y,
                Width = 160,
                Height = 80
            };
            Nodes.Add(nodeVm);
        }

        public void CreateInternetAt(DockerInternet internet, double x, double y)
        {
            var nodeVm = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = internet.Name,
                ParentSheet = this,
                StatusColor = "#E67E22",
                Type = NodeType.Internet,
                ContainerId = "",
                X = x,
                Y = y,
                Width = 160,
                Height = 80
            };
            Nodes.Add(nodeVm);
        }

        public void AddConnection(NodeViewModel source, NodeViewModel target, PortDirection sourceDir, PortDirection targetDir)
        {
            if (source == target) return;
            if (!Connectors.Any(c => (c.Source == source && c.Target == target) || (c.Source == target && c.Target == source)))
            {
                Connectors.Add(new ConnectorViewModel(source, target, sourceDir, targetDir, _dialogService));
                source.RefreshConnections();
                target.RefreshConnections();
            }
        }

        public void AddGroup(GroupViewModel group)
        {
            group.ParentSheet = this;
            Groups.Add(group);
        }

        public void RemoveNode(NodeViewModel node)
        {
            var relatedConnectors = Connectors.Where(c => c.Source == node || c.Target == node).ToList();
            foreach (var c in relatedConnectors) Connectors.Remove(c);
            foreach (var g in Groups) if (g.ContainedNodes.Contains(node)) g.RemoveNode(node);
            Nodes.Remove(node);
        }

        // =========================================================================
        // 기타 기존 유지 헬퍼 메서드들
        // =========================================================================
        public List<GroupViewModel> FindGroupsAt(double x, double y, double w, double h)
        {
            var foundGroups = new List<GroupViewModel>();
            double centerX = x + w / 2;
            double centerY = y + h / 2;

            foreach (var group in Groups)
            {
                if (centerX >= group.X && centerX <= group.X + group.Width &&
                    centerY >= group.Y && centerY <= group.Y + group.Height)
                {
                    foundGroups.Add(group);
                }
            }
            return foundGroups;
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

        public void UpdateGroupLayering()
        {
            var sortedGroups = Groups.OrderByDescending(g => g.Width * g.Height).ToList();
            for (int i = 0; i < sortedGroups.Count; i++)
            {
                sortedGroups[i].ZIndex = i;
            }
        }
    }
}