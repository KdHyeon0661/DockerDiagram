using DockerDiagram.Diagram;
using DockerDiagram.Contracts;
using DockerDiagram.Common;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    internal sealed class ConnectorCollection : ObservableCollection<ConnectorViewModel>
    {
        protected override void InsertItem(int index, ConnectorViewModel item)
        {
            if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));
            item.Attach();
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            this[index].Detach();
            base.RemoveItem(index);
        }

        protected override void SetItem(int index, ConnectorViewModel item)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));

            ConnectorViewModel previous = this[index];
            if (ReferenceEquals(previous, item)) return;

            previous.Detach();
            item.Attach();
            base.SetItem(index, item);
        }

        protected override void ClearItems()
        {
            foreach (ConnectorViewModel connector in this)
                connector.Detach();
            base.ClearItems();
        }
    }

    /// <summary>
    /// 다이어그램이 그려지는 하나의 도화지(시트) 상태를 관리하는 뷰모델입니다.
    /// 노드, 그룹, 연결선 데이터를 관리하고 화면의 줌/팬(이동) 및 선택 상태를 제어합니다.
    /// </summary>
    public class SheetViewModel : ViewModelBase
    {
        private int _layoutUpdateDepth;
        public bool IsLayoutUpdating => _layoutUpdateDepth > 0;

        public IDisposable BeginLayoutUpdate()
        {
            _layoutUpdateDepth++;
            return new LayoutUpdateScope(this);
        }

        private void EndLayoutUpdate()
        {
            if (_layoutUpdateDepth == 0) return;
            _layoutUpdateDepth--;
            if (_layoutUpdateDepth == 0)
            {
                foreach (ConnectorViewModel connector in Connectors)
                    connector.RefreshRoute();
            }
        }

        private sealed class LayoutUpdateScope : IDisposable
        {
            private SheetViewModel? _owner;

            public LayoutUpdateScope(SheetViewModel owner) => _owner = owner;

            public void Dispose()
            {
                _owner?.EndLayoutUpdate();
                _owner = null;
            }
        }

        #region Fields & Services
        private readonly IContainerService _containerService;
        private readonly IVolumeService _volumeService;
        private readonly IDialogService _dialogService;

        private string _title = "Sheet";
        private double _mapWidth = 5000;
        private double _mapHeight = 3000;
        private double _scale = 1.2;
        private double _offsetX = 0;
        private double _offsetY = 0;
        private bool _hasViewportCenter;
        private double _viewportCenterX;
        private double _viewportCenterY;
        private bool _isRuntimeUnavailable;
        private string _runtimeStatusMessage = string.Empty;

        private NodeViewModel? _selectedNode;
        private GroupViewModel? _selectedGroup;
        private ConnectorViewModel? _selectedConnector;
        #endregion

        #region Basic Properties
        public string Title { get => _title; set => SetProperty(ref _title, value); } // 탭 등에 표시될 시트 이름
        public ConnectionProfile Profile { get; set; } // 현재 시트가 연결된 도커 접속 정보
        public IDockerService DockerService { get; set; } // 도커 엔진 통신 서비스
        public RuntimeKind RuntimeKind { get; set; } = RuntimeKind.DockerEngine; // 현재 시트의 런타임 모델
        public string RuntimeLabel => RuntimeKind switch
        {
            RuntimeKind.DockerEngine => "Docker",
            RuntimeKind.DockerSwarm => "Swarm",
            RuntimeKind.Kubernetes => "Kubernetes",
            _ => RuntimeKind.ToString()
        };
        public string ComposeRawYaml { get; set; } = string.Empty; // Compose import 원본 보존용
        public bool IsRuntimeUnavailable
        {
            get => _isRuntimeUnavailable;
            set
            {
                if (SetProperty(ref _isRuntimeUnavailable, value))
                    NotifyRuntimeAvailabilityChanged();
            }
        }

        public string RuntimeStatusMessage
        {
            get => _runtimeStatusMessage;
            set => SetProperty(ref _runtimeStatusMessage, value);
        }
        #endregion

        #region Map Data (Collections)
        public ObservableCollection<NodeViewModel> Nodes { get; set; } = new(); // 도화지 위의 모든 노드(컨테이너, 볼륨 등)
        public ObservableCollection<ConnectorViewModel> Connectors { get; } = new ConnectorCollection(); // 노드들을 연결하는 모든 선
        public ObservableCollection<GroupViewModel> Groups { get; set; } = new(); // 노드들을 묶는 모든 그룹(네트워크 등)
        #endregion

        private void NotifyRuntimeAvailabilityChanged()
        {
            foreach (var node in Nodes)
                node.NotifyRuntimeAvailabilityChanged();
        }

        #region Map Settings & View State (Zoom/Pan)
        public const double MapInputScale = 10.0;
        public const double MinimumMapInputWidth = 500.0;
        public const double MinimumMapInputHeight = 300.0;
        public const double MinimumMapWidth = MinimumMapInputWidth * MapInputScale;
        public const double MinimumMapHeight = MinimumMapInputHeight * MapInputScale;
        public const double MaximumScale = 1.8;

        public double MapWidth { get => _mapWidth; set => SetProperty(ref _mapWidth, Math.Max(MinimumMapWidth, value)); } // 도화지의 전체 가로 길이
        public double MapHeight { get => _mapHeight; set => SetProperty(ref _mapHeight, Math.Max(MinimumMapHeight, value)); } // 도화지의 전체 세로 길이
        public double Scale { get => _scale; set => SetProperty(ref _scale, Math.Min(MaximumScale, value)); } // 현재 화면의 확대/축소 비율
        public double OffsetX { get => _offsetX; set => SetProperty(ref _offsetX, value); } // 화면의 가로 스크롤 위치
        public double OffsetY { get => _offsetY; set => SetProperty(ref _offsetY, value); } // 화면의 세로 스크롤 위치
        public bool HasViewportCenter { get => _hasViewportCenter; set => _hasViewportCenter = value; }
        public double ViewportCenterX { get => _viewportCenterX; set => _viewportCenterX = value; }
        public double ViewportCenterY { get => _viewportCenterY; set => _viewportCenterY = value; }
        public bool IsViewportInitialized { get; set; } // 실행 중 최초 화면 위치 복원 여부이며 파일에는 저장하지 않음

        public bool CaptureViewportCenter(double viewportWidth, double viewportHeight)
        {
            if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
                viewportWidth <= 0 || viewportHeight <= 0 ||
                !double.IsFinite(Scale) || Scale <= 0)
            {
                return false;
            }

            double centerX = ((viewportWidth / 2.0) - OffsetX) / Scale;
            double centerY = ((viewportHeight / 2.0) - OffsetY) / Scale;
            if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
                return false;

            bool changed = !HasViewportCenter ||
                           Math.Abs(ViewportCenterX - centerX) > 0.001 ||
                           Math.Abs(ViewportCenterY - centerY) > 0.001;

            ViewportCenterX = centerX;
            ViewportCenterY = centerY;
            HasViewportCenter = true;
            return changed;
        }

        public bool RestoreViewportOffset(double viewportWidth, double viewportHeight)
        {
            if (!HasViewportCenter ||
                !double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
                viewportWidth <= 0 || viewportHeight <= 0 ||
                !double.IsFinite(Scale) || Scale <= 0)
            {
                return false;
            }

            double centerX = Math.Clamp(ViewportCenterX, 0, MapWidth);
            double centerY = Math.Clamp(ViewportCenterY, 0, MapHeight);
            OffsetX = (viewportWidth / 2.0) - (centerX * Scale);
            OffsetY = (viewportHeight / 2.0) - (centerY * Scale);
            return true;
        }
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
        public SheetViewModel(
            string title,
            ConnectionProfile profile,
            IDockerService dockerService,
            IDialogService dialogService,
            RuntimeKind? runtimeKind = null)
        {
            Title = title;
            Profile = profile;
            DockerService = dockerService;
            RuntimeKind = runtimeKind ?? profile.RuntimeKind;
            Profile.RuntimeKind = RuntimeKind;

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
                nodeVm.PortInfo = container.Ports;
                nodeVm.StatusColor = container.StateColor;
                nodeVm.ContainerId = container.Id;
                nodeVm.ComposeProjectName = container.ComposeProjectName;
                nodeVm.ComposeServiceName = container.ComposeServiceName;
                nodeVm.ComposeContainerNumber = container.ComposeContainerNumber;
                nodeVm.IsSwarmService = container.IsSwarmService;
                nodeVm.SwarmMode = container.SwarmMode;
                nodeVm.SwarmDesiredReplicas = container.SwarmDesiredReplicas;
                nodeVm.SwarmRunningReplicas = container.SwarmRunningReplicas;
                nodeVm.TargetSwarmReplicas = container.SwarmDesiredReplicas;
                nodeVm.IsKubernetesPod = container.IsKubernetesPod;
                nodeVm.KubernetesKind = container.KubernetesKind;
                nodeVm.KubernetesApiResource = container.KubernetesApiResource;
                nodeVm.KubernetesApiVersion = container.KubernetesApiVersion;
                nodeVm.KubernetesNamespace = container.KubernetesNamespace;
                nodeVm.KubernetesNodeName = container.KubernetesNodeName;
                nodeVm.KubernetesReady = container.KubernetesReady;
                nodeVm.KubernetesRestarts = container.KubernetesRestarts;
                nodeVm.KubernetesDesiredReplicas = container.KubernetesDesiredReplicas;
                nodeVm.KubernetesReadyReplicas = container.KubernetesReadyReplicas;
                nodeVm.TargetKubernetesReplicas = container.KubernetesDesiredReplicas;
                nodeVm.KubernetesPodIp = container.KubernetesPodIp;
                nodeVm.KubernetesPodJsonText = container.KubernetesRawJson;
                nodeVm.DetailStatus = (container.IsSwarmService || container.IsKubernetesResource) && !string.IsNullOrWhiteSpace(container.Ports)
                    ? container.Ports
                    : container.State;
                nodeVm.IsRunning = container.IsSwarmService || string.Equals(container.State, "running", System.StringComparison.OrdinalIgnoreCase);
                nodeVm.IsDockerConnected = !string.IsNullOrWhiteSpace(container.Id);
            }
            else if (nodeModel is DockerVolume volume)
            {
                nodeVm.StatusColor = "#E67E22";
                nodeVm.ContainerId = "";
                nodeVm.DockerVolumeName = volume.Name;
                nodeVm.VolumeExternal = !string.IsNullOrWhiteSpace(volume.Id);
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

        public bool TryAddDirectedConnection(
            NodeViewModel source,
            NodeViewModel target,
            RelationType relationType,
            string? mountPath = null,
            string? ipAddress = null,
            bool allowSelfConnection = false)
        {
            if (!allowSelfConnection && ReferenceEquals(source, target))
            {
                return false;
            }

            bool exists = Connectors.Any(connector =>
                connector.RelationType == relationType &&
                ReferenceEquals(connector.Source, source) &&
                ReferenceEquals(connector.Target, target));
            if (exists)
            {
                return false;
            }

            Connectors.Add(new ConnectorViewModel(source, target, PortDirection.Right, PortDirection.Left, _dialogService)
            {
                RelationType = relationType,
                MountPath = mountPath,
                IpAddress = ipAddress
            });
            return true;
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
