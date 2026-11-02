using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    internal readonly record struct CanvasPositionUpdate(
        ConnectableItemViewModel Item,
        double X,
        double Y);

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
    public class SheetViewModel : ViewModelBase, IDisposable
    {
        private int _layoutUpdateDepth;
        public bool IsLayoutUpdating => _layoutUpdateDepth > 0;

        public IDisposable BeginLayoutUpdate()
        {
            _layoutUpdateDepth++;
            return new LayoutUpdateScope(this);
        }

        /// <summary>
        /// 한 번의 드래그에 포함된 모든 위치를 먼저 적용한 뒤, 영향을 받은 연결선만
        /// 한 번씩 다시 계산합니다. 일반 레이아웃 갱신의 전체 연결선 새로 고침은 유지합니다.
        /// </summary>
        internal void ApplyInteractivePosition(
            ConnectableItemViewModel item,
            double x,
            double y)
        {
            ArgumentNullException.ThrowIfNull(item);

            _layoutUpdateDepth++;
            bool changed;
            try
            {
                changed = item.SetPosition(x, y);
            }
            finally
            {
                _layoutUpdateDepth--;
            }

            if (_layoutUpdateDepth > 0 || !changed)
                return;

            foreach (ConnectorViewModel connector in Connectors)
            {
                if (ReferenceEquals(connector.Source, item) ||
                    ReferenceEquals(connector.Target, item))
                {
                    connector.RefreshRoute();
                }
            }
        }

        internal void ApplyInteractivePositions(IEnumerable<CanvasPositionUpdate> updates)
        {
            ArgumentNullException.ThrowIfNull(updates);

            var changedItems = new HashSet<ConnectableItemViewModel>(
                ReferenceEqualityComparer.Instance);

            _layoutUpdateDepth++;
            try
            {
                foreach (CanvasPositionUpdate update in updates)
                {
                    if (update.Item.SetPosition(update.X, update.Y))
                        changedItems.Add(update.Item);
                }
            }
            finally
            {
                _layoutUpdateDepth--;
            }

            // 바깥쪽 레이아웃 갱신이 진행 중이면 해당 범위가 끝날 때 전체 경로를 갱신합니다.
            if (_layoutUpdateDepth > 0 || changedItems.Count == 0)
                return;

            foreach (ConnectorViewModel connector in Connectors)
            {
                bool sourceMoved = connector.Source is ConnectableItemViewModel source &&
                                   changedItems.Contains(source);
                bool targetMoved = connector.Target is ConnectableItemViewModel target &&
                                   changedItems.Contains(target);
                if (sourceMoved || targetMoved)
                    connector.RefreshRoute();
            }
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
        private double _offsetX;
        private double _offsetY;
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
        public ObservableCollection<NodeViewModel> Nodes { get; set; } = []; // 도화지 위의 모든 노드(컨테이너, 볼륨 등)
        public ObservableCollection<ConnectorViewModel> Connectors { get; } = new ConnectorCollection(); // 노드들을 연결하는 모든 선
        public ObservableCollection<GroupViewModel> Groups { get; set; } = []; // 노드들을 묶는 모든 그룹(네트워크 등)
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

        internal Func<Task>? RuntimeResourcesChangedAsync { get; set; }

        internal Task NotifyRuntimeResourcesChangedAsync() =>
            RuntimeResourcesChangedAsync?.Invoke() ?? Task.CompletedTask;
        #endregion

        #region Node Methods
        /// <summary>
        /// 지정된 좌표에 새로운 도커 노드(컨테이너, 볼륨, 인터넷)를 생성하여 도화지에 추가합니다.
        /// </summary>
        public void CreateNodeAt(DockerNodeBase nodeModel, double x, double y)
        {
            NodeViewModel nodeVm = nodeModel switch
            {
                DockerVolume volume => DiagramNodeFactory.CreateVolume(
                    _containerService,
                    _volumeService,
                    _dialogService,
                    volume.Name,
                    x,
                    y,
                    isConnected: true),
                DockerInternet => DiagramNodeFactory.CreateInternet(_containerService, _volumeService, _dialogService),
                _ => DiagramNodeFactory.CreateContainer(_containerService, _volumeService, _dialogService)
            };
            nodeVm.Name = nodeModel.Name;
            nodeVm.ParentSheet = this;
            nodeVm.X = x;
            nodeVm.Y = y;
            nodeVm.Width = NodeViewModel.StandardWidth;
            nodeVm.Height = NodeViewModel.StandardHeight;

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
                bool isConnected = !string.IsNullOrWhiteSpace(container.Id);
                if (!container.IsSwarmService && !container.IsKubernetesResource)
                {
                    nodeVm.ApplyObservedContainerState(isConnected, container.State);
                }
                else
                {
                    bool isRunning = container.IsSwarmService ||
                                     string.Equals(container.State, "running", System.StringComparison.OrdinalIgnoreCase);
                    nodeVm.ApplyRuntimeState(
                        isConnected ? RuntimeConnectionState.Connected : RuntimeConnectionState.Disconnected,
                        !isConnected
                            ? RuntimeLifecycleState.Unknown
                            : isRunning
                                ? RuntimeLifecycleState.Running
                                : RuntimeLifecycleState.Stopped,
                        nodeVm.DetailStatus);
                }

                if (RuntimeKind == RuntimeKind.DockerSwarm && container.IsSwarmService)
                {
                    nodeVm.RuntimeKind = RuntimeKind.DockerSwarm;
                    nodeVm.ResourceKind = RuntimeResourceKind.SwarmService;
                    nodeVm.BindingState = string.IsNullOrWhiteSpace(container.Id)
                        ? RuntimeBindingState.Draft
                        : RuntimeBindingState.Bound;
                }
            }
            else if (nodeModel is DockerVolume volume)
            {
                nodeVm.StatusColor = "#E67E22";
                nodeVm.ContainerId = "";
                nodeVm.DockerVolumeName = volume.Name;
                nodeVm.Driver = string.IsNullOrWhiteSpace(volume.Driver) ? "local" : volume.Driver;
                nodeVm.ImageName = nodeVm.Driver;
                nodeVm.VolumeLabels = new Dictionary<string, string>(volume.Labels, StringComparer.OrdinalIgnoreCase);
                nodeVm.VolumeExternal = !string.IsNullOrWhiteSpace(volume.Id);
                nodeVm.SetDockerConnection(true);

                if (RuntimeKind == RuntimeKind.DockerSwarm)
                {
                    nodeVm.RuntimeKind = RuntimeKind.DockerSwarm;
                    nodeVm.ResourceKind = RuntimeResourceKind.SwarmVolume;
                    nodeVm.BindingState = RuntimeBindingState.Bound;
                    nodeVm.DetailStatus = "Manager-local volume · existing";
                }
            }
            else if (nodeModel is DockerInternet internet)
            {
                nodeVm.StatusColor = "#E67E22";
                nodeVm.ContainerId = "";
                nodeVm.SetDockerConnection(true);
            }

            Nodes.Add(nodeVm);
        }

        /// <summary>
        /// Docker inspect에서 확인한 마운트를 시트의 저장소 노드와 연결선에 반영합니다.
        /// Named volume은 Docker 리소스 노드로, bind는 Docker 호스트 경로 참조 노드로 표현합니다.
        /// </summary>
        public void SynchronizeContainerMounts(
            NodeViewModel container,
            IEnumerable<ContainerMountSpec>? mounts)
        {
            if (container.Type != NodeType.Container || container.IsSwarmService || container.IsKubernetesResource)
                return;

            List<ContainerMountSpec> allMounts = mounts?.Select(mount => mount.Clone()).ToList() ?? [];
            container.SetMountSpecifications(allMounts);
            List<ContainerMountSpec> diagramMounts = allMounts
                .Where(mount => mount.Kind is MountKind.Volume or MountKind.Bind)
                .Where(mount => !string.IsNullOrWhiteSpace(mount.Source) && !string.IsNullOrWhiteSpace(mount.Target))
                .ToList();

            List<ConnectorViewModel> existing = Connectors
                .Where(connector => connector.RelationType == RelationType.VolumeMount &&
                                    (ReferenceEquals(connector.Source, container) || ReferenceEquals(connector.Target, container)))
                .Where(connector => GetStorageNode(connector, container)?.Type is NodeType.Volume or NodeType.HostPath)
                .ToList();

            foreach (ConnectorViewModel connector in existing)
            {
                NodeViewModel? storage = GetStorageNode(connector, container);
                if (storage == null) continue;
                bool stillExists = diagramMounts.Any(mount =>
                    mount.Kind == (storage.Type == NodeType.HostPath ? MountKind.Bind : MountKind.Volume) &&
                    string.Equals(mount.Target, connector.MountPath, StringComparison.Ordinal) &&
                    StorageSourceEquals(storage, mount.Source));
                if (!stillExists)
                    Connectors.Remove(connector);
            }

            int storageIndex = 0;
            foreach (ContainerMountSpec mount in diagramMounts)
            {
                NodeViewModel storage = mount.Kind == MountKind.Volume
                    ? GetOrCreateVolumeNode(mount, container, storageIndex)
                    : GetOrCreateHostPathNode(mount, container, storageIndex);

                ConnectorViewModel? connector = Connectors.FirstOrDefault(candidate =>
                    candidate.RelationType == RelationType.VolumeMount &&
                    ((ReferenceEquals(candidate.Source, container) && ReferenceEquals(candidate.Target, storage)) ||
                     (ReferenceEquals(candidate.Target, container) && ReferenceEquals(candidate.Source, storage))) &&
                    string.Equals(candidate.MountPath, mount.Target, StringComparison.Ordinal));
                if (connector == null)
                {
                    connector = new ConnectorViewModel(
                        container,
                        storage,
                        PortDirection.Right,
                        PortDirection.Left,
                        _dialogService)
                    {
                        RelationType = RelationType.VolumeMount
                    };
                    Connectors.Add(connector);
                }

                connector.MountKind = mount.Kind;
                connector.MountPath = mount.Target;
                connector.MountReadOnly = mount.ReadOnly;
                connector.MountOptions = mount.OptionSummary;
                storageIndex++;
            }

            foreach (NodeViewModel hostPath in Nodes.Where(node => node.Type == NodeType.HostPath).ToList())
            {
                bool referenced = Connectors.Any(connector =>
                    ReferenceEquals(connector.Source, hostPath) || ReferenceEquals(connector.Target, hostPath));
                if (!referenced && Nodes.Remove(hostPath))
                    hostPath.Dispose();
            }
        }

        private NodeViewModel GetOrCreateVolumeNode(
            ContainerMountSpec mount,
            NodeViewModel container,
            int index)
        {
            NodeViewModel? volume = Nodes.FirstOrDefault(node =>
                node.Type == NodeType.Volume &&
                (string.Equals(node.Name, mount.Source, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.EffectiveVolumeName, mount.Source, StringComparison.OrdinalIgnoreCase)));
            if (volume != null)
                return volume;

            volume = DiagramNodeFactory.CreateVolume(
                _containerService,
                _volumeService,
                _dialogService,
                mount.Source,
                container.X + container.Width + 50,
                container.Y + (index * 100),
                isConnected: true);
            volume.DockerVolumeName = mount.Source;
            volume.ParentSheet = this;
            volume.ImageName = "local";
            volume.Driver = "local";
            volume.StatusColor = "#E67E22";
            volume.SetDockerConnection(true);
            // 런타임에서 발견한 볼륨은 기본적으로 외부 참조로 보호합니다.
            // 컨테이너 생성 흐름만 생성 전 스냅샷을 사용해 새 노드의 소유권을 확정합니다.
            volume.VolumeExternal = true;
            Nodes.Add(volume);
            return volume;
        }

        private NodeViewModel GetOrCreateHostPathNode(
            ContainerMountSpec mount,
            NodeViewModel container,
            int index)
        {
            NodeViewModel? hostPath = Nodes.FirstOrDefault(node =>
                node.Type == NodeType.HostPath && HostPathEquals(node.HostPath, mount.Source));
            if (hostPath != null)
            {
                if (!string.IsNullOrWhiteSpace(hostPath.ComposeLayoutInstanceId) &&
                    !string.Equals(hostPath.ComposeLayoutInstanceId, container.ComposeLayoutInstanceId, StringComparison.Ordinal))
                {
                    hostPath.ComposeLayoutInstanceId = string.Empty;
                }
                return hostPath;
            }

            string trimmed = mount.Source.TrimEnd('/', '\\');
            string displayName = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                                 ?? mount.Source;
            hostPath = DiagramNodeFactory.CreateHostPath(_containerService, _volumeService, _dialogService);
            hostPath.Name = displayName;
            hostPath.HostPath = mount.Source;
            hostPath.ParentSheet = this;
            hostPath.ImageName = "Docker 호스트 경로";
            hostPath.X = container.X + container.Width + 50;
            hostPath.Y = container.Y + (index * 100);
            hostPath.RuntimeKind = RuntimeKind;
            hostPath.ResourceKind = RuntimeResourceKind.HostPathReference;
            hostPath.BindingState = RuntimeBindingState.Bound;
            hostPath.ComposeLayoutInstanceId = container.ComposeLayoutInstanceId;
            hostPath.StatusColor = "#2563EB";
            hostPath.SetDockerConnection(true);
            Nodes.Add(hostPath);
            return hostPath;
        }

        private static NodeViewModel? GetStorageNode(ConnectorViewModel connector, NodeViewModel container) =>
            ReferenceEquals(connector.Source, container)
                ? connector.Target as NodeViewModel
                : ReferenceEquals(connector.Target, container)
                    ? connector.Source as NodeViewModel
                    : null;

        private static bool StorageSourceEquals(NodeViewModel storage, string source) =>
            storage.Type == NodeType.HostPath
                ? HostPathEquals(storage.HostPath, source)
                : string.Equals(storage.EffectiveVolumeName, source, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(storage.Name, source, StringComparison.OrdinalIgnoreCase);

        private static bool HostPathEquals(string left, string right)
            => ContainerMountParser.HostPathsEqual(left, right);

        /// <summary>
        /// 특정 노드를 도화지에서 삭제하고, 연결된 선과 소속된 그룹 정보를 함께 정리합니다.
        /// </summary>
        public async Task RemoveNodeAsync(NodeViewModel node)
        {
            if (node == null) return;

            IReadOnlyList<NodeViewModel> autoGeneratedDependents =
                DiagramDeletionSupport.FindExclusiveAutoGeneratedDependents(this, node);
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

            if (Nodes.Remove(node))
                node.Dispose();

            foreach (NodeViewModel orphanHostPath in Nodes.Where(candidate => candidate.Type == NodeType.HostPath).ToList())
            {
                bool referenced = Connectors.Any(connector =>
                    ReferenceEquals(connector.Source, orphanHostPath) ||
                    ReferenceEquals(connector.Target, orphanHostPath));
                if (!referenced && Nodes.Remove(orphanHostPath))
                    orphanHostPath.Dispose();
            }

            foreach (NodeViewModel dependent in autoGeneratedDependents)
                await RemoveNodeAsync(dependent);
        }

        public void ClearNodes()
        {
            SelectedNode = null;
            foreach (NodeViewModel node in Nodes.ToList())
            {
                if (ReferenceEquals(node.ParentSheet, this))
                    node.ParentSheet = null;
                node.Dispose();
            }
            Nodes.Clear();
        }

        public void ClearContents()
        {
            SelectedNode = null;
            SelectedGroup = null;
            SelectedConnector = null;

            // 연결과 그룹이 노드를 참조하므로 노드를 폐기하기 전에 먼저 제거합니다.
            Connectors.Clear();
            Groups.Clear();
            ClearNodes();
            ComposeRawYaml = string.Empty;
        }

        public void Dispose()
        {
            ClearContents();
            GC.SuppressFinalize(this);
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
