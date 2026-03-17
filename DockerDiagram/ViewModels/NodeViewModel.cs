using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using System.Windows.Threading;

namespace DockerDiagram.ViewModels
{
    public class NodeViewModel : ViewModelBase, IConnectableItem
    {
        private const int GRID_SIZE = 10;
        private const double MIN_SIZE = 50;

        private readonly IContainerService? _containerService;
        private readonly IVolumeService? _volumeService;
        private readonly IDialogService? _dialogService;

        // 마운트 정보 원본 저장용 (캐시)
        private List<Docker.DotNet.Models.MountPoint> _cachedMounts = new();

        // --- 1. 기본 레이아웃 속성 ---
        private double _x;
        private double _y;
        private double _width = 160;
        private double _height = 80;
        private bool _isSelected;
        private string _name = string.Empty;
        private NodeType _type;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        private string _containerId = string.Empty;

        public string ContainerId
        {
            get => _containerId;
            set
            {
                if (_containerId != value)
                {
                    _containerId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShortContainerId));
                }
            }
        }

        private static string ShortenId(string? id, int len = 12)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            return id.Length <= len ? id : id.Substring(0, len);
        }

        public string ShortContainerId => (Type == NodeType.Container) ? ShortenId(ContainerId) : "";

        // --- 네트워크 3단계 전환 모드 ---
        private int _networkDisplayMode = 0;
        public int NetworkDisplayMode
        {
            get => _networkDisplayMode;
            set { _networkDisplayMode = value; OnPropertyChanged(); }
        }

        // 볼륨 디스플레이 모드 (0: Named, 1: Bind)
        private int _volumeDisplayMode = 0;
        public int VolumeDisplayMode
        {
            get => _volumeDisplayMode;
            set
            {
                _volumeDisplayMode = value;
                OnPropertyChanged();
                UpdateVolumeList(); // 모드가 바뀌면 리스트 내용 즉시 갱신
            }
        }

        public class NetworkDetail
        {
            public string NetworkName { get; set; } = "";
            public string IPv4 { get; set; } = "-";
            public string IPv6 { get; set; } = "-";
        }

        public ObservableCollection<NetworkDetail> NetworkDetailList { get; } = new();
        public Dictionary<string, string> NetworkIpMap { get; private set; } = new Dictionary<string, string>();

        // [리스트] 볼륨 (필터링된 결과 표시)
        public ObservableCollection<string> MountedVolumeList { get; } = new ObservableCollection<string>();

        // [리스트] 연결된 노드 (인터넷, 컨테이너만 표시)
        public ObservableCollection<string> ConnectedNodes { get; } = new ObservableCollection<string>();

        // UsedByContainers: 문자열 -> 리스트로 변경 (볼륨 상세 정보용)
        public ObservableCollection<string> UsedByContainers { get; } = new ObservableCollection<string>();

        // --- 실시간 리소스 모니터링 ---
        private string _cpuUsage = "0.0%";
        public string CpuUsage { get => _cpuUsage; set { _cpuUsage = value; OnPropertyChanged(); } }

        private double _cpuValue = 0;
        public double CpuValue { get => _cpuValue; set { _cpuValue = value; OnPropertyChanged(); } }

        private string _memoryUsage = "0B / 0B";
        public string MemoryUsage { get => _memoryUsage; set { _memoryUsage = value; OnPropertyChanged(); } }

        private double _memoryValue = 0;
        public double MemoryValue
        {
            get => _memoryValue;
            set
            {
                // NaN 값 방지
                if (double.IsNaN(value)) _memoryValue = 0;
                else _memoryValue = value;
                OnPropertyChanged();
            }
        }

        private DispatcherTimer? _statsTimer;

        // --- 도커 상세 정보 ---
        private List<string> _portBindings = new List<string>();
        public List<string> PortBindings
        {
            get => _portBindings;
            set { _portBindings = value; OnPropertyChanged(); }
        }

        private List<string> _environmentVariables = new List<string>();
        public List<string> EnvironmentVariables
        {
            get => _environmentVariables;
            set { _environmentVariables = value; OnPropertyChanged(); }
        }

        public List<string> EnvList => EnvironmentVariables;

        private string _uptime = "-";
        public string Uptime { get => _uptime; set { _uptime = value; OnPropertyChanged(); } }

        private string _ipAddress = "-";
        public string IPAddress { get => _ipAddress; set { _ipAddress = value; OnPropertyChanged(); } }

        private string _restartPolicy = "no";
        public string RestartPolicy
        {
            get => _restartPolicy;
            set { _restartPolicy = value; OnPropertyChanged(); }
        }

        private string _healthStatus = "No Check";
        public string HealthStatus { get => _healthStatus; set { _healthStatus = value; OnPropertyChanged(); } }

        private string _healthColor = "#888888";
        public string HealthColor { get => _healthColor; set { _healthColor = value; OnPropertyChanged(); } }

        private string _containerLogs = "Loading logs...";
        public string ContainerLogs
        {
            get => _containerLogs;
            set { _containerLogs = value; OnPropertyChanged(); }
        }

        // ★ [추가] 팝업창 전용 파일 입출력 및 환경변수 속성
        private string _hostFilePath = @"C:\temp\";
        public string HostFilePath { get => _hostFilePath; set { _hostFilePath = value; OnPropertyChanged(); } }

        private string _containerFilePath = @"/app/data";
        public string ContainerFilePath { get => _containerFilePath; set { _containerFilePath = value; OnPropertyChanged(); } }

        private string _newEnvInput = "";
        public string NewEnvInput { get => _newEnvInput; set { _newEnvInput = value; OnPropertyChanged(); } }

        public NodeType Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(); }
        }

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

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();
                if (value)
                {
                    StartMonitoring();
                    RefreshConnections();
                }
                else
                {
                    StopMonitoring();
                }
            }
        }

        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public string ImageName { get; set; } = string.Empty;
        public string PortInfo { get; set; } = string.Empty;

        private string _statusColor = "#28a745";
        public string StatusColor { get => _statusColor; set { _statusColor = value; OnPropertyChanged(); } }

        public double CenterX => X + (Width / 2);
        public double CenterY => Y + (Height / 2);

        private bool _isCreating = false;
        public bool IsCreating
        {
            get => _isCreating;
            set
            {
                _isCreating = value;
                OnPropertyChanged();
                StatusColor = value ? "#FFC107" : "#28a745";
            }
        }

        public event EventHandler? OnPositionChanged;
        public event EventHandler? OnModified;

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged();
                    RaiseCommandStates();
                }
            }
        }

        private bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (_isPaused != value)
                {
                    _isPaused = value;
                    OnPropertyChanged();
                    RaiseCommandStates();
                }
            }
        }

        private void RaiseCommandStates()
        {
            StartCommand?.RaiseCanExecuteChanged();
            StopCommand?.RaiseCanExecuteChanged();
            PauseCommand?.RaiseCanExecuteChanged();
            RestartCommand?.RaiseCanExecuteChanged();
            TerminalCommand?.RaiseCanExecuteChanged();
        }

        private string _detailStatus = "Unknown";
        public string DetailStatus { get => _detailStatus; set { _detailStatus = value; OnPropertyChanged(); } }

        private string _createdDate = "-";
        public string CreatedDate { get => _createdDate; set { _createdDate = value; OnPropertyChanged(); } }

        private string _startedAt = "-";
        public string StartedAt { get => _startedAt; set { _startedAt = value; OnPropertyChanged(); } }

        private string _finishedAt = "-";
        public string FinishedAt { get => _finishedAt; set { _finishedAt = value; OnPropertyChanged(); } }

        private string _ipAddresses = "-";
        public string IpAddresses { get => _ipAddresses; set { _ipAddresses = value; OnPropertyChanged(); } }

        private string _connectedNetworks = "-";
        public string ConnectedNetworksString { get => _connectedNetworks; set { _connectedNetworks = value; OnPropertyChanged(); } }

        private string _mountedVolumes = "None";
        public string MountedVolumes { get => _mountedVolumes; set { _mountedVolumes = value; OnPropertyChanged(); } }

        private string _driver = "-";
        public string Driver { get => _driver; set { _driver = value; OnPropertyChanged(); } }

        private string _mountpoint = "-";
        public string Mountpoint { get => _mountpoint; set { _mountpoint = value; OnPropertyChanged(); } }

        private SheetViewModel? _parentSheet;
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

        private void Connectors_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshConnections();
        }


        // --- Commands ---
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

        public NodeViewModel(IContainerService? containerService = null,
                             IVolumeService? volumeService = null,
                             IDialogService? dialogService = null)
        {
            _containerService = containerService;
            _volumeService = volumeService;
            _dialogService = dialogService;

            StartCommand = new AsyncRelayCommand(_ => ControlAction("start"), _ => Type == NodeType.Container && !IsRunning);
            StopCommand = new AsyncRelayCommand(_ => ControlAction("stop"), _ => Type == NodeType.Container && IsRunning);
            PauseCommand = new AsyncRelayCommand(_ => ControlAction("pause"), _ => Type == NodeType.Container && (IsRunning || IsPaused));
            RestartCommand = new AsyncRelayCommand(_ => ControlAction("restart"), _ => Type == NodeType.Container);

            TerminalCommand = new RelayCommand(_ => OpenTerminal(), _ => Type == NodeType.Container && IsRunning);

            ToggleNetworkModeCommand = new RelayCommand(_ => {
                NetworkDisplayMode = (NetworkDisplayMode + 1) % 3;
            });

            ToggleVolumeModeCommand = new RelayCommand(_ => {
                VolumeDisplayMode = (VolumeDisplayMode + 1) % 2;
            });

            OpenDetailWindowCommand = new AsyncRelayCommand(_ => OpenDetailWindowAsync(), _ => Type == NodeType.Container);
            RefreshLogsCommand = new AsyncRelayCommand(_ => LoadLogsAsync(), _ => Type == NodeType.Container);

            CopyLogsCommand = new RelayCommand(_ => {
                if (!string.IsNullOrEmpty(ContainerLogs))
                {
                    System.Windows.Clipboard.SetText(ContainerLogs);
                    _dialogService?.ShowInfo("로그가 클립보드에 복사되었습니다.", "복사 완료");
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
                    System.IO.File.WriteAllText(dlg.FileName, ContainerLogs);
                    _dialogService?.ShowInfo("로그가 파일로 저장되었습니다.", "저장 완료");
                }
            });

            CopyToContainerCommand = new AsyncRelayCommand(async _ => {
                if (string.IsNullOrWhiteSpace(HostFilePath) || string.IsNullOrWhiteSpace(ContainerFilePath)) return;
                try
                {
                    await _containerService!.CopyToContainerAsync(ContainerId, HostFilePath, ContainerFilePath);
                    _dialogService?.ShowInfo("컨테이너로 파일 복사가 완료되었습니다.", "업로드 성공");
                }
                catch (Exception ex)
                {
                    _dialogService?.ShowMessage($"업로드 실패: {ex.Message}");
                }
            });

            CopyFromContainerCommand = new AsyncRelayCommand(async _ => {
                if (string.IsNullOrWhiteSpace(HostFilePath) || string.IsNullOrWhiteSpace(ContainerFilePath)) return;
                try
                {
                    await _containerService!.CopyFromContainerAsync(ContainerId, ContainerFilePath, HostFilePath);
                    _dialogService?.ShowInfo("컨테이너에서 파일 다운로드가 완료되었습니다.", "다운로드 성공");
                }
                catch (Exception ex)
                {
                    _dialogService?.ShowMessage($"다운로드 실패: {ex.Message}");
                }
            });

            AddEnvAndRecreateCommand = new RelayCommand(_ => {
                if (string.IsNullOrWhiteSpace(NewEnvInput)) return;
                _dialogService?.ShowInfo($"이 기능은 기존 설정을 바탕으로 컨테이너를 삭제하고 [{NewEnvInput}] 환경변수를 추가하여 다시 생성합니다.\n(기능 연결 필요)", "Recreate");
            });
        }

        // --- 상세 정보 로드 ---
        public async Task RefreshDetailsAsync()
        {
            if (string.IsNullOrEmpty(Name)) return;
            if (Type == NodeType.Internet) return;

            try
            {
                if (Type == NodeType.Container && _containerService != null)
                {
                    if (string.IsNullOrEmpty(ContainerId)) return;

                    var info = await _containerService.InspectContainerAsync(ContainerId);

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

                    OnPropertyChanged(nameof(NetworkIpMap));
                    OnPropertyChanged(nameof(NetworkDetailList));
                    OnPropertyChanged(nameof(EnvList));
                }
                else if (Type == NodeType.Volume && _volumeService != null)
                {
                    var vol = await _volumeService.InspectVolumeAsync(Name);

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
                Debug.WriteLine($"Refresh Error: {ex.Message}");
                DetailStatus = "Error";
                IsRunning = false;
                IsPaused = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void UpdateVolumeList()
        {
            MountedVolumeList.Clear();

            if (ParentSheet == null) return;

            var validVolumeNames = ParentSheet.Nodes
                                              .Where(n => n.Type == NodeType.Volume)
                                              .Select(n => n.Name)
                                              .ToHashSet();

            foreach (var m in _cachedMounts)
            {
                if (VolumeDisplayMode == 0)
                {
                    if (m.Type == "volume" && validVolumeNames.Contains(m.Name))
                    {
                        MountedVolumeList.Add($"{m.Name} : {m.Destination}");
                    }
                }
                else
                {
                    if (m.Type == "bind")
                    {
                        MountedVolumeList.Add($"{m.Source} -> {m.Destination}");
                    }
                }
            }
        }

        public void StartMonitoring()
        {
            if (Type != NodeType.Container || string.IsNullOrEmpty(ContainerId) || _containerService == null) return;

            if (_statsTimer == null)
            {
                _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _statsTimer.Tick += async (s, e) => await UpdateStatsAsync();
            }
            _statsTimer.Start();
        }

        public void StopMonitoring() => _statsTimer?.Stop();

        private async Task UpdateStatsAsync()
        {
            if (!IsRunning || _containerService == null) return;

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

        private async Task ControlAction(string action)
        {
            if (string.IsNullOrEmpty(ContainerId) || _containerService == null) return;

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
                _dialogService?.ShowMessage($"동작 실패 : {ex.Message}");
            }
        }

        private void OpenTerminal()
        {
            if (string.IsNullOrEmpty(ContainerId) || _containerService == null) return;

            try
            {
                _containerService.OpenTerminal(ContainerId);
            }
            catch (Exception ex)
            {
                _dialogService?.ShowMessage($"터미널 오류 : {ex.Message}");
            }
        }

        // ★ [수정됨] 연결된 대상이 노드인지 그룹인지 안전하게 확인하도록 수정!
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

        private async Task LoadLogsAsync()
        {
            if (_containerService == null || string.IsNullOrEmpty(ContainerId))
            {
                ContainerLogs = "Container Service is not available.";
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
    }
}