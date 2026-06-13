using DockerDiagram.Helpers;
using DockerDiagram.Models;
using OxyPlot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 노드의 공통 배치·선택 상태를 관리하고 타입별 자식 ViewModel을 조정합니다.
    /// </summary>
    public class NodeViewModel : ViewModelBase, IConnectableItem
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

        // 마운트 정보 원본 저장용 (캐시)
        private SheetViewModel? _parentSheet;

        private double _x;
        private double _y;
        private double _width = 160;
        private double _height = 80;
        private bool _isSelected;
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

        private int _networkDisplayMode = 0;
        private int _volumeDisplayMode = 0;
        private List<string> _portBindings = new();
        private List<string> _environmentVariables = new();
        private string _hostFilePath = @"C:\temp\";
        private string _containerFilePath = @"/app/data";
        private string _newEnvInput = "";

        #endregion

        #region Events
        public event EventHandler? OnPositionChanged;
        public event EventHandler? OnModified;
        #endregion

        #region Layout Properties
        public double X
        {
            get => _x;
            set
            {
                double newVal = Math.Round(value / GRID_SIZE) * GRID_SIZE;
                if (_x != newVal)
                {
                    _x = newVal;
                    OnPropertyChanged();
                    OnPositionChanged?.Invoke(this, EventArgs.Empty);
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                double newVal = Math.Round(value / GRID_SIZE) * GRID_SIZE;
                if (_y != newVal)
                {
                    _y = newVal;
                    OnPropertyChanged();
                    OnPositionChanged?.Invoke(this, EventArgs.Empty);
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double Width
        {
            get => _width;
            set
            {
                double val = Math.Max(MIN_SIZE, Math.Round(value / GRID_SIZE) * GRID_SIZE);
                if (_width != val)
                {
                    _width = val;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CenterX));
                    OnPositionChanged?.Invoke(this, EventArgs.Empty);
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double Height
        {
            get => _height;
            set
            {
                double val = Math.Max(MIN_SIZE, Math.Round(value / GRID_SIZE) * GRID_SIZE);
                if (_height != val)
                {
                    _height = val;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CenterY));
                    OnPositionChanged?.Invoke(this, EventArgs.Empty);
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double CenterX => X + (Width / 2);
        public double CenterY => Y + (Height / 2);

        /// <summary>
        /// 사용자가 화면에서 이 노드를 클릭하여 선택했는지 여부를 나타냅니다.
        /// 선택 시 상세 정보 패널이 열리며, 컨테이너 리소스 모니터링 타이머가 자동으로 시작됩니다.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    if (value)
                    {
                        StartMonitoring();
                        _ = RefreshDetailsAsync();
                    }
                    else
                    {
                        StopMonitoring();
                    }
                }
            }
        }

        /// <summary>
        /// 이 노드가 배치된 부모 도화지(Sheet) 객체를 참조합니다.
        /// 시트가 할당되거나 변경될 때마다 선 연결(Connector) 변경 이벤트를 감지하도록 구독하여, 연결 상태 문자열을 실시간으로 갱신합니다.
        /// </summary>
        public SheetViewModel? ParentSheet
        {
            get => _parentSheet;
            set
            {
                if (_parentSheet != value)
                {
                    if (_parentSheet != null)
                        _parentSheet.Connectors.CollectionChanged -= Connectors_CollectionChanged;

                    _parentSheet = value;

                    if (_parentSheet != null)
                        _parentSheet.Connectors.CollectionChanged += Connectors_CollectionChanged;

                    RefreshConnections();
                }
            }
        }
        #endregion

        #region Basic Info Properties
        /// <summary>
        /// 이 노드의 역할을 정의합니다. (컨테이너, 볼륨, 또는 인터넷)
        /// </summary>
        public NodeType Type { get; init; }

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    OnPropertyChanged(nameof(EffectiveVolumeName));
                }
            }
        }
        public string ImageName { get; set; } = string.Empty;
        public string PortInfo { get; set; } = string.Empty;
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ComposeServiceName { get; set; } = string.Empty;
        public string ComposeRawServiceYaml { get; set; } = string.Empty;
        public string ComposeRawVolumeYaml { get; set; } = string.Empty;
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
                }
            }
        }

        public bool IsDockerConnected
        {
            get => _isDockerConnected;
            set
            {
                if (SetProperty(ref _isDockerConnected, value))
                {
                    OnPropertyChanged(nameof(IsDockerDisconnected));
                    RaiseCommandStates();
                }
            }
        }

        public bool IsDockerDisconnected => Type != NodeType.Internet && !IsCreating && !IsDockerConnected;

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
        #endregion

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

        internal void NotifyModified() => OnModified?.Invoke(this, EventArgs.Empty);
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
        #endregion

        #region Constructor
        /// <summary>
        /// 공통 노드 상태와 타입별 기능 ViewModel을 초기화합니다.
        /// </summary>
        public NodeViewModel(IContainerService containerService, IVolumeService volumeService, IDialogService dialogService)
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
        public void StartMonitoring()
        {
            if (Type != NodeType.Container || string.IsNullOrEmpty(ContainerId)) return;
            _monitoring.Start();
        }

        /// <summary>
        /// 노드 선택이 해제되면 불필요한 백그라운드 통신을 줄이기 위해 리소스 모니터링 타이머를 정지합니다.
        /// </summary>
        public void StopMonitoring() => _monitoring.Stop();

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
