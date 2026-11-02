using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 노드의 공통 배치·선택 상태를 관리하고 타입별 자식 ViewModel을 조정합니다.
    /// </summary>
    public partial class NodeViewModel : ConnectableItemViewModel, IDisposable
    {
        #region Constants & Fields
        public const double StandardWidth = 160;
        public const double StandardHeight = 80;

        private readonly IContainerService _containerService;
        private readonly IVolumeService _volumeService;
        private readonly IDialogService _dialogService;
        private readonly ContainerMonitoringViewModel _monitoring;
        private readonly ContainerOperationsViewModel _containerOperations;
        private readonly VolumeNodeViewModel _volume;
        private readonly ContainerNetworkViewModel _network;
        private bool _disposed;

        private RuntimeLifecycleState _lifecycleState = RuntimeLifecycleState.Unknown;
        private RuntimeConnectionState _connectionState = RuntimeConnectionState.Unknown;

        private string _name = string.Empty;
        private string _imageName = string.Empty;
        private string _hostPath = string.Empty;
        private string _containerId = string.Empty;
        private string _portInfo = string.Empty;
        private string _detailStatus = "Unknown";
        private string _createdDate = "-";
        private string _startedAt = "-";
        private string _finishedAt = "-";
        private string _stoppedRuntimeSummary = "Exit code - · 종료 시각 -";
        private string _uptime = "-";
        private string _ipAddress = "-";
        private string _ipAddresses = "-";
        private string _connectedNetworks = "-";
        private int _mountCount;
        private string _driver = "-";
        private string _mountpoint = "-";
        private string _restartPolicy = "no";
        private string _healthStatus = "검사 없음";
        private string _healthColor = StatusColors.NotChecked;
        private string _containerLogs = "로그를 불러오는 중...";
        private string _statusColor = StatusColors.Running;
        private NodeOperationProgress _creationProgress = NodeOperationProgress.Idle;
        private string _lastCreationError = string.Empty;
        private string _composePlacementWarning = string.Empty;
        private Func<Task>? _retryFailedCreationAsync;
        private bool _removeBeforeRetry = true;
        private ulong _targetSwarmReplicas;
        private string _swarmMode = string.Empty;
        private ulong _swarmDesiredReplicas;
        private int _targetKubernetesReplicas = 1;
        private string _swarmServiceInspectJson = string.Empty;
        private string _kubernetesPodDescribeText = string.Empty;
        private string _kubernetesPodYamlText = string.Empty;
        private string _kubernetesPodJsonText = string.Empty;
        private bool _swarmDiagramManagesPublishedPorts;
        private bool _swarmDiagramManagesMounts;
        private bool _swarmDiagramManagesNetworks;
        private bool _isSwarmDiagramDirty;

        private int _networkDisplayMode;
        private List<string> _portBindings = [];
        private List<string> _environmentVariables = [];
        private List<ContainerMountSpec> _mountSpecifications = [];
        private string _hostFilePath = string.Empty;
        private string _containerFilePath = string.Empty;
        private string _newEnvInput = "";

        #endregion

        #region Layout Behavior
        protected override double NormalizeX(double value) =>
            DiagramMetrics.SnapToGrid(value);

        protected override double NormalizeY(double value) =>
            DiagramMetrics.SnapToGrid(value);

        protected override double NormalizeWidth(double value) =>
            Math.Max(StandardWidth,
                DiagramMetrics.SnapToGrid(value));

        protected override double NormalizeHeight(double value) =>
            Math.Max(StandardHeight,
                DiagramMetrics.SnapToGrid(value));

        protected override void OnSelectionChanged(bool isSelected)
        {
            if (!isSelected)
            {
                StopMonitoring();
            }
            else if (IsPrimarySelection)
            {
                StartMonitoring();
                _ = RefreshDetailsAsync();
            }
        }

        protected override void OnPrimarySelectionChanged(bool isPrimarySelection)
        {
            if (isPrimarySelection && IsSelected)
            {
                StartMonitoring();
                _ = RefreshDetailsAsync();
            }
            else
            {
                StopMonitoring();
            }
        }

        protected override void OnParentSheetChanged(
            SheetViewModel? previous,
            SheetViewModel? current)
        {
            if (previous != null)
                previous.Connectors.CollectionChanged -= Connectors_CollectionChanged;

            if (current != null)
                current.Connectors.CollectionChanged += Connectors_CollectionChanged;
            else
            {
                // 삭제된 선택 노드가 시트 밖에서도 DispatcherTimer/로그 스트림을 유지하지 않게 합니다.
                IsSelected = false;
                StopMonitoring();
                StopLogStream();
            }

            RefreshConnections();
        }
        #endregion

        #region Basic Info Properties
        /// <summary>
        /// 이 노드의 역할을 정의합니다. (컨테이너, 볼륨, 또는 인터넷)
        /// </summary>
        private NodeType _type = NodeType.Container;
        public NodeType Type
        {
            get => _type;
            init
            {
                _type = value;
                Width = Math.Max(Width, StandardWidth);
                Height = Math.Max(Height, StandardHeight);
            }
        }

        public override string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    OnPropertyChanged(nameof(EffectiveVolumeName));
                    OnPropertyChanged(nameof(IsAnonymousVolume));
                    OnPropertyChanged(nameof(VolumeIdentityTitle));
                    OnPropertyChanged(nameof(VolumeDisplayName));
                    OnPropertyChanged(nameof(KubernetesPodName));
                    OnPropertyChanged(nameof(KubernetesResourceName));
                    RaiseModified();
                }
            }
        }
        public string ImageName
        {
            get => _imageName;
            set
            {
                if (SetProperty(ref _imageName, value ?? string.Empty)) RaiseModified();
            }
        }
        public string HostPath
        {
            get => _hostPath;
            set
            {
                if (SetProperty(ref _hostPath, value ?? string.Empty)) RaiseModified();
            }
        }
        public string PortInfo
        {
            get => _portInfo;
            set
            {
                if (SetProperty(ref _portInfo, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(PortSummary));
                    OnPropertyChanged(nameof(CardResourceSummary));
                }
            }
        }

        public string CardResourceSummary => $"Port {PublishedPortCount} · Mount {MountCount}";

        private int PublishedPortCount
        {
            get
            {
                int bindingCount = _portBindings
                    .Where(binding => !string.IsNullOrWhiteSpace(binding))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (bindingCount > 0) return bindingCount;

                return _portInfo
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(IsPublishedPortMapping)
                    .Select(FormatPortMapping)
                    .Where(mapping => !string.IsNullOrWhiteSpace(mapping))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }
        }

        public string PortSummary
        {
            get
            {
                var mappings = _portInfo
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(IsPublishedPortMapping)
                    .Select(FormatPortMapping)
                    .Where(mapping => !string.IsNullOrWhiteSpace(mapping))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (mappings.Count == 0) return "공개된 포트 없음";
                return mappings.Count == 1 ? mappings[0] : $"{mappings[0]}  +{mappings.Count - 1}";
            }
        }

        private static bool IsPublishedPortMapping(string mapping)
        {
            if (string.IsNullOrWhiteSpace(mapping)) return false;

            // 목록 API의 "8080/tcp" 형식은 EXPOSE만 된 컨테이너 전용 포트입니다.
            // 실제 게시 포트는 "호스트->컨테이너", "호스트:컨테이너" 또는
            // 자동 할당을 요청한 단일 포트 형식으로 들어옵니다.
            return mapping.Contains("->", StringComparison.Ordinal) ||
                   mapping.Contains(':') ||
                   !mapping.Contains('/');
        }

        private static string FormatPortMapping(string mapping)
        {
            int arrowIndex = mapping.IndexOf("->", StringComparison.Ordinal);
            if (arrowIndex < 0) return mapping;

            string host = mapping[..arrowIndex].Trim();
            string target = mapping[(arrowIndex + 2)..].Trim();
            int portSeparator = host.LastIndexOf(':');
            if (portSeparator >= 0) host = host[(portSeparator + 1)..];
            return string.IsNullOrWhiteSpace(host) ? target : $"{host} → {target}";
        }
        public string ComposeProjectName { get; set; } = string.Empty;
        public string ComposeProjectIdentity { get; set; } = string.Empty;
        public string ComposeServiceName { get; set; } = string.Empty;
        public int ComposeContainerNumber { get; set; }
        public string ComposeLayoutInstanceId { get; set; } = string.Empty;
        public string ComposePlacementWarning
        {
            get => _composePlacementWarning;
            set
            {
                if (SetProperty(ref _composePlacementWarning, value ?? string.Empty))
                    OnPropertyChanged(nameof(HasComposePlacementWarning));
            }
        }
        public bool HasComposePlacementWarning => !string.IsNullOrWhiteSpace(ComposePlacementWarning);
        public string ComposeRawServiceYaml { get; set; } = string.Empty;
        public string ComposeRawVolumeYaml { get; set; } = string.Empty;
        public bool IsSwarmService { get; set; }
        public string SwarmMode
        {
            get => _swarmMode;
            set
            {
                if (SetProperty(ref _swarmMode, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(SwarmReplicaSummary));
                    OnPropertyChanged(nameof(SwarmTaskPlanSummary));
                    OnPropertyChanged(nameof(CanScaleSwarmService));
                    RaiseModified();
                }
            }
        }
        public ulong SwarmDesiredReplicas
        {
            get => _swarmDesiredReplicas;
            set
            {
                if (SetProperty(ref _swarmDesiredReplicas, value))
                {
                    OnPropertyChanged(nameof(SwarmReplicaSummary));
                    OnPropertyChanged(nameof(SwarmTaskPlanSummary));
                    RaiseModified();
                }
            }
        }
        public ulong SwarmRunningReplicas { get; set; }
        public bool IsKubernetesPod { get; set; }
        public bool IsKubernetesResource => IsKubernetesPod || !string.IsNullOrWhiteSpace(KubernetesKind);
        public string KubernetesKind { get; set; } = string.Empty;
        public string KubernetesApiResource { get; set; } = string.Empty;
        public string KubernetesApiVersion { get; set; } = string.Empty;
        public string KubernetesNamespace { get; set; } = string.Empty;
        public string KubernetesNodeName { get; set; } = string.Empty;
        public string KubernetesReady { get; set; } = string.Empty;
        public int KubernetesRestarts { get; set; }
        public int KubernetesDesiredReplicas { get; set; }
        public int KubernetesReadyReplicas { get; set; }
        public string KubernetesPodIp { get; set; } = string.Empty;
        public string KubernetesResourceName => ExtractKubernetesResourceName(Name);
        public string KubernetesPodName => KubernetesResourceName;
        public bool IsRuntimeUnavailable => ParentSheet?.IsRuntimeUnavailable == true;
        public bool IsOfflineSnapshot => (IsSwarmService || IsKubernetesResource) && IsRuntimeUnavailable;
        public bool IsDockerRuntimeContainer => Type == NodeType.Container && !IsDraft && !IsSwarmService && !IsKubernetesResource;
        public bool IsGenericKubernetesResource => IsKubernetesResource && !IsKubernetesPod;
        public bool CanControlSwarmService => IsSwarmService && !IsDraft && !IsRuntimeUnavailable && IsDockerConnected;
        public bool CanScaleSwarmService => CanControlSwarmService && SwarmMode.Equals("replicated", StringComparison.OrdinalIgnoreCase);
        public bool CanRefreshKubernetesResource => IsKubernetesResource && !IsRuntimeUnavailable && IsDockerConnected;
        public bool CanRefreshKubernetesPod => IsKubernetesPod && CanRefreshKubernetesResource;
        public bool IsKubernetesDeployment => IsGenericKubernetesResource &&
            (KubernetesKind.Equals("Deployment", StringComparison.OrdinalIgnoreCase) ||
             KubernetesApiResource.Equals("deployment", StringComparison.OrdinalIgnoreCase) ||
             KubernetesApiResource.Equals("deployments", StringComparison.OrdinalIgnoreCase));
        public bool CanScaleKubernetesDeployment => IsKubernetesDeployment && CanRefreshKubernetesResource;
        public bool CanRestartKubernetesRollout => IsKubernetesDeployment && CanRefreshKubernetesResource;
        public bool CanDeleteKubernetesResource => IsKubernetesResource && CanRefreshKubernetesResource;
        public bool CanOpenKubernetesLogsFollow => IsKubernetesPod && CanRefreshKubernetesResource;
        public bool CanOpenKubernetesPortForward => CanRefreshKubernetesResource &&
            (IsKubernetesPod ||
             KubernetesKind.Equals("Service", StringComparison.OrdinalIgnoreCase) ||
             KubernetesKind.Equals("Deployment", StringComparison.OrdinalIgnoreCase));
        public bool CanExportKubernetesYaml => IsKubernetesResource && !string.IsNullOrWhiteSpace(KubernetesPodYamlText);
        public bool CanApplyKubernetesManifest => IsKubernetesResource && CanRefreshKubernetesResource;
        public string KubernetesReplicaSummary => IsKubernetesDeployment
            ? $"{KubernetesReadyReplicas}/{KubernetesDesiredReplicas} ready"
            : "-";
        public string SwarmReplicaSummary => SwarmMode.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? $"global / running {SwarmRunningReplicas}"
            : $"{SwarmRunningReplicas}/{SwarmDesiredReplicas}";
        public string SwarmTaskPlanSummary => SwarmMode.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? "각 eligible Swarm node에 Task 1개"
            : $"Task {SwarmDesiredReplicas}개 배치 예정";
        public ulong TargetSwarmReplicas { get => _targetSwarmReplicas; set => SetProperty(ref _targetSwarmReplicas, value); }
        public int TargetKubernetesReplicas { get => _targetKubernetesReplicas; set => SetProperty(ref _targetKubernetesReplicas, Math.Max(0, value)); }
        public string SwarmServiceInspectJson { get => _swarmServiceInspectJson; set => SetProperty(ref _swarmServiceInspectJson, value); }
        public string KubernetesPodDescribeText { get => _kubernetesPodDescribeText; set => SetProperty(ref _kubernetesPodDescribeText, value); }
        public string KubernetesPodYamlText
        {
            get => _kubernetesPodYamlText;
            set
            {
                if (SetProperty(ref _kubernetesPodYamlText, value))
                {
                    OnPropertyChanged(nameof(CanExportKubernetesYaml));
                    if (ExportKubernetesYamlCommand is AsyncRelayCommand exportYaml)
                        exportYaml.RaiseCanExecuteChanged();
                }
            }
        }
        public string KubernetesPodJsonText { get => _kubernetesPodJsonText; set => SetProperty(ref _kubernetesPodJsonText, value); }
        public ObservableCollection<DockerSwarmTask> SwarmTasks { get; } = [];
        public ObservableCollection<SwarmTaskPlacement> SwarmTaskPlacements { get; } = [];
        public string SwarmTaskSummary => SwarmTasks.Count == 0
            ? "No tasks"
            : $"{SwarmTasks.Count(task => task.CurrentState.Equals("running", StringComparison.OrdinalIgnoreCase))}/{SwarmTasks.Count} running";
        public string SwarmPlacementSummary => SwarmTaskPlacements.Count == 0
            ? "No placement"
            : $"{SwarmTaskPlacements.Count} node(s) · {SwarmTaskSummary}";
        public VolumeNodeViewModel Volume => _volume;

        public Dictionary<string, string> VolumeLabels
        {
            get => _volume.Labels;
            set => _volume.Labels = value;
        }

        public Dictionary<string, string> VolumeDriverOptions
        {
            get => _volume.DriverOptions;
            set => _volume.DriverOptions = value;
        }

        public string VolumeLabelsText => _volume.LabelsText;
        public string VolumeDriverOptionsText => _volume.DriverOptionsText;
        public string VolumeSizeText => _volume.SizeText;
        public long VolumeRefCount => _volume.RefCount;

        public string DockerVolumeName
        {
            get => _volume.DockerVolumeName;
            set
            {
                _volume.DockerVolumeName = value;
                OnPropertyChanged(nameof(DockerVolumeName));
                OnPropertyChanged(nameof(EffectiveVolumeName));
            }
        }

        public bool VolumeExternal
        {
            get => _volume.External;
            set => _volume.External = value;
        }

        public string EffectiveVolumeName => _volume.EffectiveVolumeName;
        public bool IsAnonymousVolume =>
            Type == NodeType.Volume &&
            (VolumeLabels.ContainsKey("com.docker.volume.anonymous") ||
             (!VolumeLabels.ContainsKey("com.docker.compose.volume") &&
              LooksLikeDockerAnonymousVolumeName(EffectiveVolumeName)));
        public string VolumeIdentityTitle => IsAnonymousVolume ? "익명 볼륨" : "이름 있는 볼륨";
        public string VolumeDisplayName => IsAnonymousVolume
            ? DockerIdFormatter.Shorten(EffectiveVolumeName)
            : EffectiveVolumeName;

        private static bool LooksLikeDockerAnonymousVolumeName(string value) =>
            value.Length == 64 && value.All(char.IsAsciiHexDigit);

        /// <summary>
        /// 도커 엔진에서 발급한 실제 컨테이너(또는 볼륨)의 고유 해시 ID입니다.
        /// 이 ID를 기반으로 도커 백엔드와 모든 통신(조회, 제어)이 이루어집니다.
        /// </summary>
        public string ContainerId
        {
            get => _containerId;
            set
            {
                if (SetProperty(ref _containerId, value))
                {
                    OnPropertyChanged(nameof(ShortContainerId));
                }
            }
        }

        public string ShortContainerId => Type == NodeType.Container
            ? DockerIdFormatter.Shorten(ContainerId)
            : string.Empty;
        #endregion

        #region Docker Status Properties
        /// <summary>
        /// 컨테이너 이미지를 다운로드(Pull)하거나 생성 중인 임시 상태인지 여부를 나타냅니다.
        /// 이 상태일 때는 노드의 상태 표시줄이 노란색으로 변경되며 일부 제어가 제한됩니다.
        /// </summary>
        public bool IsCreating
            => LifecycleState == RuntimeLifecycleState.Creating;

        public bool IsBusyCreating => IsCreating && !IsCreationFailed;

        public bool IsCreationFailed => LifecycleState == RuntimeLifecycleState.Failed;

        public string LastCreationError
        {
            get => _lastCreationError;
            private set => SetProperty(ref _lastCreationError, value);
        }

        public bool CanRetryFailedCreation => IsCreationFailed && _retryFailedCreationAsync != null;

        public bool IsDockerConnected => ConnectionState == RuntimeConnectionState.Connected;

        public bool IsDockerDisconnected =>
            Type is NodeType.Container or NodeType.Volume &&
            ResourceKind is not RuntimeResourceKind.SwarmVolume and not RuntimeResourceKind.SwarmExternalTraffic &&
            !IsDraft && !IsCreating && !IsCreationFailed && !IsDockerConnected;

        public bool SwarmDiagramManagesPublishedPorts
        {
            get => _swarmDiagramManagesPublishedPorts;
            set => SetProperty(ref _swarmDiagramManagesPublishedPorts, value);
        }

        public bool SwarmDiagramManagesMounts
        {
            get => _swarmDiagramManagesMounts;
            set => SetProperty(ref _swarmDiagramManagesMounts, value);
        }

        public bool SwarmDiagramManagesNetworks
        {
            get => _swarmDiagramManagesNetworks;
            set => SetProperty(ref _swarmDiagramManagesNetworks, value);
        }

        public bool IsSwarmDiagramDirty
        {
            get => _isSwarmDiagramDirty;
            set
            {
                if (!SetProperty(ref _isSwarmDiagramDirty, value)) return;
                OnPropertyChanged(nameof(SwarmDiagramApplyStatus));
                OnPropertyChanged(nameof(SwarmDiagramApplyStatusColor));
            }
        }

        /// <summary>
        /// 실제 서비스 설정을 읽어 오면서 자동 생성한 보조 노드인지 나타냅니다.
        /// 사용자가 직접 만든 노드는 서비스 삭제 시 자동으로 제거하지 않습니다.
        /// </summary>
        public bool IsAutoGeneratedSwarmTopologyNode { get; set; }

        public string SwarmDiagramApplyStatus => IsSwarmDiagramDirty
            ? "Unapplied diagram changes"
            : "Diagram matches the last applied service topology";

        public string SwarmDiagramApplyStatusColor => IsSwarmDiagramDirty ? "#B45309" : "#2E7D32";

        protected override void OnRuntimeMetadataChanged(string propertyName)
        {
            base.OnRuntimeMetadataChanged(propertyName);
            OnPropertyChanged(nameof(IsDockerRuntimeContainer));
            OnPropertyChanged(nameof(IsDockerDisconnected));
            OnPropertyChanged(nameof(CanControlSwarmService));
            OnPropertyChanged(nameof(CanScaleSwarmService));
        }

        /// <summary>
        /// 실제 도커 컨테이너가 현재 실행 중(Running)인지 여부를 나타냅니다. 
        /// 상태가 변경될 때마다 활성화될 수 있는 버튼(Start, Stop, Terminal 등)의 상태를 자동으로 재평가합니다.
        /// </summary>
        public bool IsRunning
            => LifecycleState is RuntimeLifecycleState.Running or RuntimeLifecycleState.Paused;

        /// <summary>
        /// 실제 도커 컨테이너가 현재 일시 정지(Paused) 상태인지 여부를 나타냅니다.
        /// </summary>
        public bool IsPaused
            => LifecycleState == RuntimeLifecycleState.Paused;

        public string DetailStatus { get => _detailStatus; set => SetProperty(ref _detailStatus, value); }
        public string PauseActionLabel => IsPaused ? "재개" : "일시정지";
        public string CreatedDate { get => _createdDate; set => SetProperty(ref _createdDate, value); }
        public string StartedAt { get => _startedAt; set => SetProperty(ref _startedAt, value); }
        public string FinishedAt { get => _finishedAt; set => SetProperty(ref _finishedAt, value); }
        public string StoppedRuntimeSummary
        {
            get => _stoppedRuntimeSummary;
            internal set => SetProperty(ref _stoppedRuntimeSummary, value);
        }
        public string Uptime { get => _uptime; set => SetProperty(ref _uptime, value); }
        private string _lastDetailsRefresh = "아직 갱신되지 않음";
        public string LastDetailsRefresh { get => _lastDetailsRefresh; set => SetProperty(ref _lastDetailsRefresh, value); }
        public string IPAddress { get => _ipAddress; set => SetProperty(ref _ipAddress, value); }
        public string IpAddresses { get => _ipAddresses; set => SetProperty(ref _ipAddresses, value); }
        public string ConnectedNetworksString { get => _connectedNetworks; set => SetProperty(ref _connectedNetworks, value); }
        public int MountCount
        {
            get => _mountCount;
            internal set
            {
                if (SetProperty(ref _mountCount, value))
                    OnPropertyChanged(nameof(CardResourceSummary));
            }
        }
        public IReadOnlyList<ContainerMountSpec> MountSpecifications => _mountSpecifications;
        public int TmpfsMountCount => _mountSpecifications.Count(mount => mount.Kind == MountKind.Tmpfs);
        public string MountSummary => _mountSpecifications.Count == 0
            ? "마운트 없음"
            : TmpfsMountCount == 0
                ? $"마운트 {_mountSpecifications.Count}"
                : $"마운트 {_mountSpecifications.Count} · tmpfs {TmpfsMountCount}";

        internal void SetMountSpecifications(IEnumerable<ContainerMountSpec>? mounts)
        {
            _mountSpecifications = mounts?.Select(mount => mount.Clone()).ToList() ?? [];
            MountCount = _mountSpecifications.Count;
            OnPropertyChanged(nameof(MountSpecifications));
            OnPropertyChanged(nameof(TmpfsMountCount));
            OnPropertyChanged(nameof(MountSummary));
        }
        public string Driver
        {
            get => _driver;
            set
            {
                if (SetProperty(ref _driver, value)) RaiseModified();
            }
        }
        public string Mountpoint
        {
            get => _mountpoint;
            set => SetProperty(
                ref _mountpoint,
                string.IsNullOrWhiteSpace(value) ? "확인 불가" : value);
        }
        public string RestartPolicy { get => _restartPolicy; set => SetProperty(ref _restartPolicy, value); }
        public string HealthStatus { get => _healthStatus; set => SetProperty(ref _healthStatus, value); }
        public string HealthColor { get => _healthColor; set => SetProperty(ref _healthColor, value); }
        public string ContainerLogs { get => _containerLogs; set => SetProperty(ref _containerLogs, value); }
        public string StatusColor { get => _statusColor; set => SetProperty(ref _statusColor, value); }
        public RuntimeLifecycleState LifecycleState
        {
            get => _lifecycleState;
            private set
            {
                if (!SetProperty(ref _lifecycleState, value)) return;
                OnPropertyChanged(nameof(IsCreating));
                OnPropertyChanged(nameof(IsBusyCreating));
                OnPropertyChanged(nameof(IsCreationFailed));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(PauseActionLabel));
                OnPropertyChanged(nameof(IsDockerDisconnected));
                OnPropertyChanged(nameof(CanRetryFailedCreation));
                RaiseFailureCommandStates();
                RaiseCommandStates();
            }
        }
        public RuntimeConnectionState ConnectionState
        {
            get => _connectionState;
            private set
            {
                if (!SetProperty(ref _connectionState, value)) return;
                OnPropertyChanged(nameof(IsDockerConnected));
                OnPropertyChanged(nameof(IsDockerDisconnected));
                OnPropertyChanged(nameof(CanRefreshKubernetesPod));
                RaiseKubernetesCommandStates();
                RaiseCommandStates();
            }
        }
        public NodeOperationProgress CreationProgress
        {
            get => _creationProgress;
            private set => SetProperty(ref _creationProgress, value);
        }
        #endregion

        public void SetLifecycleState(RuntimeLifecycleState state, string? detailStatus = null)
        {
            LifecycleState = state;
            StatusColor = state switch
            {
                RuntimeLifecycleState.Creating or RuntimeLifecycleState.Paused => StatusColors.InProgress,
                RuntimeLifecycleState.Running => StatusColors.Running,
                RuntimeLifecycleState.Stopped or RuntimeLifecycleState.Failed => StatusColors.Failed,
                _ => StatusColors.Unknown
            };
            if (!string.IsNullOrWhiteSpace(detailStatus))
                DetailStatus = detailStatus;
        }

        public void EndCreating()
        {
            if (LifecycleState == RuntimeLifecycleState.Creating)
                SetLifecycleState(RuntimeLifecycleState.Unknown);
        }

        /// <summary>
        /// Docker가 보고한 컨테이너 상태를 연결/수명주기/표시 상태에 한 번에 반영합니다.
        /// 호출부가 상태 관련 속성을 따로 덮어써 모순을 만들지 않도록 상태 해석을 한곳에 둡니다.
        /// </summary>
        public void ApplyObservedContainerState(bool connected, string? dockerState)
        {
            string normalizedState = dockerState?.Trim() ?? string.Empty;
            if (!connected)
            {
                ApplyRuntimeState(
                    RuntimeConnectionState.Disconnected,
                    RuntimeLifecycleState.Unknown,
                    string.IsNullOrWhiteSpace(normalizedState) ? "Not found" : normalizedState);
                return;
            }

            RuntimeLifecycleState lifecycleState = normalizedState.ToLowerInvariant() switch
            {
                "paused" => RuntimeLifecycleState.Paused,
                "running" or "restarting" => RuntimeLifecycleState.Running,
                "created" or "exited" or "dead" or "removing" => RuntimeLifecycleState.Stopped,
                _ => RuntimeLifecycleState.Unknown
            };

            ApplyRuntimeState(
                RuntimeConnectionState.Connected,
                lifecycleState,
                string.IsNullOrWhiteSpace(normalizedState) ? "Unknown" : normalizedState);
        }

        public void SetDockerConnection(bool connected)
        {
            ConnectionState = connected
                ? RuntimeConnectionState.Connected
                : RuntimeConnectionState.Disconnected;
        }

        public void ApplyRuntimeState(
            RuntimeConnectionState connectionState,
            RuntimeLifecycleState lifecycleState,
            string? detailStatus = null)
        {
            ConnectionState = connectionState;
            SetLifecycleState(lifecycleState, detailStatus);
        }

        public void SetCreationProgress(
            NodeOperationPhase phase,
            string message,
            double? percent = null,
            string? detail = null,
            string? error = null)
        {
            string normalizedMessage = message?.Trim() ?? string.Empty;
            string normalizedDetail = string.IsNullOrWhiteSpace(detail)
                ? normalizedMessage
                : detail.Trim();
            CreationProgress = new NodeOperationProgress(
                phase,
                normalizedMessage,
                normalizedDetail,
                percent.HasValue ? Math.Clamp(percent.Value, 0, 100) : null,
                error?.Trim() ?? string.Empty);
        }

        public void MarkCreationFailed(
            string errorMessage,
            Func<Task>? retryFailedCreationAsync = null,
            bool removeBeforeRetry = true)
        {
            _retryFailedCreationAsync = retryFailedCreationAsync;
            _removeBeforeRetry = removeBeforeRetry;
            SetLifecycleState(RuntimeLifecycleState.Failed, "Failed");
            LastCreationError = string.IsNullOrWhiteSpace(errorMessage)
                ? "Creation failed."
                : errorMessage.Trim();
            SetCreationProgress(
                NodeOperationPhase.Failed,
                "Failed",
                0,
                LastCreationError,
                LastCreationError);
            DetailStatus = "Failed";
            RaiseFailureCommandStates();
        }

        public void ClearCreationFailure()
        {
            _retryFailedCreationAsync = null;
            _removeBeforeRetry = true;
            if (LifecycleState == RuntimeLifecycleState.Failed)
                SetLifecycleState(RuntimeLifecycleState.Unknown);
            LastCreationError = string.Empty;
            RaiseFailureCommandStates();
        }

        #region Resource Monitoring Properties
        public ContainerMonitoringViewModel Monitoring => _monitoring;
        #endregion

        #region Lists & Collections
        public int NetworkDisplayMode { get => _networkDisplayMode; set => SetProperty(ref _networkDisplayMode, value); }

        public ContainerNetworkViewModel Network => _network;
        public Dictionary<string, string> NetworkIpMap
        {
            get => _network.IpMap;
            set => _network.IpMap = value;
        }
        public Dictionary<string, ContainerNetworkOptions> NetworkOptionsMap
        {
            get => _network.OptionsMap;
            set => _network.OptionsMap = value;
        }
        public ObservableCollection<string> ConnectedNodes { get; } = [];
        public ObservableCollection<string> UsedByContainers => _volume.UsedByContainers;
        public ObservableCollection<VolumeUsageInfo> VolumeUsageDetails => _volume.UsageDetails;

        public List<string> PortBindings
        {
            get => _portBindings;
            set
            {
                if (SetProperty(ref _portBindings, value ?? []))
                    OnPropertyChanged(nameof(CardResourceSummary));
            }
        }
        public List<string> EnvironmentVariables { get => _environmentVariables; set => SetProperty(ref _environmentVariables, value); }

        public string HostFilePath
        {
            get => _hostFilePath;
            set
            {
                if (SetProperty(ref _hostFilePath, value))
                    _containerOperations?.OnTransferPathChanged();
            }
        }
        public string ContainerFilePath
        {
            get => _containerFilePath;
            set
            {
                if (SetProperty(ref _containerFilePath, value))
                    _containerOperations?.OnTransferPathChanged();
            }
        }
        public string NewEnvInput { get => _newEnvInput; set => SetProperty(ref _newEnvInput, value); }
        #endregion

        public ContainerNetworkOptions? GetNetworkOptions(string networkName) =>
            _network.GetOptions(networkName);

        public Task<bool> ValidateNetworkOptionsBeforeConnectAsync(
            INetworkService networkService,
            string networkName,
            string? dockerNetworkName = null) =>
            _network.ValidateBeforeConnectAsync(networkService, networkName, dockerNetworkName);

        internal void NotifyModified() => RaiseModified();
        #region Commands
        public ContainerOperationsViewModel ContainerOperations => _containerOperations;
        public AsyncRelayCommand StartCommand => _containerOperations.StartCommand;
        public AsyncRelayCommand StopCommand => _containerOperations.StopCommand;
        public AsyncRelayCommand PauseCommand => _containerOperations.PauseCommand;
        public AsyncRelayCommand RestartCommand => _containerOperations.RestartCommand;
        public AsyncRelayCommand TerminalCommand => _containerOperations.TerminalCommand;
        public ICommand ToggleNetworkModeCommand { get; }
        public ICommand OpenDetailWindowCommand => _containerOperations.OpenDetailWindowCommand;
        public ICommand CopyContainerIdCommand => _containerOperations.CopyContainerIdCommand;
        public ICommand ExportLogsCommand => _containerOperations.ExportLogsCommand;
        public ICommand CopyToContainerCommand => _containerOperations.CopyToContainerCommand;
        public ICommand CopyFromContainerCommand => _containerOperations.CopyFromContainerCommand;
        public ICommand AddEnvAndRecreateCommand => _containerOperations.AddEnvAndRecreateCommand;
        public ICommand BackupVolumeCommand => _volume.BackupCommand;
        public ICommand RestoreVolumeCommand => _volume.RestoreCommand;
        public ICommand RecreateVolumeCommand => _volume.RecreateCommand;
        public ICommand OpenVolumeDetailWindowCommand => _volume.OpenDetailCommand;
        public ICommand CopyVolumeNameCommand => _volume.CopyNameCommand;
        public AsyncRelayCommand ExtractDockerfileCommand => _containerOperations.ExtractDockerfileCommand;
        public ICommand UpdateResourcesCommand => _containerOperations.UpdateResourcesCommand;
        public ICommand RenameContainerCommand => _containerOperations.RenameContainerCommand;
        public ICommand ExecContainerCommand => _containerOperations.ExecContainerCommand;
        public ICommand CommitContainerCommand => _containerOperations.CommitContainerCommand;
        public ICommand KillContainerCommand => _containerOperations.KillContainerCommand;
        public ICommand ViewRawInspectCommand => _containerOperations.ViewRawInspectCommand;
        public ICommand RefreshSwarmServiceCommand { get; }
        public ICommand ScaleSwarmServiceCommand { get; }
        public ICommand RemoveSwarmServiceCommand { get; }
        public ICommand RefreshKubernetesPodCommand { get; }
        public ICommand RefreshKubernetesResourceCommand { get; }
        public ICommand ScaleKubernetesDeploymentCommand { get; }
        public ICommand RestartKubernetesRolloutCommand { get; }
        public ICommand DeleteKubernetesResourceCommand { get; }
        public ICommand OpenKubernetesLogsFollowCommand { get; }
        public ICommand OpenKubernetesPortForwardCommand { get; }
        public ICommand ExportKubernetesYamlCommand { get; }
        public ICommand ApplyKubernetesManifestCommand { get; }
        public ICommand RetryFailedCreationCommand { get; }
        public ICommand RemoveFailedCreationCommand { get; }
        public ICommand ViewCreationErrorCommand { get; }
        #endregion

        #region Constructor
        /// <summary>
        /// 공통 노드 상태와 타입별 기능 ViewModel을 초기화합니다.
        /// </summary>
        public NodeViewModel(IContainerService containerService, IVolumeService volumeService, IDialogService dialogService)
            : base(0, 0, StandardWidth, StandardHeight)
        {
            _containerService = containerService ?? throw new ArgumentNullException(nameof(containerService));
            _volumeService = volumeService ?? throw new ArgumentNullException(nameof(volumeService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _monitoring = new ContainerMonitoringViewModel(
                _containerService,
                () => ContainerId,
                () => IsRunning);
            _containerOperations = new ContainerOperationsViewModel(this, _containerService, _dialogService);
            RefreshSwarmServiceCommand = new AsyncRelayCommand(_ => RefreshSwarmServiceAsync(), _ => CanControlSwarmService);
            ScaleSwarmServiceCommand = new AsyncRelayCommand(_ => ExecuteScaleSwarmServiceAsync(), _ => CanScaleSwarmService);
            RemoveSwarmServiceCommand = new AsyncRelayCommand(_ => ExecuteRemoveSwarmServiceAsync(), _ => CanControlSwarmService);
            RefreshKubernetesPodCommand = new AsyncRelayCommand(_ => RefreshKubernetesPodAsync(), _ => CanRefreshKubernetesPod);
            RefreshKubernetesResourceCommand = new AsyncRelayCommand(_ => RefreshKubernetesResourceAsync(), _ => CanRefreshKubernetesResource);
            ScaleKubernetesDeploymentCommand = new AsyncRelayCommand(_ => ExecuteScaleKubernetesDeploymentAsync(), _ => CanScaleKubernetesDeployment);
            RestartKubernetesRolloutCommand = new AsyncRelayCommand(_ => ExecuteRestartKubernetesRolloutAsync(), _ => CanRestartKubernetesRollout);
            DeleteKubernetesResourceCommand = new AsyncRelayCommand(_ => ExecuteDeleteKubernetesResourceAsync(), _ => CanDeleteKubernetesResource);
            OpenKubernetesLogsFollowCommand = new RelayCommand(_ => ExecuteOpenKubernetesLogsFollow(), _ => CanOpenKubernetesLogsFollow);
            OpenKubernetesPortForwardCommand = new RelayCommand(_ => ExecuteOpenKubernetesPortForward(), _ => CanOpenKubernetesPortForward);
            ExportKubernetesYamlCommand = new AsyncRelayCommand(_ => ExecuteExportKubernetesYamlAsync(), _ => IsKubernetesResource);
            ApplyKubernetesManifestCommand = new AsyncRelayCommand(_ => ExecuteApplyKubernetesManifestAsync(), _ => CanApplyKubernetesManifest);
            RetryFailedCreationCommand = new AsyncRelayCommand(_ => ExecuteRetryFailedCreationAsync(), _ => CanRetryFailedCreation);
            RemoveFailedCreationCommand = new AsyncRelayCommand(_ => ExecuteRemoveFailedCreationAsync(), _ => IsCreationFailed);
            ViewCreationErrorCommand = new RelayCommand(_ => ExecuteViewCreationError(), _ => IsCreationFailed);
            _network = new ContainerNetworkViewModel(this, _dialogService);
            _network.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ContainerNetworkViewModel.IpMap))
                    OnPropertyChanged(nameof(NetworkIpMap));
                else if (e.PropertyName == nameof(ContainerNetworkViewModel.OptionsMap))
                    OnPropertyChanged(nameof(NetworkOptionsMap));
            };
            _volume = new VolumeNodeViewModel(this, _volumeService, _dialogService);
            _volume.PropertyChanged += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.PropertyName)) return;

                switch (e.PropertyName)
                {
                    case nameof(VolumeNodeViewModel.Labels):
                        OnPropertyChanged(nameof(VolumeLabels));
                        OnPropertyChanged(nameof(IsAnonymousVolume));
                        OnPropertyChanged(nameof(VolumeIdentityTitle));
                        OnPropertyChanged(nameof(VolumeDisplayName));
                        break;
                    case nameof(VolumeNodeViewModel.DriverOptions):
                        OnPropertyChanged(nameof(VolumeDriverOptions));
                        break;
                    case nameof(VolumeNodeViewModel.LabelsText):
                        OnPropertyChanged(nameof(VolumeLabelsText));
                        break;
                    case nameof(VolumeNodeViewModel.DriverOptionsText):
                        OnPropertyChanged(nameof(VolumeDriverOptionsText));
                        break;
                    case nameof(VolumeNodeViewModel.SizeText):
                        OnPropertyChanged(nameof(VolumeSizeText));
                        break;
                    case nameof(VolumeNodeViewModel.RefCount):
                        OnPropertyChanged(nameof(VolumeRefCount));
                        break;
                    case nameof(VolumeNodeViewModel.DockerVolumeName):
                        OnPropertyChanged(nameof(DockerVolumeName));
                        OnPropertyChanged(nameof(EffectiveVolumeName));
                        OnPropertyChanged(nameof(IsAnonymousVolume));
                        OnPropertyChanged(nameof(VolumeIdentityTitle));
                        OnPropertyChanged(nameof(VolumeDisplayName));
                        break;
                    case nameof(VolumeNodeViewModel.External):
                        OnPropertyChanged(nameof(VolumeExternal));
                        break;
                }
            };

            ToggleNetworkModeCommand = new RelayCommand(_ => {
                NetworkDisplayMode = (NetworkDisplayMode + 1) % 3;
            });

        }
        #endregion

        #region Public Methods (Lifecycle & Monitoring)
        /// <summary>
        /// 노드 타입에 맞는 자식 ViewModel에서 최신 Docker 상태를 불러옵니다.
        /// </summary>
        public async Task RefreshDetailsAsync(bool refreshMonitoring = true)
        {
            if (string.IsNullOrWhiteSpace(Name)) return;

            if (IsDraft)
            {
                ApplyRuntimeState(RuntimeConnectionState.Disconnected, RuntimeLifecycleState.Unknown);
                DetailStatus = "Draft";
                StatusColor = "#7652A8";
                CommandManager.InvalidateRequerySuggested();
                RefreshConnections();
                return;
            }

            if (Type == NodeType.Internet)
            {
                SetDockerConnection(true);
                return;
            }

            if (IsSwarmService)
            {
                bool connected = !string.IsNullOrWhiteSpace(ContainerId);
                ApplyRuntimeState(
                    connected ? RuntimeConnectionState.Connected : RuntimeConnectionState.Disconnected,
                    connected ? RuntimeLifecycleState.Running : RuntimeLifecycleState.Unknown);
                DetailStatus = string.IsNullOrWhiteSpace(PortInfo) ? "service" : PortInfo;
                StatusColor = StatusColors.Running;
                CommandManager.InvalidateRequerySuggested();
                RefreshConnections();
                return;
            }

            if (IsKubernetesResource)
            {
                bool connected = !string.IsNullOrWhiteSpace(ContainerId);
                bool running = DetailStatus.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                               DetailStatus.Contains("available", StringComparison.OrdinalIgnoreCase) ||
                               DetailStatus.Contains("bound", StringComparison.OrdinalIgnoreCase);
                ApplyRuntimeState(
                    connected ? RuntimeConnectionState.Connected : RuntimeConnectionState.Disconnected,
                    !connected
                        ? RuntimeLifecycleState.Unknown
                        : running
                            ? RuntimeLifecycleState.Running
                            : RuntimeLifecycleState.Stopped);
                DetailStatus = string.IsNullOrWhiteSpace(PortInfo) ? DetailStatus : PortInfo;
                CommandManager.InvalidateRequerySuggested();
                RefreshConnections();
                return;
            }

            try
            {
                if (Type == NodeType.Container)
                    await _containerOperations.RefreshDetailsAsync(refreshMonitoring);
                else if (Type == NodeType.Volume)
                    await _volume.RefreshDetailsAsync();

                CommandManager.InvalidateRequerySuggested();
                RefreshConnections();
            }
            catch (Exception ex)
            {
                AppLog.WarningThrottled(
                    $"node-refresh-{ContainerId}",
                    $"노드 '{Name}' 상세 정보 새로 고침 실패",
                    ex);
                DetailStatus = "Error";
                ApplyRuntimeState(RuntimeConnectionState.Disconnected, RuntimeLifecycleState.Unknown);
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public async Task<bool> ReconnectDockerResourceAsync()
        {
            try
            {
                if (Type == NodeType.Internet)
                {
                    SetDockerConnection(true);
                    return true;
                }

                if (Type == NodeType.Container)
                    return await _containerOperations.ReconnectAsync();
                if (Type == NodeType.Volume)
                    return await _volume.ReconnectAsync();
            }
            catch (Exception ex)
            {
                if (Type == NodeType.Container)
                    ApplyObservedContainerState(connected: false, dockerState: "Connection error");
                else
                    SetDockerConnection(false);
                _dialogService.ShowError($"Reconnect 실패: {ex.Message}", "Reconnect");
            }

            return false;
        }

        private async Task ExecuteRetryFailedCreationAsync()
        {
            if (_retryFailedCreationAsync == null)
            {
                _dialogService.ShowInfo("이 실패 노드는 다시 시도할 수 있는 작업 정보가 없습니다.", "Retry");
                return;
            }

            var retry = _retryFailedCreationAsync;
            bool removeBeforeRetry = _removeBeforeRetry;
            try
            {
                if (removeBeforeRetry)
                    await RemoveSelfFromSheetAsync();
                await retry();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"재시도 실패:\n{ex.Message}", "Retry Failed");
            }
        }

        private async Task ExecuteRemoveFailedCreationAsync()
        {
            if (!IsCreationFailed)
                return;

            if (!_dialogService.ShowConfirm($"실패한 노드 '{Name}'을(를) 시트에서 제거하시겠습니까?", "Remove Failed Node"))
                return;

            await RemoveSelfFromSheetAsync();
        }

        private void ExecuteViewCreationError()
        {
            _dialogService.ShowError(
                string.IsNullOrWhiteSpace(LastCreationError) ? "저장된 오류 정보가 없습니다." : LastCreationError,
                "Creation Error");
        }

        private async Task RemoveSelfFromSheetAsync()
        {
            if (ParentSheet != null)
            {
                await ParentSheet.RemoveNodeAsync(this);
                return;
            }

            ClearCreationFailure();
        }

        private void RaiseFailureCommandStates()
        {
            if (RetryFailedCreationCommand is AsyncRelayCommand retry)
                retry.RaiseCanExecuteChanged();
            if (RemoveFailedCreationCommand is AsyncRelayCommand remove)
                remove.RaiseCanExecuteChanged();
            if (ViewCreationErrorCommand is RelayCommand view)
                view.RaiseCanExecuteChanged();
        }
        public void StartMonitoring()
        {
            if (Type != NodeType.Container || IsSwarmService || IsKubernetesResource || string.IsNullOrEmpty(ContainerId)) return;
            _monitoring.Start();
        }

        /// <summary>
        /// 노드 선택이 해제되면 불필요한 백그라운드 통신을 줄이기 위해 리소스 모니터링 타이머를 정지합니다.
        /// </summary>
        public void StopMonitoring() => _monitoring.Stop();

        internal bool IsMonitoring => _monitoring.IsMonitoring;

        public void NotifyRuntimeAvailabilityChanged()
        {
            OnPropertyChanged(nameof(IsRuntimeUnavailable));
            OnPropertyChanged(nameof(IsOfflineSnapshot));
            OnPropertyChanged(nameof(CanControlSwarmService));
            OnPropertyChanged(nameof(CanRefreshKubernetesResource));
            OnPropertyChanged(nameof(CanRefreshKubernetesPod));
            RaiseCommandStates();
            RaiseSwarmCommandStates();
            RaiseKubernetesCommandStates();
        }

        /// <summary>
        /// 다이어그램 캔버스 상에서 이 노드와 선(Connector)으로 직접 연결된 
        /// 다른 노드(볼륨, 인터넷 등)나 네트워크 그룹들을 탐색하여 '연결 관계 목록'을 최신화합니다.
        /// </summary>
        public void RefreshConnections()
        {
            ConnectedNodes.Clear();
            if (ParentSheet == null) return;

            var relatedConnectors = ParentSheet.Connectors
                .Where(c => c.Source == this || c.Target == this)
                .ToList();

            foreach (var conn in relatedConnectors)
            {
                var otherItem = (conn.Source == this) ? conn.Target : conn.Source;

                // 1. 만약 연결된 대상이 Node라면
                if (otherItem is NodeViewModel otherNode)
                {
                    if (otherNode.Type == NodeType.Container || otherNode.Type == NodeType.Internet)
                    {
                        if (!ConnectedNodes.Contains(otherNode.Name))
                        {
                            ConnectedNodes.Add(otherNode.Name);
                        }
                    }
                }
                // 2. 만약 연결된 대상이 Group(네트워크)라면
                else if (otherItem is GroupViewModel groupNode)
                {
                    if (!ConnectedNodes.Contains(groupNode.Name))
                    {
                        ConnectedNodes.Add($"[Network] {groupNode.Name}");
                    }
                }
            }
        }
        #endregion

        #region Private Helper Methods (Internal Logic)
        private static string ExtractKubernetesResourceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            int slashIndex = name.IndexOf('/');
            return slashIndex >= 0 && slashIndex + 1 < name.Length ? name[(slashIndex + 1)..] : name;
        }

        /// <summary>
        /// 노드의 실행 상태(Running, Paused)가 변경되었을 때, 
        /// UI에 연결된 제어 버튼(시작, 정지, 재시작, 터미널 등)들의 활성화/비활성화 가능 여부를 즉시 갱신합니다.
        /// </summary>
        private void RaiseCommandStates()
        {
            _containerOperations?.RaiseCommandStates();
        }

        private void Connectors_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshConnections();
        }
        #endregion

        #region Feature Methods (UI Actions)
        /// <summary>
        /// 컨테이너 로그 스트림의 시작과 종료를 작업 ViewModel에 위임합니다.
        /// </summary>
        public Task StartLogStreamAsync(Action<string> onLogReceived, int initialTailCount = 100) =>
            _containerOperations.StartLogStreamAsync(onLogReceived, initialTailCount);


        public void StopLogStream() => _containerOperations.StopLogStream();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // ParentSheet 해제는 Connectors.CollectionChanged 구독과 선택 기반 모니터링을
            // 함께 정리합니다. 시트 컬렉션이 Reset되기 전에 반드시 수행되어야 합니다.
            ParentSheet = null;
            _monitoring.Dispose();
            _containerOperations.Dispose();
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
