using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 다이어그램이 그려지는 하나의 도화지(시트) 상태를 관리하는 뷰모델입니다.
    /// 노드, 그룹, 연결선 데이터를 관리하고 화면의 줌/팬(이동) 및 선택 상태를 제어합니다.
    /// </summary>
    public class SheetViewModel : ViewModelBase
    {
        #region Fields & Services
        private readonly IContainerService _containerService;
        private readonly IVolumeService _volumeService;
        private readonly IDialogService _dialogService;

        private string _title = "Sheet";
        private double _mapWidth = 2000;
        private double _mapHeight = 1500;
        private double _scale = 1.0;
        private double _offsetX = 0;
        private double _offsetY = 0;

        private NodeViewModel? _selectedNode;
        private GroupViewModel? _selectedGroup;
        private ConnectorViewModel? _selectedConnector;
        #endregion

        #region Basic Properties
        public string Title { get => _title; set => SetProperty(ref _title, value); } // 탭 등에 표시될 시트 이름
        public ConnectionProfile Profile { get; set; } // 현재 시트가 연결된 도커 접속 정보
        public IDockerService DockerService { get; set; } // 도커 엔진 통신 서비스
        public string ComposeRawYaml { get; set; } = string.Empty; // Compose import 원본 보존용
        #endregion

        #region Map Data (Collections)
        public ObservableCollection<NodeViewModel> Nodes { get; set; } = new(); // 도화지 위의 모든 노드(컨테이너, 볼륨 등)
        public ObservableCollection<ConnectorViewModel> Connectors { get; set; } = new(); // 노드들을 연결하는 모든 선
        public ObservableCollection<GroupViewModel> Groups { get; set; } = new(); // 노드들을 묶는 모든 그룹(네트워크 등)
        #endregion

        #region Map Settings & View State (Zoom/Pan)
        public double MapWidth { get => _mapWidth; set => SetProperty(ref _mapWidth, value); } // 도화지의 전체 가로 길이
        public double MapHeight { get => _mapHeight; set => SetProperty(ref _mapHeight, value); } // 도화지의 전체 세로 길이
        public double Scale { get => _scale; set => SetProperty(ref _scale, value); } // 현재 화면의 확대/축소 비율
        public double OffsetX { get => _offsetX; set => SetProperty(ref _offsetX, value); } // 화면의 가로 스크롤 위치
        public double OffsetY { get => _offsetY; set => SetProperty(ref _offsetY, value); } // 화면의 세로 스크롤 위치
        #endregion

        #region Selection Management
        public NodeViewModel? SelectedNode
        {
            get => _selectedNode;
            set
            {
                var oldNode = _selectedNode;
                if (SetProperty(ref _selectedNode, value))
                {
                    if (oldNode != null) oldNode.IsSelected = false;
                    if (_selectedNode != null)
                    {
                        _selectedNode.IsSelected = true;
                        _selectedNode.RefreshConnections();

                        SelectedGroup = null;
                        SelectedConnector = null;
                    }
                }
            }
        }

        public GroupViewModel? SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (SetProperty(ref _selectedGroup, value))
                {
                    if (_selectedGroup != null)
                    {
                        SelectedNode = null;
                        SelectedConnector = null;
                    }
                }
            }
        }

        public ConnectorViewModel? SelectedConnector
        {
            get => _selectedConnector;
            set
            {
                var oldConnector = _selectedConnector;
                if (SetProperty(ref _selectedConnector, value))
                {
                    if (oldConnector != null) oldConnector.IsSelected = false;
                    if (_selectedConnector != null)
                    {
                        _selectedConnector.IsSelected = true;
                        SelectedNode = null;
                        SelectedGroup = null;
                    }
                }
            }
        }
        #endregion

        #region Constructor
        /// <summary>
        /// 시트 뷰모델을 초기화하고 필요한 서비스들을 주입받습니다.
        /// </summary>
        public SheetViewModel(string title, ConnectionProfile profile, IDockerService dockerService, IDialogService dialogService)
        {
            Title = title;
            Profile = profile;
            DockerService = dockerService;

            _containerService = dockerService;
            _volumeService = dockerService;
            _dialogService = dialogService;
        }
        #endregion

        #region Node Methods
        /// <summary>
        /// 지정된 좌표에 새로운 도커 노드(컨테이너, 볼륨, 인터넷)를 생성하여 도화지에 추가합니다.
        /// </summary>
        public void CreateNodeAt(DockerNodeBase nodeModel, double x, double y)
        {
            NodeType determinedType = nodeModel switch
            {
                DockerVolume => NodeType.Volume,
                DockerInternet => NodeType.Internet,
                _ => NodeType.Container
            };

            var nodeVm = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = nodeModel.Name,
                Type = determinedType,
                ParentSheet = this,
                X = x,
                Y = y,
                Width = 160,
                Height = 80
            };

            if (nodeModel is DockerContainer container)
            {
                nodeVm.ImageName = container.Image;
                nodeVm.StatusColor = container.StateColor;
                nodeVm.ContainerId = container.Id;
                nodeVm.IsDockerConnected = !string.IsNullOrWhiteSpace(container.Id);
            }
            else if (nodeModel is DockerVolume volume)
            {
                nodeVm.StatusColor = "#E67E22";
                nodeVm.ContainerId = "";
                nodeVm.IsDockerConnected = true;
            }
            else if (nodeModel is DockerInternet internet)
            {
                nodeVm.StatusColor = "#E67E22";
                nodeVm.ContainerId = "";
                nodeVm.IsDockerConnected = true;
            }

            Nodes.Add(nodeVm);
        }

        /// <summary>
        /// 특정 노드를 도화지에서 삭제하고, 연결된 선과 소속된 그룹 정보를 함께 정리합니다.
        /// </summary>
        public async Task RemoveNodeAsync(NodeViewModel node)
        {
            if (node == null) return;

            var relatedConnectors = Connectors.Where(c => c.Source == node || c.Target == node).ToList();
            foreach (var c in relatedConnectors)
            {
                Connectors.Remove(c);
            }

            foreach (var g in Groups.ToList())
            {
                if (g.ContainedNodes.Contains(node))
                {
                    await g.RemoveNodeAsync(node);
                }
            }

            Nodes.Remove(node);
        }
        #endregion

        #region Connection Methods
        /// <summary>
        /// 두 노드 사이에 새로운 연결선을 추가합니다. 중복 연결은 무시됩니다.
        /// </summary>
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
        #endregion

        #region Group Methods
        /// <summary>
        /// 새로운 그룹(네트워크 묶음 등)을 도화지에 추가합니다.
        /// </summary>
        public void AddGroup(GroupViewModel group)
        {
            group.ParentSheet = this;
            Groups.Add(group);
        }

        /// <summary>
        /// 특정 좌표(영역)가 포함되는 모든 그룹 목록을 찾아 반환합니다.
        /// </summary>
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

        /// <summary>
        /// 도화지 위의 노드 좌표를 다시 계산하여, 특정 그룹 영역 안에 들어온 노드는 편입시키고 나간 노드는 제외시킵니다.
        /// </summary>
        public async Task RefreshGroupContainmentAsync(GroupViewModel group)
        {
            Rect groupRect = new Rect(group.X, group.Y, group.Width, group.Height);

            foreach (var node in Nodes)
            {
                Point nodeCenter = new Point(node.X + node.Width / 2, node.Y + node.Height / 2);

                if (groupRect.Contains(nodeCenter))
                {
                    await group.AddNodeAsync(node);
                }
                else
                {
                    await group.RemoveNodeAsync(node);
                }
            }
        }

        /// <summary>
        /// 그룹의 크기(넓이)를 기준으로 큰 그룹이 뒤로(ZIndex 낮음), 작은 그룹이 앞으로(ZIndex 높음) 오도록 화면 겹침 순서를 업데이트합니다.
        /// </summary>
        public void UpdateGroupLayering()
        {
            var sortedGroups = Groups.OrderByDescending(g => g.Width * g.Height).ToList();
            for (int i = 0; i < sortedGroups.Count; i++)
            {
                sortedGroups[i].ZIndex = i;
            }
        }
        #endregion
    }
}
