using DockerDiagram.Helpers;
using DockerDiagram.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 다이어그램 캔버스 위에 배치되는 개별 노드(컨테이너, 볼륨, 인터넷)의 시각적 상태와 도커 데이터를 관리하는 핵심 뷰모델입니다.
    /// 단순한 UI 표현을 넘어 도커 엔진과 실시간으로 동기화(CPU/메모리 모니터링, 상태 갱신)를 수행하며,
    /// 시작/정지, 터미널 접속, 로그 조회 등 사용자의 제어 명령을 백엔드 서비스로 전달하는 컨트롤 센터 역할을 합니다.
    /// </summary>
    public class NodeViewModel : ViewModelBase, IConnectableItem
    {
        #region Constants & Fields
        private const int GRID_SIZE = 10;
        private const double MIN_SIZE = 50;

        private readonly IContainerService _containerService;
        private readonly IVolumeService _volumeService;
        private readonly IDialogService _dialogService;

        // 마운트 정보 원본 저장용 (캐시)
        private List<Docker.DotNet.Models.MountPoint> _cachedMounts = new();
        private DispatcherTimer? _statsTimer;
        private CancellationTokenSource? _logStreamCts;
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

        private double _maxCpuCount = 8.0;
        private double _targetCpuCount = 1.0;
        private long _maxMemoryMb = 8192;
        private long _targetMemoryMb = 512;
        private string _cpuUsage = "0.0%";
        private double _cpuValue = 0;
        private string _memoryUsage = "0B / 0B";
        private double _memoryValue = 0;

        private int _networkDisplayMode = 0;
        private int _volumeDisplayMode = 0;
        private List<string> _portBindings = new();
        private List<string> _environmentVariables = new();
        private string _hostFilePath = @"C:\temp\";
        private string _containerFilePath = @"/app/data";
        private string _newEnvInput = "";

        private LineSeries _cpuSeries;
        private LineSeries _memorySeries;
        private int _timeIndex = 0; // X축 기준점이 될 시간 흐름 인덱스
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

        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public string ImageName { get; set; } = string.Empty;
        public string PortInfo { get; set; } = string.Empty;
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ComposeServiceName { get; set; } = string.Empty;
        public string ComposeRawServiceYaml { get; set; } = string.Empty;

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
        public double MaxCpuCount { get => _maxCpuCount; set => SetProperty(ref _maxCpuCount, value); }
        public double TargetCpuCount { get => _targetCpuCount; set => SetProperty(ref _targetCpuCount, value); }
        public long MaxMemoryMb { get => _maxMemoryMb; set => SetProperty(ref _maxMemoryMb, value); }
        public long TargetMemoryMb { get => _targetMemoryMb; set => SetProperty(ref _targetMemoryMb, value); }
        public string CpuUsage { get => _cpuUsage; set => SetProperty(ref _cpuUsage, value); }
        public double CpuValue { get => _cpuValue; set => SetProperty(ref _cpuValue, value); }
        public string MemoryUsage { get => _memoryUsage; set => SetProperty(ref _memoryUsage, value); }
        public double MemoryValue
        {
            get => _memoryValue;
            set => SetProperty(ref _memoryValue, double.IsNaN(value) ? 0 : value);
        }

        public PlotModel CpuPlotModel { get; private set; }
        public PlotModel MemoryPlotModel { get; private set; }
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
                    UpdateVolumeList(); // 모드가 바뀌면 리스트 내용 즉시 갱신
                }
            }
        }

        public class NetworkDetail
        {
            public string NetworkName { get; set; } = "";
            public string IPv4 { get; set; } = "-";
            public string IPv6 { get; set; } = "-";
        }

        public ObservableCollection<NetworkDetail> NetworkDetailList { get; } = new();
        public Dictionary<string, string> NetworkIpMap { get; private set; } = new();
        public ObservableCollection<string> MountedVolumeList { get; } = new();
        public ObservableCollection<string> ConnectedNodes { get; } = new();
        public ObservableCollection<string> UsedByContainers { get; } = new();

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

        #region Commands
        public AsyncRelayCommand StartCommand { get; }
        public AsyncRelayCommand StopCommand { get; }
        public AsyncRelayCommand PauseCommand { get; }
        public AsyncRelayCommand RestartCommand { get; }
        public RelayCommand TerminalCommand { get; }
        public ICommand ToggleNetworkModeCommand { get; }
        public ICommand ToggleVolumeModeCommand { get; }
        public ICommand OpenDetailWindowCommand { get; }
        public ICommand RefreshLogsCommand { get; }
        public ICommand CopyLogsCommand { get; }
        public ICommand ExportLogsCommand { get; }
        public ICommand CopyToContainerCommand { get; }
        public ICommand CopyFromContainerCommand { get; }
        public ICommand AddEnvAndRecreateCommand { get; }
        public ICommand BackupVolumeCommand { get; }
        public ICommand RestoreVolumeCommand { get; }
        public AsyncRelayCommand ExtractDockerfileCommand { get; }
        public ICommand UpdateResourcesCommand { get; }
        #endregion

        #region Constructor
        /// <summary>
        /// 도커 통신에 필요한 백엔드 서비스(컨테이너, 볼륨) 및 알림(다이얼로그) 서비스를 주입받아 객체를 초기화합니다.
        /// UI 버튼과 연결될 수많은 명령(제어, 터미널 열기, 로그 복사, 파일 업/다운로드, Dockerfile 추출 등)을 여기서 세팅합니다.
        /// </summary>
        public NodeViewModel(IContainerService containerService, IVolumeService volumeService, IDialogService dialogService)
        {
            _containerService = containerService ?? throw new ArgumentNullException(nameof(containerService));
            _volumeService = volumeService ?? throw new ArgumentNullException(nameof(volumeService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            BackupVolumeCommand = new AsyncRelayCommand(ExecuteBackupVolumeAsync, _ => Type == NodeType.Volume);
            RestoreVolumeCommand = new AsyncRelayCommand(ExecuteRestoreVolumeAsync, _ => Type == NodeType.Volume);

            StartCommand = new AsyncRelayCommand(_ => ControlAction("start"), _ => Type == NodeType.Container && IsDockerConnected && !IsRunning);
            StopCommand = new AsyncRelayCommand(_ => ControlAction("stop"), _ => Type == NodeType.Container && IsDockerConnected && IsRunning);
            PauseCommand = new AsyncRelayCommand(_ => ControlAction("pause"), _ => Type == NodeType.Container && IsDockerConnected && (IsRunning || IsPaused));
            RestartCommand = new AsyncRelayCommand(_ => ControlAction("restart"), _ => Type == NodeType.Container && IsDockerConnected);

            UpdateResourcesCommand = new AsyncRelayCommand(ExecuteUpdateResourcesAsync, _ => Type == NodeType.Container && IsDockerConnected);

            TerminalCommand = new RelayCommand(_ => OpenTerminal(), _ => Type == NodeType.Container && IsDockerConnected && IsRunning);
            ExtractDockerfileCommand = new AsyncRelayCommand(_ => ExtractDockerfileAsync(), _ => Type == NodeType.Container && IsDockerConnected);

            ToggleNetworkModeCommand = new RelayCommand(_ => {
                NetworkDisplayMode = (NetworkDisplayMode + 1) % 3;
            });

            ToggleVolumeModeCommand = new RelayCommand(_ => {
                VolumeDisplayMode = (VolumeDisplayMode + 1) % 2;
            });

            OpenDetailWindowCommand = new AsyncRelayCommand(_ => OpenDetailWindowAsync(), _ => Type == NodeType.Container && IsDockerConnected);
            RefreshLogsCommand = new AsyncRelayCommand(_ => LoadLogsAsync(), _ => Type == NodeType.Container && IsDockerConnected);

            CopyLogsCommand = new RelayCommand(_ => {
                if (!string.IsNullOrEmpty(ContainerLogs))
                {
                    Clipboard.SetText(ContainerLogs);
                    _dialogService.ShowInfo("로그가 클립보드에 복사되었습니다.", "복사 완료");
                }
            });

            ExportLogsCommand = new RelayCommand(_ => {
                if (string.IsNullOrEmpty(ContainerLogs)) return;

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Text File|*.txt",
                    FileName = $"{Name}_logs.txt",
                    Title = "Export Logs"
                };

                if (dlg.ShowDialog() == true)
                {
                    File.WriteAllText(dlg.FileName, ContainerLogs);
                    _dialogService.ShowInfo("로그가 파일로 저장되었습니다.", "저장 완료");
                }
            });

            CopyToContainerCommand = new AsyncRelayCommand(async _ => {
                if (string.IsNullOrWhiteSpace(HostFilePath) || string.IsNullOrWhiteSpace(ContainerFilePath)) return;
                try
                {
                    await _containerService.CopyToContainerAsync(ContainerId, HostFilePath, ContainerFilePath);
                    _dialogService.ShowInfo("컨테이너로 파일 복사가 완료되었습니다.", "업로드 성공");
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage($"업로드 실패: {ex.Message}");
                }
            });

            CopyFromContainerCommand = new AsyncRelayCommand(async _ => {
                if (string.IsNullOrWhiteSpace(HostFilePath) || string.IsNullOrWhiteSpace(ContainerFilePath)) return;
                try
                {
                    await _containerService.CopyFromContainerAsync(ContainerId, ContainerFilePath, HostFilePath);
                    _dialogService.ShowInfo("컨테이너에서 파일 다운로드가 완료되었습니다.", "다운로드 성공");
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage($"다운로드 실패: {ex.Message}");
                }
            });

            AddEnvAndRecreateCommand = new RelayCommand(_ => {
                if (string.IsNullOrWhiteSpace(NewEnvInput)) return;
                _dialogService.ShowInfo($"이 기능은 기존 설정을 바탕으로 컨테이너를 삭제하고 [{NewEnvInput}] 환경변수를 추가하여 다시 생성합니다.\n(기능 연결 필요)", "Recreate");
            });

            // =========================================================
            // ★ [마이그레이션 완료] OxyPlot 초기화 세팅
            // =========================================================

            // 1. CPU 차트 모델 및 축 생성
            CpuPlotModel = new PlotModel { PlotMargins = new OxyThickness(0) };
            CpuPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Maximum = 100, IsAxisVisible = true, MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = OxyColors.LightGray });
            CpuPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, IsAxisVisible = false }); // X축 숨김

            _cpuSeries = new LineSeries { Color = OxyColor.Parse("#28a745"), StrokeThickness = 2, MarkerType = MarkerType.None };
            CpuPlotModel.Series.Add(_cpuSeries);

            // 2. Memory 차트 모델 및 축 생성
            MemoryPlotModel = new PlotModel { PlotMargins = new OxyThickness(0) };
            MemoryPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, IsAxisVisible = true, MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = OxyColors.LightGray });
            MemoryPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, IsAxisVisible = false }); // X축 숨김

            _memorySeries = new LineSeries { Color = OxyColor.Parse("#007ACC"), StrokeThickness = 2, MarkerType = MarkerType.None };
            MemoryPlotModel.Series.Add(_memorySeries);
        }
        #endregion

        #region Public Methods (Lifecycle & Monitoring)
        /// <summary>
        /// 도커 엔진 API를 호출하여 이 노드(컨테이너 또는 볼륨)의 최신 상세 정보(상태, IP, 포트, 마운트 내역, 헬스 체크 등)를 가져와 UI 속성에 동기화합니다.
        /// 패널이 열리거나, 상태 변경 이벤트가 발생할 때 호출되어 다이어그램이 항상 실제 도커 환경과 동일한 팩트(Fact)를 유지하도록 합니다.
        /// </summary>
        public async Task RefreshDetailsAsync()
        {
            if (string.IsNullOrEmpty(Name)) return;
            if (Type == NodeType.Internet)
            {
                IsDockerConnected = true;
                return;
            }

            try
            {
                if (Type == NodeType.Container)
                {
                    if (string.IsNullOrEmpty(ContainerId))
                    {
                        IsDockerConnected = false;
                        return;
                    }

                    var info = await _containerService.InspectContainerAsync(ContainerId);
                    IsDockerConnected = true;

                    // =====================================================================
                    // ★ 도커 엔진(Daemon) 기준 실제 스펙 및 현재 할당값 동기화 ★
                    // =====================================================================
                    try
                    {
                        var dockerSystemInfo = await _containerService.GetSystemInfoAsync();
                        MaxCpuCount = dockerSystemInfo.NCPU > 0 ? dockerSystemInfo.NCPU : Environment.ProcessorCount;
                        MaxMemoryMb = dockerSystemInfo.MemTotal > 0 ? (dockerSystemInfo.MemTotal / 1048576) : 32768;
                    }
                    catch
                    {
                        MaxCpuCount = Environment.ProcessorCount;
                        MaxMemoryMb = 32768;
                    }

                    if (info.HostConfig != null)
                    {
                        long currentMem = info.HostConfig.Memory;
                        TargetMemoryMb = currentMem > 0 ? (currentMem / 1048576) : MaxMemoryMb;

                        long currentCpu = info.HostConfig.NanoCPUs;
                        TargetCpuCount = currentCpu > 0 ? (currentCpu / 1_000_000_000.0) : MaxCpuCount;
                    }
                    // =====================================================================

                    DetailStatus = info.State.Status;
                    IsRunning = info.State.Running;
                    IsPaused = info.State.Paused;

                    StartedAt = DateTime.TryParse(info.State.StartedAt, out var sTime) ? sTime.ToString("yyyy-MM-dd HH:mm:ss") : info.State.StartedAt;
                    FinishedAt = DateTime.TryParse(info.State.FinishedAt, out var fTime) ? fTime.ToString("yyyy-MM-dd HH:mm:ss") : info.State.FinishedAt;

                    CreatedDate = info.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

                    if (IsRunning && DateTime.TryParse(info.State.StartedAt, out var startTime))
                    {
                        var duration = DateTime.UtcNow - startTime.ToUniversalTime();
                        Uptime = $"Up {duration.Days}d {duration.Hours}h {duration.Minutes}m";
                    }
                    else
                    {
                        Uptime = $"Created {info.Created.ToLocalTime():yy-MM-dd}";
                    }

                    if (info.State.Health != null && !string.IsNullOrEmpty(info.State.Health.Status))
                    {
                        string status = info.State.Health.Status.ToLower();
                        if (status == "healthy")
                        {
                            HealthStatus = "Healthy 💚";
                            HealthColor = "#28a745";
                        }
                        else if (status == "starting")
                        {
                            HealthStatus = "Starting 💛";
                            HealthColor = "#ffc107";
                        }
                        else if (status == "unhealthy")
                        {
                            HealthStatus = "Unhealthy 💔";
                            HealthColor = "#dc3545";
                        }
                        else
                        {
                            HealthStatus = info.State.Health.Status;
                            HealthColor = "#555555";
                        }
                    }
                    else
                    {
                        HealthStatus = "No Check";
                        HealthColor = "#888888";
                    }

                    if (info.HostConfig?.RestartPolicy != null)
                    {
                        string policy = info.HostConfig.RestartPolicy.Name.ToString().ToLower();
                        if (policy == "unlessstopped") policy = "unless-stopped";
                        else if (policy == "onfailure") policy = "on-failure";
                        RestartPolicy = policy;
                    }

                    if (info.Config?.Env != null)
                        this.EnvironmentVariables = info.Config.Env.ToList();
                    else
                        this.EnvironmentVariables = new List<string>();

                    var portsList = new List<string>();
                    if (info.HostConfig?.PortBindings != null)
                    {
                        foreach (var kvp in info.HostConfig.PortBindings)
                        {
                            string containerPort = kvp.Key.Replace("/tcp", "").Replace("/udp", "");
                            foreach (var binding in kvp.Value)
                            {
                                if (!string.IsNullOrEmpty(binding.HostPort))
                                    portsList.Add($"{binding.HostPort}:{containerPort}");
                            }
                        }
                    }
                    this.PortBindings = portsList;

                    var nets = new List<string>();
                    var ips = new List<string>();
                    NetworkDetailList.Clear();
                    NetworkIpMap.Clear();

                    if (info.NetworkSettings?.Networks != null)
                    {
                        foreach (var net in info.NetworkSettings.Networks)
                        {
                            nets.Add(net.Key);
                            string ipv4 = net.Value.IPAddress;
                            string ipv6 = net.Value.GlobalIPv6Address;

                            if (!string.IsNullOrEmpty(ipv4))
                            {
                                ips.Add(ipv4);
                                NetworkIpMap[net.Key] = ipv4;
                            }

                            NetworkDetailList.Add(new NetworkDetail
                            {
                                NetworkName = net.Key,
                                IPv4 = string.IsNullOrEmpty(ipv4) ? "-" : ipv4,
                                IPv6 = string.IsNullOrEmpty(ipv6) ? "-" : ipv6
                            });
                        }
                    }
                    ConnectedNetworksString = nets.Count > 0 ? string.Join(", ", nets) : "None";
                    IpAddresses = ips.Count > 0 ? string.Join(", ", ips) : "-";
                    IPAddress = ips.Count > 0 ? ips[0] : "-";

                    var vols = new List<string>();

                    if (info.Mounts != null)
                    {
                        _cachedMounts = info.Mounts.ToList();
                        foreach (var m in info.Mounts)
                        {
                            vols.Add($"{m.Source} -> {m.Destination}");
                        }
                    }
                    else
                    {
                        _cachedMounts = new List<Docker.DotNet.Models.MountPoint>();
                    }

                    UpdateVolumeList();
                    MountedVolumes = vols.Count > 0 ? string.Join("\n", vols) : "None";

                    if (IsRunning) StatusColor = "#28a745";
                    else if (IsPaused) StatusColor = "#ffc107";
                    else StatusColor = "#dc3545";

                    // =========================================================
                    // ★ [마이그레이션 완료] OxyPlot 전용 데이터 갱신 로직
                    // =========================================================
                    if (IsRunning)
                    {
                        var stats = await _containerService.GetContainerStatsAsync(ContainerId);

                        // ★ [추가됨] 화면의 텍스트(Usage) 값도 실제 통계 데이터로 갱신
                        CpuUsage = $"{stats.CpuPercentage:F1}%";
                        MemoryUsage = $"{stats.MemoryUsedMB:F1}MB / {stats.MemoryLimitMB:F1}MB";

                        // (선택적) 프로그레스 바 등 Value를 사용하는 다른 UI를 위한 데이터 갱신
                        CpuValue = stats.CpuPercentage;
                        MemoryValue = stats.MemoryLimitMB > 0 ? (stats.MemoryUsedMB / stats.MemoryLimitMB) * 100 : 0;

                        // 새 데이터 포인트 추가
                        _cpuSeries.Points.Add(new DataPoint(_timeIndex, stats.CpuPercentage));
                        _memorySeries.Points.Add(new DataPoint(_timeIndex, stats.MemoryUsedMB));

                        // 메모리 보호: 60개가 넘으면 제일 오래된 데이터를 잘라냅니다.
                        if (_cpuSeries.Points.Count > 60)
                        {
                            _cpuSeries.Points.RemoveAt(0);
                            _memorySeries.Points.RemoveAt(0);
                        }

                        _timeIndex++;

                        // 스크롤 효과를 위해 X축 범위 갱신
                        var cpuXAxis = CpuPlotModel.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
                        var memXAxis = MemoryPlotModel.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
                        if (cpuXAxis != null) { cpuXAxis.Minimum = _timeIndex - 60; cpuXAxis.Maximum = _timeIndex; }
                        if (memXAxis != null) { memXAxis.Minimum = _timeIndex - 60; memXAxis.Maximum = _timeIndex; }

                        // 차트를 다시 그리라고 엔진에 명령
                        CpuPlotModel.InvalidatePlot(true);
                        MemoryPlotModel.InvalidatePlot(true);
                    }
                    else
                    {
                        // ★ [추가됨] 컨테이너가 꺼져있을 때는 텍스트도 0으로 초기화하여 잔상 방지
                        CpuUsage = "0.0%";
                        MemoryUsage = "0.0MB / 0.0MB";
                        CpuValue = 0;
                        MemoryValue = 0;

                        // 꺼져있을 때는 차트가 바닥(0)으로 자연스럽게 떨어지도록 그립니다.
                        if (_cpuSeries.Points.Count > 0 && _cpuSeries.Points.Last().Y != 0)
                        {
                            _cpuSeries.Points.Add(new DataPoint(_timeIndex, 0));
                            _memorySeries.Points.Add(new DataPoint(_timeIndex, 0));
                            _timeIndex++;

                            var cpuXAxis = CpuPlotModel.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
                            var memXAxis = MemoryPlotModel.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
                            if (cpuXAxis != null) { cpuXAxis.Minimum = _timeIndex - 60; cpuXAxis.Maximum = _timeIndex; }
                            if (memXAxis != null) { memXAxis.Minimum = _timeIndex - 60; memXAxis.Maximum = _timeIndex; }

                            CpuPlotModel.InvalidatePlot(true);
                            MemoryPlotModel.InvalidatePlot(true);
                        }
                    }
                }
                else if (Type == NodeType.Volume)
                {
                    var vol = await _volumeService.InspectVolumeAsync(Name);
                    IsDockerConnected = true;

                    DetailStatus = "Created";
                    Driver = vol.Driver;
                    Mountpoint = vol.Mountpoint;
                    CreatedDate = DateTime.TryParse(vol.CreatedAt, out var cTime) ? cTime.ToString("yyyy-MM-dd HH:mm:ss") : vol.CreatedAt;

                    var usedList = await _volumeService.GetContainersUsingVolumeAsync(Name);
                    UsedByContainers.Clear();

                    if (usedList.Count > 0)
                    {
                        foreach (var u in usedList) UsedByContainers.Add(u);
                    }
                    else
                    {
                        UsedByContainers.Add("None");
                    }

                    IsRunning = false;
                    IsPaused = false;
                    StatusColor = "#E67E22";
                }

                CommandManager.InvalidateRequerySuggested();
                RefreshConnections();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Refresh Error: {ex.Message}");
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
                {
                    var containers = await _containerService.GetContainersAsync();
                    var match = containers.FirstOrDefault(c => !string.IsNullOrWhiteSpace(ContainerId) && c.Id == ContainerId)
                                ?? containers.FirstOrDefault(c => string.Equals(c.Name, Name, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        _dialogService.ShowInfo($"Docker에서 '{Name}' 컨테이너를 찾지 못했습니다.", "Reconnect");
                        IsDockerConnected = false;
                        return false;
                    }

                    ContainerId = match.Id;
                    Name = match.Name;
                    ImageName = match.Image;
                    PortInfo = match.Ports;
                    StatusColor = match.StateColor;
                    IsDockerConnected = true;
                    await RefreshDetailsAsync();
                    return true;
                }

                if (Type == NodeType.Volume)
                {
                    var volumes = await _volumeService.GetVolumesAsync();
                    var match = volumes.FirstOrDefault(v => string.Equals(v.Name, Name, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        _dialogService.ShowInfo($"Docker에서 '{Name}' 볼륨을 찾지 못했습니다.", "Reconnect");
                        IsDockerConnected = false;
                        return false;
                    }

                    Name = match.Name;
                    IsDockerConnected = true;
                    await RefreshDetailsAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                IsDockerConnected = false;
                _dialogService.ShowError($"Reconnect 실패: {ex.Message}", "Reconnect");
            }

            return false;
        }

        /// <summary>
        /// 이 컨테이너가 화면에서 선택(클릭)되었을 때 호출되며, 
        /// 2초 주기로 도커 엔진에 리소스 사용량을 질의하는 백그라운드 모니터링 타이머를 시작합니다.
        /// </summary>
        public void StartMonitoring()
        {
            if (Type != NodeType.Container || string.IsNullOrEmpty(ContainerId)) return;

            if (_statsTimer == null)
            {
                _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _statsTimer.Tick += async (s, e) => await UpdateStatsAsync();
            }
            _statsTimer.Start();
        }

        /// <summary>
        /// 노드 선택이 해제되면 불필요한 백그라운드 통신을 줄이기 위해 리소스 모니터링 타이머를 정지합니다.
        /// </summary>
        public void StopMonitoring() => _statsTimer?.Stop();

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
        /// 도커 엔진으로부터 현재 컨테이너의 실시간 CPU 및 메모리 사용량을 가져와 UI 프로그레스 바와 텍스트에 동기화합니다.
        /// </summary>
        private async Task UpdateStatsAsync()
        {
            if (!IsRunning) return;

            try
            {
                var stats = await _containerService.GetContainerStatsAsync(ContainerId);

                CpuUsage = $"{stats.CpuPercentage:F1}%";
                CpuValue = stats.CpuPercentage;

                MemoryUsage = $"{stats.MemoryUsedMB:F1}MB / {stats.MemoryLimitMB:F1}MB";

                if (stats.MemoryLimitMB > 0)
                {
                    MemoryValue = (stats.MemoryUsedMB / stats.MemoryLimitMB) * 100;
                }
                else
                {
                    MemoryValue = 0;
                }
            }
            catch { }
        }

        /// <summary>
        /// 사용자가 선택한 볼륨 표시 모드(도커 Named 볼륨 또는 로컬 Bind 마운트)에 맞추어, 
        /// 현재 컨테이너에 연결된 볼륨 목록을 필터링하고 UI 리스트에 갱신합니다.
        /// </summary>
        private void UpdateVolumeList()
        {
            MountedVolumeList.Clear();
            if (ParentSheet == null) return;

            var validVolumeNames = ParentSheet.Nodes.Where(n => n.Type == NodeType.Volume).Select(n => n.Name).ToHashSet();

            foreach (var m in _cachedMounts)
            {
                if (VolumeDisplayMode == 0)
                {
                    if (m.Type == "volume" && validVolumeNames.Contains(m.Name))
                        MountedVolumeList.Add($"{m.Name} : {m.Destination}");
                }
                else
                {
                    if (m.Type == "bind")
                        MountedVolumeList.Add($"{m.Source} -> {m.Destination}");
                }
            }
        }

        /// <summary>
        /// 시작, 정지, 일시정지, 재시작 등 사용자가 UI에서 내린 제어 명령(Action)을 
        /// 실제 도커 백엔드 서비스로 전달하여 실행하고, 완료 직후 상태를 갱신합니다.
        /// </summary>
        private async Task ControlAction(string action)
        {
            if (string.IsNullOrEmpty(ContainerId)) return;

            try
            {
                switch (action)
                {
                    case "start": await _containerService.StartContainerAsync(ContainerId); break;
                    case "stop": await _containerService.StopContainerAsync(ContainerId); break;
                    case "pause":
                        if (DetailStatus == "paused") await _containerService.UnpauseContainerAsync(ContainerId);
                        else await _containerService.PauseContainerAsync(ContainerId);
                        break;
                    case "restart": await _containerService.RestartContainerAsync(ContainerId); break;
                }
                await RefreshDetailsAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"동작 실패 : {ex.Message}");
            }
        }

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
            StartCommand?.RaiseCanExecuteChanged();
            StopCommand?.RaiseCanExecuteChanged();
            PauseCommand?.RaiseCanExecuteChanged();
            RestartCommand?.RaiseCanExecuteChanged();
            TerminalCommand?.RaiseCanExecuteChanged();
            if (UpdateResourcesCommand is AsyncRelayCommand updateResourcesCommand)
                updateResourcesCommand.RaiseCanExecuteChanged();
            ExtractDockerfileCommand?.RaiseCanExecuteChanged();
            if (OpenDetailWindowCommand is AsyncRelayCommand openDetailWindowCommand)
                openDetailWindowCommand.RaiseCanExecuteChanged();
            if (RefreshLogsCommand is AsyncRelayCommand refreshLogsCommand)
                refreshLogsCommand.RaiseCanExecuteChanged();
        }

        private void Connectors_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshConnections();
        }
        #endregion

        #region Feature Methods (UI Actions)
        /// <summary>
        /// 실행 중인 컨테이너 내부에 직접 명령을 내릴 수 있도록, 호스트 PC의 터미널(cmd)을 띄워 컨테이너 셸(bash/sh)에 연결합니다.
        /// </summary>
        private void OpenTerminal()
        {
            if (string.IsNullOrEmpty(ContainerId)) return;

            try
            {
                _containerService.OpenTerminal(ContainerId);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"터미널 오류 : {ex.Message}");
            }
        }

        /// <summary>
        /// 이 컨테이너의 세부 정보(로그, 환경변수 상세, 파일 송수신 기능 등)를 확인하고 제어할 수 있는 전용 상세 팝업 창을 엽니다.
        /// </summary>
        private async Task OpenDetailWindowAsync()
        {
            if (Type != NodeType.Container || string.IsNullOrEmpty(ContainerId)) return;

            var detailWindow = new ContainerDetailWindow
            {
                DataContext = this,
                Owner = App.Current.MainWindow
            };

            detailWindow.Show();
            await LoadLogsAsync();
        }

        /// <summary>
        /// 도커 엔진으로부터 이 컨테이너가 출력한 최근 로그(표준 출력 및 에러)를 최대 500줄까지 읽어와 UI에 표시합니다.
        /// </summary>
        private async Task LoadLogsAsync()
        {
            if (string.IsNullOrEmpty(ContainerId))
            {
                ContainerLogs = "Container ID is missing.";
                return;
            }

            try
            {
                ContainerLogs = "Fetching logs from Docker engine...";
                string logs = await _containerService.GetContainerLogsAsync(ContainerId, tailCount: 500);

                ContainerLogs = string.IsNullOrEmpty(logs) ? "(No logs found)" : logs;
            }
            catch (Exception ex)
            {
                ContainerLogs = $"Error fetching logs: {ex.Message}";
            }
        }

        /// <summary>
        /// 도커 엔진을 찔러 컨테이너의 상세 설정(Inspect) 정보를 가져온 뒤, 
        /// 해당 컨테이너를 구워냈을 법한 [Dockerfile]의 뼈대를 역추출(Reverse Engineering)하여 사용자가 저장하거나 복사할 수 있게 해줍니다.
        /// </summary>
        private async Task ExtractDockerfileAsync()
        {
            if (string.IsNullOrEmpty(ContainerId)) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                // 도커 엔진에서 컨테이너의 모든 뼈대 정보 가져오기
                var info = await _containerService.InspectContainerAsync(ContainerId);
                var sb = new System.Text.StringBuilder();

                // 1. 베이스 이미지 (FROM)
                sb.AppendLine($"FROM {info.Config.Image}");
                sb.AppendLine();

                // 2. 작업 디렉토리 (WORKDIR)
                if (!string.IsNullOrWhiteSpace(info.Config.WorkingDir))
                {
                    sb.AppendLine($"WORKDIR {info.Config.WorkingDir}");
                }

                // 3. 환경 변수 (ENV)
                if (info.Config.Env != null && info.Config.Env.Count > 0)
                {
                    foreach (var env in info.Config.Env)
                    {
                        sb.AppendLine($"ENV {env}");
                    }
                }

                // 4. 노출 포트 (EXPOSE)
                if (info.Config.ExposedPorts != null && info.Config.ExposedPorts.Count > 0)
                {
                    foreach (var port in info.Config.ExposedPorts.Keys)
                    {
                        sb.AppendLine($"EXPOSE {port.Split('/')[0]}");
                    }
                }

                // 5. 볼륨 (VOLUME)
                if (info.Config.Volumes != null && info.Config.Volumes.Count > 0)
                {
                    foreach (var vol in info.Config.Volumes.Keys)
                    {
                        sb.AppendLine($"VOLUME {vol}");
                    }
                }

                // 6. 시작 명령어 (ENTRYPOINT / CMD)
                if (info.Config.Entrypoint != null && info.Config.Entrypoint.Count > 0)
                {
                    var eps = string.Join("\", \"", info.Config.Entrypoint);
                    sb.AppendLine($"ENTRYPOINT [\"{eps}\"]");
                }

                if (info.Config.Cmd != null && info.Config.Cmd.Count > 0)
                {
                    var cmds = string.Join("\", \"", info.Config.Cmd);
                    sb.AppendLine($"CMD [\"{cmds}\"]");
                }

                string dockerfileContent = sb.ToString();

                var saveDlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "Dockerfile",
                    Filter = "Dockerfile|*.*|Text Files (*.txt)|*.txt",
                    Title = "추출한 Dockerfile 저장"
                };

                if (saveDlg.ShowDialog() == true)
                {
                    File.WriteAllText(saveDlg.FileName, dockerfileContent);
                    _dialogService.ShowInfo($"[{saveDlg.FileName}] 경로에 성공적으로 저장되었습니다.", "저장 완료");
                }
                else
                {
                    bool wantCopy = _dialogService.ShowConfirm("파일 저장을 취소하셨습니다.\n대신 내용을 클립보드(Ctrl+C)에 복사하시겠습니까?", "클립보드 복사");

                    if (wantCopy)
                    {
                        Clipboard.SetText(dockerfileContent);
                        _dialogService.ShowInfo("클립보드에 복사되었습니다. (Ctrl+V 로 붙여넣기 하세요)", "복사 완료");
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"추출 실패: {ex.Message}");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        /// <summary>
        /// 볼륨 백업: 저장할 위치를 묻고 백그라운드에서 백업을 수행합니다.
        /// </summary>
        private async Task ExecuteBackupVolumeAsync(object? parameter)
        {
            var saveDlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"[{Name}] 볼륨 백업 저장",
                Filter = "Tar Archive (*.tar)|*.tar",
                FileName = $"{Name}_backup_{DateTime.Now:yyyyMMdd_HHmmss}.tar"
            };

            if (saveDlg.ShowDialog() == true)
            {
                DetailStatus = "Backing up...";
                StatusColor = "#007ACC";

                try
                {
                    await _volumeService.BackupVolumeAsync(Name, saveDlg.FileName);
                    _dialogService.ShowInfo($"볼륨 백업이 완료되었습니다.\n저장 위치: {saveDlg.FileName}", "백업 성공");
                }
                catch (Exception ex)
                {
                    _dialogService.ShowError($"백업 중 오류가 발생했습니다.\n{ex.Message}", "백업 실패");
                }
                finally
                {
                    await RefreshDetailsAsync(); // 상태 원상복구
                }
            }
        }

        /// <summary>
        /// 볼륨 복원: 복원할 .tar 파일을 선택받고 볼륨에 덮어씁니다.
        /// </summary>
        private async Task ExecuteRestoreVolumeAsync(object? parameter)
        {
            bool confirm = _dialogService.ShowConfirm($"[{Name}] 볼륨에 데이터를 복원하시겠습니까?\n기존 데이터가 덮어씌워질 수 있습니다.", "복원 경고");
            if (!confirm) return;

            var openDlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "복원할 백업 파일(.tar) 선택",
                Filter = "Tar Archive (*.tar)|*.tar|All Files (*.*)|*.*"
            };

            if (openDlg.ShowDialog() == true)
            {
                DetailStatus = "Restoring...";
                StatusColor = "#E67E22";

                try
                {
                    await _volumeService.RestoreVolumeAsync(Name, openDlg.FileName);
                    _dialogService.ShowInfo($"볼륨 데이터가 성공적으로 복원되었습니다.", "복원 성공");
                }
                catch (Exception ex)
                {
                    _dialogService.ShowError($"복원 중 오류가 발생했습니다.\n{ex.Message}", "복원 실패");
                }
                finally
                {
                    await RefreshDetailsAsync(); // 상태 원상복구
                }
            }
        }

        /// <summary>
        /// 슬라이더에 설정된 값을 바탕으로 실행 중인 컨테이너의 리소스를 실시간 업데이트합니다.
        /// </summary>
        private async Task ExecuteUpdateResourcesAsync(object? parameter)
        {
            // 컨테이너의 고유 ID가 없으면 실행 불가
            if (string.IsNullOrWhiteSpace(ContainerId)) return;

            bool confirm = _dialogService.ShowConfirm(
                $"컨테이너 리소스를 실시간으로 제한하시겠습니까? (재시작 없음)\n\n" +
                $"- 목표 CPU: {TargetCpuCount:0.1} Core\n" +
                $"- 목표 Memory: {TargetMemoryMb} MB",
                "실시간 리소스 변경");

            if (!confirm) return;

            try
            {
                // 1단계에서 만든 백엔드 서비스 호출!
                await _containerService.UpdateContainerResourcesAsync(ContainerId, TargetCpuCount, TargetMemoryMb);

                _dialogService.ShowInfo("리소스 제한이 무중단으로 성공적으로 적용되었습니다.", "업데이트 완료");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"리소스 업데이트 실패: {ex.Message}\n(참고: CPU 제한이 호스트 코어 수를 넘을 수 없습니다.)", "오류");
            }
        }

        /// <summary>
        /// UI 창에서 콜백(Action)을 넘겨주면, 도커 엔진과 파이프를 뚫고 로그를 실시간으로 쏴줍니다.
        /// </summary>
        public async Task StartLogStreamAsync(Action<string> onLogReceived)
        {
            if (string.IsNullOrEmpty(ContainerId)) return;

            // 이미 열린 파이프가 있다면 안전하게 끊고 새로 엽니다.
            _logStreamCts?.Cancel();
            _logStreamCts = new CancellationTokenSource();

            try
            {
                // 백엔드(DockerApiService)의 스트리밍 메서드 호출
                await _containerService.StreamContainerLogsAsync(ContainerId, onLogReceived, _logStreamCts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Log stream error: {ex.Message}");
            }
        }

        /// <summary>
        /// 창을 닫을 때 메모리 누수를 막기 위해 파이프를 폭파합니다.
        /// </summary>
        public void StopLogStream()
        {
            _logStreamCts?.Cancel();
            _logStreamCts?.Dispose();
            _logStreamCts = null;
        }
        #endregion
    }
}
