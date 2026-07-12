using DockerDiagram.Helpers;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;
using OxyPlot;
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

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 노드의 공통 배치·선택 상태를 관리하고 타입별 자식 ViewModel을 조정합니다.
    /// </summary>
    public class NodeViewModel : ConnectableItemViewModel
    {
        #region Constants & Fields
        private const int GRID_SIZE = 10;
        private const double MIN_SIZE = 50;

        private readonly IContainerService _containerService;
        private readonly IVolumeService _volumeService;
        private readonly IDialogService _dialogService;
        private readonly ContainerMonitoringViewModel _monitoring;
        private readonly ContainerOperationsViewModel _containerOperations;
        private readonly VolumeNodeViewModel _volume;
        private readonly ContainerNetworkViewModel _network;

        private bool _isCreating = false;
        private bool _isDockerConnected = false;
        private bool _isRunning;
        private bool _isPaused;

        private string _name = string.Empty;
        private string _containerId = string.Empty;
        private string _detailStatus = "Unknown";
        private string _createdDate = "-";
        private string _startedAt = "-";
        private string _finishedAt = "-";
        private string _uptime = "-";
        private string _ipAddress = "-";
        private string _ipAddresses = "-";
        private string _connectedNetworks = "-";
        private string _mountedVolumes = "None";
        private string _driver = "-";
        private string _mountpoint = "-";
        private string _restartPolicy = "no";
        private string _healthStatus = "No Check";
        private string _healthColor = "#888888";
        private string _containerLogs = "Loading logs...";
        private string _statusColor = "#28a745";
        private string _creationProgressMessage = string.Empty;
        private double _creationProgressValue;
        private bool _isCreationProgressIndeterminate = true;
        private bool _isCreationFailed;
        private string _lastCreationError = string.Empty;
        private Func<Task>? _retryFailedCreationAsync;
        private ulong _targetSwarmReplicas;
        private int _targetKubernetesReplicas = 1;
        private string _swarmServiceInspectJson = string.Empty;
        private string _kubernetesPodDescribeText = string.Empty;
        private string _kubernetesPodYamlText = string.Empty;
        private string _kubernetesPodJsonText = string.Empty;

        private int _networkDisplayMode = 0;
        private int _volumeDisplayMode = 0;
        private List<string> _portBindings = new();
        private List<string> _environmentVariables = new();
        private string _hostFilePath = @"C:\temp\";
        private string _containerFilePath = @"/app/data";
        private string _newEnvInput = "";

        #endregion

        #region Layout Behavior
        protected override double NormalizeX(double value) =>
            Math.Round(value / GRID_SIZE) * GRID_SIZE;

        protected override double NormalizeY(double value) =>
            Math.Round(value / GRID_SIZE) * GRID_SIZE;

        protected override double NormalizeWidth(double value) =>
            Math.Max(MIN_SIZE, Math.Round(value / GRID_SIZE) * GRID_SIZE);

        protected override double NormalizeHeight(double value) =>
            Math.Max(MIN_SIZE, Math.Round(value / GRID_SIZE) * GRID_SIZE);

        protected override void OnSelectionChanged(bool isSelected)
        {
            if (isSelected)
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

            RefreshConnections();
        }
        #endregion

        #region Basic Info Properties
        /// <summary>
        /// 이 노드의 역할을 정의합니다. (컨테이너, 볼륨, 또는 인터넷)
        /// </summary>
        public NodeType Type { get; init; }

        public override string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    OnPropertyChanged(nameof(EffectiveVolumeName));
                    OnPropertyChanged(nameof(KubernetesPodName));
                    OnPropertyChanged(nameof(KubernetesResourceName));
                }
            }
        }
        public string ImageName { get; set; } = string.Empty;
        public string PortInfo { get; set; } = string.Empty;
        public string ComposeProjectName { get; set; } = string.Empty;
        public string ComposeServiceName { get; set; } = string.Empty;
        public int ComposeContainerNumber { get; set; }
        public string ComposeLayoutInstanceId { get; set; } = string.Empty;
        public string ComposeRawServiceYaml { get; set; } = string.Empty;
        public string ComposeRawVolumeYaml { get; set; } = string.Empty;
        public bool IsSwarmService { get; set; }
        public string SwarmMode { get; set; } = string.Empty;
        public ulong SwarmDesiredReplicas { get; set; }
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
        public bool IsDockerRuntimeContainer => Type == NodeType.Container && !IsSwarmService && !IsKubernetesResource;
        public bool IsGenericKubernetesResource => IsKubernetesResource && !IsKubernetesPod;
        public bool CanControlSwarmService => IsSwarmService && !IsRuntimeUnavailable && IsDockerConnected;
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
        public ObservableCollection<DockerSwarmTask> SwarmTasks { get; } = new();
        public string SwarmTaskSummary => SwarmTasks.Count == 0
            ? "No tasks"
            : $"{SwarmTasks.Count(task => task.CurrentState.Equals("running", StringComparison.OrdinalIgnoreCase))}/{SwarmTasks.Count} running";
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

        public string ShortContainerId => (Type == NodeType.Container) ? ShortenId(ContainerId) : "";
        #endregion

        #region Docker Status Properties
        /// <summary>
        /// 컨테이너 이미지를 다운로드(Pull)하거나 생성 중인 임시 상태인지 여부를 나타냅니다.
        /// 이 상태일 때는 노드의 상태 표시줄이 노란색으로 변경되며 일부 제어가 제한됩니다.
        /// </summary>
        public bool IsCreating
        {
            get => _isCreating;
            set
            {
                if (SetProperty(ref _isCreating, value))
                {
                    StatusColor = value ? "#FFC107" : "#28a745";
                    OnPropertyChanged(nameof(IsDockerDisconnected));
                    OnPropertyChanged(nameof(IsBusyCreating));
                }
            }
        }

        public bool IsBusyCreating => IsCreating && !IsCreationFailed;

        public bool IsCreationFailed
        {
            get => _isCreationFailed;
            private set
            {
                if (SetProperty(ref _isCreationFailed, value))
                {
                    OnPropertyChanged(nameof(IsBusyCreating));
                    OnPropertyChanged(nameof(IsDockerDisconnected));
                    OnPropertyChanged(nameof(CanRetryFailedCreation));
                    RaiseFailureCommandStates();
                }
            }
        }

        public string LastCreationError
        {
            get => _lastCreationError;
            private set => SetProperty(ref _lastCreationError, value);
        }

        public bool CanRetryFailedCreation => IsCreationFailed && _retryFailedCreationAsync != null;

        public bool IsDockerConnected
        {
            get => _isDockerConnected;
            set
            {
                if (SetProperty(ref _isDockerConnected, value))
                {
                    OnPropertyChanged(nameof(IsDockerDisconnected));
                    OnPropertyChanged(nameof(CanRefreshKubernetesPod));
                    RaiseKubernetesCommandStates();
                    RaiseCommandStates();
                }
            }
        }

        public bool IsDockerDisconnected => Type != NodeType.Internet && !IsCreating && !IsCreationFailed && !IsDockerConnected;

        /// <summary>
        /// 실제 도커 컨테이너가 현재 실행 중(Running)인지 여부를 나타냅니다. 
        /// 상태가 변경될 때마다 활성화될 수 있는 버튼(Start, Stop, Terminal 등)의 상태를 자동으로 재평가합니다.
        /// </summary>
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>
        /// 실제 도커 컨테이너가 현재 일시 정지(Paused) 상태인지 여부를 나타냅니다.
        /// </summary>
        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (SetProperty(ref _isPaused, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public string DetailStatus { get => _detailStatus; set => SetProperty(ref _detailStatus, value); }
        public string CreatedDate { get => _createdDate; set => SetProperty(ref _createdDate, value); }
        public string StartedAt { get => _startedAt; set => SetProperty(ref _startedAt, value); }
        public string FinishedAt { get => _finishedAt; set => SetProperty(ref _finishedAt, value); }
        public string Uptime { get => _uptime; set => SetProperty(ref _uptime, value); }
        public string IPAddress { get => _ipAddress; set => SetProperty(ref _ipAddress, value); }
        public string IpAddresses { get => _ipAddresses; set => SetProperty(ref _ipAddresses, value); }
        public string ConnectedNetworksString { get => _connectedNetworks; set => SetProperty(ref _connectedNetworks, value); }
        public string MountedVolumes { get => _mountedVolumes; set => SetProperty(ref _mountedVolumes, value); }
        public string Driver { get => _driver; set => SetProperty(ref _driver, value); }
        public string Mountpoint { get => _mountpoint; set => SetProperty(ref _mountpoint, value); }
        public string RestartPolicy { get => _restartPolicy; set => SetProperty(ref _restartPolicy, value); }
        public string HealthStatus { get => _healthStatus; set => SetProperty(ref _healthStatus, value); }
        public string HealthColor { get => _healthColor; set => SetProperty(ref _healthColor, value); }
        public string ContainerLogs { get => _containerLogs; set => SetProperty(ref _containerLogs, value); }
        public string StatusColor { get => _statusColor; set => SetProperty(ref _statusColor, value); }
        public string CreationProgressMessage { get => _creationProgressMessage; set => SetProperty(ref _creationProgressMessage, value); }
        public double CreationProgressValue { get => _creationProgressValue; set => SetProperty(ref _creationProgressValue, Math.Clamp(value, 0, 100)); }
        public bool IsCreationProgressIndeterminate { get => _isCreationProgressIndeterminate; set => SetProperty(ref _isCreationProgressIndeterminate, value); }
        #endregion

        public void SetCreationProgress(string message, double? percent = null)
        {
            CreationProgressMessage = message;
            DetailStatus = message;
            if (percent.HasValue)
            {
                CreationProgressValue = percent.Value;
                IsCreationProgressIndeterminate = false;
            }
            else
            {
                IsCreationProgressIndeterminate = true;
            }
        }

        public void MarkCreationFailed(string errorMessage, Func<Task>? retryFailedCreationAsync = null)
        {
            _retryFailedCreationAsync = retryFailedCreationAsync;
            IsCreating = false;
            IsRunning = false;
            IsDockerConnected = false;
            IsCreationFailed = true;
            StatusColor = "#DC3545";
            LastCreationError = string.IsNullOrWhiteSpace(errorMessage)
                ? "Creation failed."
                : errorMessage.Trim();
            CreationProgressValue = 0;
            IsCreationProgressIndeterminate = false;
            CreationProgressMessage = "Failed";
            DetailStatus = "Failed";
            RaiseFailureCommandStates();
        }

        public void ClearCreationFailure()
        {
            _retryFailedCreationAsync = null;
            IsCreationFailed = false;
            LastCreationError = string.Empty;
            RaiseFailureCommandStates();
        }

        #region Resource Monitoring Properties
        public ContainerMonitoringViewModel Monitoring => _monitoring;
        public double MaxCpuCount { get => _monitoring.MaxCpuCount; set => _monitoring.MaxCpuCount = value; }
        public double TargetCpuCount { get => _monitoring.TargetCpuCount; set => _monitoring.TargetCpuCount = value; }
        public long MaxMemoryMb { get => _monitoring.MaxMemoryMb; set => _monitoring.MaxMemoryMb = value; }
        public long TargetMemoryMb { get => _monitoring.TargetMemoryMb; set => _monitoring.TargetMemoryMb = value; }
        public string CpuUsage => _monitoring.CpuUsage;
        public double CpuValue => _monitoring.CpuValue;
        public string MemoryUsage => _monitoring.MemoryUsage;
        public double MemoryValue => _monitoring.MemoryValue;
        public PlotModel CpuPlotModel => _monitoring.CpuPlotModel;
        public PlotModel MemoryPlotModel => _monitoring.MemoryPlotModel;
        #endregion

        #region Lists & Collections
        public int NetworkDisplayMode { get => _networkDisplayMode; set => SetProperty(ref _networkDisplayMode, value); }

        /// <summary>
        /// 화면에 표시할 볼륨 정보의 출력 모드를 결정합니다. (0: 도커 Named 볼륨만 표시, 1: 로컬 폴더 Bind 마운트 포함 전체 표시)
        /// </summary>
        public int VolumeDisplayMode
        {
            get => _volumeDisplayMode;
            set
            {
                if (SetProperty(ref _volumeDisplayMode, value))
                {
                    _containerOperations?.RefreshMountedVolumes();
                }
            }
        }

        public ContainerNetworkViewModel Network => _network;
        public ObservableCollection<ContainerNetworkDetailViewModel> NetworkDetailList => _network.Details;
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
        public ObservableCollection<string> MountedVolumeList { get; } = new();
        public ObservableCollection<string> ConnectedNodes { get; } = new();
        public ObservableCollection<string> UsedByContainers => _volume.UsedByContainers;
        public ObservableCollection<VolumeUsageInfo> VolumeUsageDetails => _volume.UsageDetails;

        public List<string> PortBindings { get => _portBindings; set => SetProperty(ref _portBindings, value); }
        public List<string> EnvironmentVariables
        {
            get => _environmentVariables;
            set
            {
                if (SetProperty(ref _environmentVariables, value))
                {
                    OnPropertyChanged(nameof(EnvList));
                }
            }
        }
        public List<string> EnvList => EnvironmentVariables;

        public string HostFilePath { get => _hostFilePath; set => SetProperty(ref _hostFilePath, value); }
        public string ContainerFilePath { get => _containerFilePath; set => SetProperty(ref _containerFilePath, value); }
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
        public RelayCommand TerminalCommand => _containerOperations.TerminalCommand;
        public ICommand ToggleNetworkModeCommand { get; }
        public ICommand ToggleVolumeModeCommand { get; }
        public ICommand OpenDetailWindowCommand => _containerOperations.OpenDetailWindowCommand;
        public ICommand RefreshLogsCommand => _containerOperations.RefreshLogsCommand;
        public ICommand CopyLogsCommand => _containerOperations.CopyLogsCommand;
        public ICommand ExportLogsCommand => _containerOperations.ExportLogsCommand;
        public ICommand CopyToContainerCommand => _containerOperations.CopyToContainerCommand;
        public ICommand CopyFromContainerCommand => _containerOperations.CopyFromContainerCommand;
        public ICommand AddEnvAndRecreateCommand => _containerOperations.AddEnvAndRecreateCommand;
        public ICommand BackupVolumeCommand => _volume.BackupCommand;
        public ICommand RestoreVolumeCommand => _volume.RestoreCommand;
        public ICommand RecreateVolumeCommand => _volume.RecreateCommand;
        public AsyncRelayCommand ExtractDockerfileCommand => _containerOperations.ExtractDockerfileCommand;
        public ICommand UpdateResourcesCommand => _containerOperations.UpdateResourcesCommand;
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
            : base(0, 0, 160, 80)
        {
            _containerService = containerService ?? throw new ArgumentNullException(nameof(containerService));
            _volumeService = volumeService ?? throw new ArgumentNullException(nameof(volumeService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _monitoring = new ContainerMonitoringViewModel(
                _containerService,
                () => ContainerId,
                () => IsRunning);
            _monitoring.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.PropertyName))
                {
                    OnPropertyChanged(e.PropertyName);
                }
            };
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
                        break;
                    case nameof(VolumeNodeViewModel.External):
                        OnPropertyChanged(nameof(VolumeExternal));
                        break;
                }
            };

            ToggleNetworkModeCommand = new RelayCommand(_ => {
                NetworkDisplayMode = (NetworkDisplayMode + 1) % 3;
            });

            ToggleVolumeModeCommand = new RelayCommand(_ => {
                VolumeDisplayMode = (VolumeDisplayMode + 1) % 2;
            });

        }
        #endregion

        #region Public Methods (Lifecycle & Monitoring)
        /// <summary>
        /// 노드 타입에 맞는 자식 ViewModel에서 최신 Docker 상태를 불러옵니다.
        /// </summary>
        public async Task RefreshDetailsAsync()
        {
            if (string.IsNullOrWhiteSpace(Name)) return;

            if (Type == NodeType.Internet)
            {
                IsDockerConnected = true;
                return;
            }

            if (IsSwarmService)
            {
                IsDockerConnected = !string.IsNullOrWhiteSpace(ContainerId);
                IsRunning = true;
                IsPaused = false;
                DetailStatus = string.IsNullOrWhiteSpace(PortInfo) ? "service" : PortInfo;
                StatusColor = "#28a745";
                CommandManager.InvalidateRequerySuggested();
                RefreshConnections();
                return;
            }

            if (IsKubernetesResource)
            {
                IsDockerConnected = !string.IsNullOrWhiteSpace(ContainerId);
                IsRunning = DetailStatus.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                            DetailStatus.Contains("available", StringComparison.OrdinalIgnoreCase) ||
                            DetailStatus.Contains("bound", StringComparison.OrdinalIgnoreCase);
                IsPaused = false;
                DetailStatus = string.IsNullOrWhiteSpace(PortInfo) ? DetailStatus : PortInfo;
                CommandManager.InvalidateRequerySuggested();
                RefreshConnections();
                return;
            }

            try
            {
                if (Type == NodeType.Container)
                    await _containerOperations.RefreshDetailsAsync();
                else if (Type == NodeType.Volume)
                    await _volume.RefreshDetailsAsync();

                CommandManager.InvalidateRequerySuggested();
                RefreshConnections();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Refresh Error: {ex.Message}");
                DetailStatus = "Error";
                IsDockerConnected = false;
                IsRunning = false;
                IsPaused = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public async Task<bool> ReconnectDockerResourceAsync()
        {
            try
            {
                if (Type == NodeType.Internet)
                {
                    IsDockerConnected = true;
                    return true;
                }

                if (Type == NodeType.Container)
                    return await _containerOperations.ReconnectAsync();
                if (Type == NodeType.Volume)
                    return await _volume.ReconnectAsync();
            }
            catch (Exception ex)
            {
                IsDockerConnected = false;
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
            try
            {
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

            IsCreationFailed = false;
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

        public async Task RefreshSwarmServiceAsync()
        {
            if (!IsSwarmService || _containerService is not ISwarmService swarmService)
                return;

            if (IsRuntimeUnavailable)
            {
                _dialogService.ShowInfo("현재 시트는 오프라인 스냅샷입니다. 런타임을 사용할 수 있을 때 다시 동기화해 주세요.", "Offline Snapshot");
                return;
            }

            try
            {
                var services = await swarmService.GetSwarmServicesAsync();
                var match = services.FirstOrDefault(service =>
                                !string.IsNullOrWhiteSpace(ContainerId) &&
                                service.Id.Equals(ContainerId, StringComparison.OrdinalIgnoreCase))
                            ?? services.FirstOrDefault(service =>
                                service.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    IsDockerConnected = false;
                    StatusColor = "#808080";
                    SwarmServiceInspectJson = "Swarm service not found.";
                    SwarmTasks.Clear();
                    OnPropertyChanged(nameof(SwarmTaskSummary));
                    RaiseSwarmCommandStates();
                    return;
                }

                ContainerId = match.Id;
                Name = match.Name;
                ImageName = match.Image;
                PortInfo = match.Ports;
                SwarmMode = match.SwarmMode;
                SwarmDesiredReplicas = match.SwarmDesiredReplicas;
                SwarmRunningReplicas = match.SwarmRunningReplicas;
                TargetSwarmReplicas = SwarmDesiredReplicas;
                DetailStatus = PortInfo;
                StatusColor = match.StateColor;
                IsDockerConnected = true;
                IsRunning = true;
                IsPaused = false;

                object raw = await swarmService.InspectSwarmServiceRawAsync(ContainerId);
                SwarmServiceInspectJson = raw.ToString() ?? string.Empty;
                var tasks = await swarmService.GetSwarmServiceTasksAsync(ContainerId);
                SwarmTasks.Clear();
                foreach (var task in tasks)
                    SwarmTasks.Add(task);
                OnPropertyChanged(nameof(CanScaleSwarmService));
                OnPropertyChanged(nameof(SwarmReplicaSummary));
                OnPropertyChanged(nameof(SwarmTaskSummary));
                RaiseSwarmCommandStates();
            }
            catch (Exception ex)
            {
                SwarmServiceInspectJson = $"Swarm service refresh failed: {ex.Message}";
                _dialogService.ShowError($"Swarm service 갱신 실패:\n{ex.Message}", "Swarm Service");
            }
        }

        private async Task ExecuteScaleSwarmServiceAsync()
        {
            if (!IsSwarmService || _containerService is not ISwarmService swarmService) return;
            if (!CanScaleSwarmService)
            {
                _dialogService.ShowInfo("global mode service는 replica 수를 직접 조절할 수 없습니다.", "Swarm Scale");
                return;
            }

            try
            {
                await swarmService.ScaleSwarmServiceAsync(ContainerId, TargetSwarmReplicas);
                await RefreshSwarmServiceAsync();
                _dialogService.ShowInfo($"'{Name}' service replica를 {TargetSwarmReplicas}개로 변경했습니다.", "Swarm Scale");
                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Swarm scale 실패:\n{ex.Message}", "Swarm Scale");
            }
        }

        private async Task ExecuteRemoveSwarmServiceAsync()
        {
            if (!IsSwarmService || _containerService is not ISwarmService swarmService) return;
            if (!_dialogService.ShowConfirm(
                    $"Swarm service '{Name}'을 Docker에서 삭제하시겠습니까?\n시트의 노드도 함께 제거됩니다.",
                    "Remove Swarm Service"))
            {
                return;
            }

            try
            {
                await swarmService.RemoveSwarmServiceAsync(ContainerId);

                if (ParentSheet != null)
                    await ParentSheet.RemoveNodeAsync(this);

                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Swarm service 삭제 실패:\n{ex.Message}", "Remove Swarm Service");
            }
        }

        private void RaiseSwarmCommandStates()
        {
            OnPropertyChanged(nameof(IsRuntimeUnavailable));
            OnPropertyChanged(nameof(IsOfflineSnapshot));
            OnPropertyChanged(nameof(CanControlSwarmService));
            OnPropertyChanged(nameof(CanScaleSwarmService));
            OnPropertyChanged(nameof(SwarmReplicaSummary));

            if (RefreshSwarmServiceCommand is AsyncRelayCommand refresh)
                refresh.RaiseCanExecuteChanged();
            if (ScaleSwarmServiceCommand is AsyncRelayCommand scale)
                scale.RaiseCanExecuteChanged();
            if (RemoveSwarmServiceCommand is AsyncRelayCommand remove)
                remove.RaiseCanExecuteChanged();
        }

        public async Task RefreshKubernetesPodAsync()
        {
            if (!IsKubernetesPod || _containerService is not IKubernetesService kubernetesService)
                return;

            if (IsRuntimeUnavailable)
            {
                var message = "현재 시트는 오프라인 스냅샷입니다. 런타임을 사용할 수 있을 때 다시 동기화해 주세요.";
                KubernetesPodDescribeText = message;
                KubernetesPodYamlText = message;
                KubernetesPodJsonText = message;
                ContainerLogs = message;
                return;
            }

            try
            {
                var pods = await kubernetesService.GetKubernetesPodsAsync();
                var match = pods.FirstOrDefault(pod =>
                                !string.IsNullOrWhiteSpace(ContainerId) &&
                                pod.Id.Equals(ContainerId, StringComparison.OrdinalIgnoreCase))
                            ?? pods.FirstOrDefault(pod =>
                                pod.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    IsDockerConnected = false;
                    StatusColor = "#808080";
                    KubernetesPodDescribeText = "Kubernetes Pod not found.";
                    KubernetesPodYamlText = "Kubernetes Pod not found.";
                    KubernetesPodJsonText = "Kubernetes Pod not found.";
                    ContainerLogs = "Kubernetes Pod not found.";
                    RaiseKubernetesCommandStates();
                    return;
                }

                ContainerId = match.Id;
                Name = match.Name;
                ImageName = match.Image;
                PortInfo = match.Ports;
                DetailStatus = match.State;
                StatusColor = match.StateColor;
                KubernetesNamespace = match.KubernetesNamespace;
                KubernetesNodeName = match.KubernetesNodeName;
                KubernetesReady = match.KubernetesReady;
                KubernetesRestarts = match.KubernetesRestarts;
                KubernetesPodIp = match.KubernetesPodIp;
                IsDockerConnected = true;
                IsRunning = match.State.Equals("Running", StringComparison.OrdinalIgnoreCase);
                IsPaused = false;

                object raw = await kubernetesService.InspectKubernetesPodRawAsync(KubernetesNamespace, KubernetesPodName);
                KubernetesPodJsonText = raw.ToString() ?? string.Empty;
                KubernetesPodYamlText = await kubernetesService.GetKubernetesPodYamlAsync(KubernetesNamespace, KubernetesPodName);
                KubernetesPodDescribeText = await kubernetesService.DescribeKubernetesPodAsync(KubernetesNamespace, KubernetesPodName);
                ContainerLogs = await kubernetesService.GetKubernetesPodLogsAsync(KubernetesNamespace, KubernetesPodName, 500);
                if (string.IsNullOrWhiteSpace(ContainerLogs))
                    ContainerLogs = "(No logs found)";

                RaiseKubernetesCommandStates();
            }
            catch (Exception ex)
            {
                var message = $"Kubernetes Pod refresh failed: {ex.Message}";
                KubernetesPodDescribeText = message;
                KubernetesPodYamlText = message;
                KubernetesPodJsonText = message;
                ContainerLogs = message;
                _dialogService.ShowError($"Kubernetes Pod 갱신 실패:\n{ex.Message}", "Kubernetes Pod");
            }
        }

        public async Task RefreshKubernetesResourceAsync()
        {
            if (!IsKubernetesResource || _containerService is not IKubernetesService kubernetesService)
                return;

            if (IsKubernetesPod)
            {
                await RefreshKubernetesPodAsync();
                return;
            }

            if (IsRuntimeUnavailable)
            {
                var message = "현재 시트는 오프라인 스냅샷입니다. 런타임을 사용할 수 있을 때 다시 동기화해 주세요.";
                KubernetesPodDescribeText = message;
                KubernetesPodYamlText = message;
                KubernetesPodJsonText = message;
                return;
            }

            try
            {
                object raw = await kubernetesService.InspectKubernetesResourceRawAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);
                KubernetesPodJsonText = raw.ToString() ?? string.Empty;
                KubernetesPodYamlText = await kubernetesService.GetKubernetesResourceYamlAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);
                KubernetesPodDescribeText = await kubernetesService.DescribeKubernetesResourceAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);
                UpdateKubernetesReplicaStateFromJson();
                IsDockerConnected = true;
                RaiseKubernetesCommandStates();
            }
            catch (Exception ex)
            {
                var message = $"Kubernetes resource refresh failed: {ex.Message}";
                KubernetesPodDescribeText = message;
                KubernetesPodYamlText = message;
                KubernetesPodJsonText = message;
                _dialogService.ShowError($"Kubernetes 리소스 갱신 실패:\n{ex.Message}", "Kubernetes Resource");
            }
        }

        private async Task ExecuteScaleKubernetesDeploymentAsync()
        {
            if (!CanScaleKubernetesDeployment || _containerService is not IKubernetesService kubernetesService)
                return;

            try
            {
                await kubernetesService.ScaleKubernetesDeploymentAsync(
                    KubernetesNamespace,
                    KubernetesResourceName,
                    TargetKubernetesReplicas);
                await RefreshKubernetesResourceAsync();
                _dialogService.ShowInfo($"'{KubernetesResourceName}' deployment replica를 {TargetKubernetesReplicas}개로 변경했습니다.", "Kubernetes Scale");
                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes scale 실패:\n{ex.Message}", "Kubernetes Scale");
            }
        }

        private async Task ExecuteRestartKubernetesRolloutAsync()
        {
            if (!CanRestartKubernetesRollout || _containerService is not IKubernetesService kubernetesService)
                return;

            if (!_dialogService.ShowConfirm(
                    $"'{KubernetesResourceName}' deployment를 rollout restart 하시겠습니까?",
                    "Kubernetes Rollout Restart"))
            {
                return;
            }

            try
            {
                await kubernetesService.RolloutRestartKubernetesResourceAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);
                await RefreshKubernetesResourceAsync();
                _dialogService.ShowInfo($"'{KubernetesResourceName}' deployment rollout restart를 요청했습니다.", "Kubernetes Rollout Restart");
                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes rollout restart 실패:\n{ex.Message}", "Kubernetes Rollout Restart");
            }
        }

        private async Task ExecuteDeleteKubernetesResourceAsync()
        {
            if (!CanDeleteKubernetesResource || _containerService is not IKubernetesService kubernetesService)
                return;

            string kind = string.IsNullOrWhiteSpace(KubernetesKind) ? KubernetesApiResource : KubernetesKind;
            if (!_dialogService.ShowConfirm(
                    $"{kind} '{KubernetesNamespace}/{KubernetesResourceName}'을 Kubernetes에서 삭제하시겠습니까?\n시트의 노드도 함께 제거됩니다.",
                    "Delete Kubernetes Resource"))
            {
                return;
            }

            try
            {
                await kubernetesService.DeleteKubernetesResourceAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);

                if (ParentSheet != null)
                    await ParentSheet.RemoveNodeAsync(this);

                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes 리소스 삭제 실패:\n{ex.Message}", "Delete Kubernetes Resource");
            }
        }

        private void ExecuteOpenKubernetesLogsFollow()
        {
            if (!CanOpenKubernetesLogsFollow || _containerService is not IKubernetesService kubernetesService)
                return;

            try
            {
                kubernetesService.OpenKubernetesLogsFollow(KubernetesNamespace, KubernetesPodName);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes live logs 실행 실패:\n{ex.Message}", "Kubernetes Logs");
            }
        }

        private void ExecuteOpenKubernetesPortForward()
        {
            if (!CanOpenKubernetesPortForward || _containerService is not IKubernetesService kubernetesService)
                return;

            var (defaultLocalPort, defaultRemotePort) = GetDefaultKubernetesPortForwardPorts();
            if (!_dialogService.TryShowKubernetesPortForwardDialog(
                    KubernetesKind,
                    $"{KubernetesNamespace}/{KubernetesResourceName}",
                    defaultLocalPort,
                    defaultRemotePort,
                    out int localPort,
                    out int remotePort))
            {
                return;
            }

            try
            {
                kubernetesService.OpenKubernetesPortForward(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName,
                    localPort,
                    remotePort);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes port-forward 실행 실패:\n{ex.Message}", "Kubernetes Port Forward");
            }
        }

        private async Task ExecuteExportKubernetesYamlAsync()
        {
            if (!IsKubernetesResource)
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(KubernetesPodYamlText) && CanRefreshKubernetesResource)
                    await RefreshKubernetesResourceAsync();

                if (string.IsNullOrWhiteSpace(KubernetesPodYamlText))
                {
                    _dialogService.ShowInfo("내보낼 Kubernetes YAML이 없습니다. 리소스를 먼저 갱신해 주세요.", "Export Kubernetes YAML");
                    return;
                }

                string defaultName = $"{SanitizeFileName(KubernetesKind)}-{SanitizeFileName(KubernetesResourceName)}.yaml";
                string? path = _dialogService.ShowSaveFileDialog(
                    "Kubernetes YAML (*.yaml)|*.yaml|YAML (*.yml)|*.yml|All files (*.*)|*.*",
                    ".yaml",
                    defaultName,
                    "Export Kubernetes YAML");
                if (string.IsNullOrWhiteSpace(path))
                    return;

                File.WriteAllText(path, KubernetesPodYamlText, Encoding.UTF8);
                _dialogService.ShowInfo($"Kubernetes YAML을 내보냈습니다.\n{path}", "Export Kubernetes YAML");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes YAML 내보내기 실패:\n{ex.Message}", "Export Kubernetes YAML");
            }
        }

        private async Task ExecuteApplyKubernetesManifestAsync()
        {
            if (!CanApplyKubernetesManifest || _containerService is not IKubernetesService kubernetesService)
                return;

            string? path = _dialogService.ShowOpenFileDialog(
                "Kubernetes YAML (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*",
                "Apply Kubernetes Manifest");
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!_dialogService.ShowConfirm(
                    $"선택한 manifest를 현재 Kubernetes context에 적용하시겠습니까?\n{path}",
                    "Apply Kubernetes Manifest"))
            {
                return;
            }

            try
            {
                await kubernetesService.ApplyKubernetesManifestAsync(path);
                await RefreshKubernetesResourceAsync();
                _dialogService.ShowInfo("Kubernetes manifest를 적용했습니다.", "Apply Kubernetes Manifest");
                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes manifest 적용 실패:\n{ex.Message}", "Apply Kubernetes Manifest");
            }
        }

        private void UpdateKubernetesReplicaStateFromJson()
        {
            if (!IsKubernetesDeployment || string.IsNullOrWhiteSpace(KubernetesPodJsonText))
                return;

            try
            {
                var raw = JObject.Parse(KubernetesPodJsonText);
                KubernetesDesiredReplicas = raw["spec"]?["replicas"]?.Value<int>() ?? KubernetesDesiredReplicas;
                KubernetesReadyReplicas = raw["status"]?["readyReplicas"]?.Value<int>() ?? 0;
                TargetKubernetesReplicas = KubernetesDesiredReplicas;
                OnPropertyChanged(nameof(KubernetesReplicaSummary));
            }
            catch
            {
                // 저장된 스냅샷의 JSON이 비어 있거나 오래된 형식이어도 상세 창은 계속 열려야 합니다.
            }
        }

        private (int LocalPort, int RemotePort) GetDefaultKubernetesPortForwardPorts()
        {
            const int fallbackLocal = 8080;
            const int fallbackRemote = 80;

            try
            {
                if (string.IsNullOrWhiteSpace(KubernetesPodJsonText))
                    return (fallbackLocal, fallbackRemote);

                var raw = JObject.Parse(KubernetesPodJsonText);
                int? remotePort = KubernetesKind.Equals("Service", StringComparison.OrdinalIgnoreCase)
                    ? raw["spec"]?["ports"]?.OfType<JObject>().Select(port => port["port"]?.Value<int>()).FirstOrDefault(port => port.HasValue)
                    : raw.SelectTokens("$..containers[*].ports[*].containerPort").Select(token => token.Value<int?>()).FirstOrDefault(port => port.HasValue);

                int resolvedRemote = remotePort.GetValueOrDefault(fallbackRemote);
                int resolvedLocal = resolvedRemote is 80 or 443 ? 8080 : resolvedRemote;
                return (resolvedLocal, resolvedRemote);
            }
            catch
            {
                return (fallbackLocal, fallbackRemote);
            }
        }

        private static string SanitizeFileName(string value)
        {
            string fallback = string.IsNullOrWhiteSpace(value) ? "resource" : value;
            var invalid = Path.GetInvalidFileNameChars();
            var chars = fallback.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
            string sanitized = new string(chars).Trim('-', ' ');
            return string.IsNullOrWhiteSpace(sanitized) ? "resource" : sanitized;
        }

        private void RaiseKubernetesCommandStates()
        {
            OnPropertyChanged(nameof(IsRuntimeUnavailable));
            OnPropertyChanged(nameof(IsOfflineSnapshot));
            OnPropertyChanged(nameof(IsDockerRuntimeContainer));
            OnPropertyChanged(nameof(IsGenericKubernetesResource));
            OnPropertyChanged(nameof(IsKubernetesResource));
            OnPropertyChanged(nameof(CanRefreshKubernetesResource));
            OnPropertyChanged(nameof(CanRefreshKubernetesPod));
            OnPropertyChanged(nameof(IsKubernetesDeployment));
            OnPropertyChanged(nameof(CanScaleKubernetesDeployment));
            OnPropertyChanged(nameof(CanRestartKubernetesRollout));
            OnPropertyChanged(nameof(CanDeleteKubernetesResource));
            OnPropertyChanged(nameof(CanOpenKubernetesLogsFollow));
            OnPropertyChanged(nameof(CanOpenKubernetesPortForward));
            OnPropertyChanged(nameof(CanExportKubernetesYaml));
            OnPropertyChanged(nameof(CanApplyKubernetesManifest));
            OnPropertyChanged(nameof(KubernetesReplicaSummary));
            OnPropertyChanged(nameof(KubernetesPodName));
            OnPropertyChanged(nameof(KubernetesResourceName));

            if (RefreshKubernetesPodCommand is AsyncRelayCommand refresh)
                refresh.RaiseCanExecuteChanged();
            if (RefreshKubernetesResourceCommand is AsyncRelayCommand refreshResource)
                refreshResource.RaiseCanExecuteChanged();
            if (ScaleKubernetesDeploymentCommand is AsyncRelayCommand scale)
                scale.RaiseCanExecuteChanged();
            if (RestartKubernetesRolloutCommand is AsyncRelayCommand restart)
                restart.RaiseCanExecuteChanged();
            if (DeleteKubernetesResourceCommand is AsyncRelayCommand delete)
                delete.RaiseCanExecuteChanged();
            if (OpenKubernetesLogsFollowCommand is RelayCommand logsFollow)
                logsFollow.RaiseCanExecuteChanged();
            if (OpenKubernetesPortForwardCommand is RelayCommand portForward)
                portForward.RaiseCanExecuteChanged();
            if (ExportKubernetesYamlCommand is AsyncRelayCommand exportYaml)
                exportYaml.RaiseCanExecuteChanged();
            if (ApplyKubernetesManifestCommand is AsyncRelayCommand applyManifest)
                applyManifest.RaiseCanExecuteChanged();
        }

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
        /// <summary>
        /// Docker ID를 화면 표시 길이로 줄입니다.
        /// </summary>
        private static string ShortenId(string? id, int len = 12)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            return id.Length <= len ? id : id.Substring(0, len);
        }

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
        public Task StartLogStreamAsync(Action<string> onLogReceived) =>
            _containerOperations.StartLogStreamAsync(onLogReceived);

        public void StopLogStream() => _containerOperations.StopLogStream();
        #endregion
    }
}
