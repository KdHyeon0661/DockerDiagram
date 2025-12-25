using System.Windows;
using System.Windows.Input;
using System.Collections.Generic;
using System.Linq; 
using System.Threading.Tasks;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public class NodeViewModel : ViewModelBase
    {
        private const int GRID_SIZE = 10;
        private const double MIN_SIZE = 50;

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
                    OnPropertyChanged(nameof(ShortContainerId)); // <-- 중요!
                }
            }
        }

        private static string ShortenId(string? id, int len = 12)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            return id.Length <= len ? id : id.Substring(0, len);
        }

        // 컨테이너용 ShortId
        public string ShortContainerId => (Type == NodeType.Container) ? ShortenId(ContainerId) : "";

        public Dictionary<string, string> NetworkIpMap { get; private set; } = new Dictionary<string, string>();

        private List<string> _portBindings = new List<string>();
        public List<string> PortBindings
        {
            get => _portBindings;
            set { _portBindings = value; OnPropertyChanged(); }
        }

        // 2. 환경 변수 (UI 알림 지원)
        private List<string> _environmentVariables = new List<string>();
        public List<string> EnvironmentVariables
        {
            get => _environmentVariables;
            set { _environmentVariables = value; OnPropertyChanged(); }
        }

        private string _restartPolicy = "no";
        public string RestartPolicy
        {
            get => _restartPolicy;
            set { _restartPolicy = value; OnPropertyChanged(); }
        }

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
                _x = Math.Round(value / GRID_SIZE) * GRID_SIZE;
                OnPropertyChanged();
                OnPositionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                _y = Math.Round(value / GRID_SIZE) * GRID_SIZE;
                OnPropertyChanged();
                OnPositionChanged?.Invoke(this, EventArgs.Empty);
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
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
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


        // 2. 상세 정보 및 상태 제어 속성

        // 상태 플래그 (버튼 활성화용)
        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); }
        }

        private bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            set { _isPaused = value; OnPropertyChanged(); }
        }

        // 공통 정보
        private string _detailStatus = "Unknown";
        public string DetailStatus { get => _detailStatus; set { _detailStatus = value; OnPropertyChanged(); } }

        private string _createdDate = "-"; // 볼륨/네트워크용 생성일
        public string CreatedDate { get => _createdDate; set { _createdDate = value; OnPropertyChanged(); } }

        // 컨테이너 전용
        private string _startedAt = "-";
        public string StartedAt { get => _startedAt; set { _startedAt = value; OnPropertyChanged(); } }

        private string _finishedAt = "-";
        public string FinishedAt { get => _finishedAt; set { _finishedAt = value; OnPropertyChanged(); } }

        private string _ipAddresses = "-";
        public string IpAddresses { get => _ipAddresses; set { _ipAddresses = value; OnPropertyChanged(); } }

        private string _connectedNetworks = "-";
        public string ConnectedNetworks { get => _connectedNetworks; set { _connectedNetworks = value; OnPropertyChanged(); } }

        private string _mountedVolumes = "None";
        public string MountedVolumes { get => _mountedVolumes; set { _mountedVolumes = value; OnPropertyChanged(); } }

        // 볼륨 전용
        private string _driver = "-";
        public string Driver { get => _driver; set { _driver = value; OnPropertyChanged(); } }

        private string _mountpoint = "-";
        public string Mountpoint { get => _mountpoint; set { _mountpoint = value; OnPropertyChanged(); } }

        private string _usedByContainers = "-";
        public string UsedByContainers { get => _usedByContainers; set { _usedByContainers = value; OnPropertyChanged(); } }

        private string _networkDriver = "-";
        public string NetworkDriver { get => _networkDriver; set { _networkDriver = value; OnPropertyChanged(); } }

        private string _subnet = "-";
        public string Subnet { get => _subnet; set { _subnet = value; OnPropertyChanged(); } }

        private string _gateway = "-";
        public string Gateway { get => _gateway; set { _gateway = value; OnPropertyChanged(); } }

        private string _networkContainers = "-"; // 연결된 컨테이너 목록
        public string NetworkContainers { get => _networkContainers; set { _networkContainers = value; OnPropertyChanged(); } }


        // 3. Commands
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand RestartCommand { get; }
        public ICommand TerminalCommand { get; }


        // --- 생성자 ---
        public NodeViewModel()
        {
            // Command 초기화 (조건부 활성화 로직 포함)

            // Start: 실행 중이 아닐 때만
            StartCommand = new AsyncRelayCommand(_ => ControlAction("start"), _ => !IsRunning);

            // Stop: 실행 중일 때만
            StopCommand = new AsyncRelayCommand(_ => ControlAction("stop"), _ => IsRunning);

            // Pause: 실행 중이거나 일시정지 상태일 때
            PauseCommand = new AsyncRelayCommand(_ => ControlAction("pause"), _ => IsRunning || IsPaused);

            // Restart: 실행 중이거나 멈춰있을 때 (항상 가능)
            RestartCommand = new AsyncRelayCommand(_ => ControlAction("restart"), _ => true);

            // Terminal: 실행 중일 때만 접속 가능
            TerminalCommand = new RelayCommand(_ => OpenTerminal(), _ => IsRunning);
        }


        // 4. 상세 정보 로드
        public async Task RefreshDetailsAsync()
        {
            // 이름이 없으면 중단
            if (string.IsNullOrEmpty(Name)) return;

            var api = DockerApiService.Instance;

            try
            {
                // [CASE 1] 컨테이너 상세 조회
                if (Type == NodeType.Container)
                {
                    if (string.IsNullOrEmpty(ContainerId)) return;

                    var info = await api.InspectContainerAsync(ContainerId);

                    // 1. 텍스트 상태 및 제어 플래그
                    DetailStatus = info.State.Status; // running, exited...
                    IsRunning = info.State.Running;
                    IsPaused = info.State.Paused;

                    // 2. 시간 파싱
                    StartedAt = DateTime.TryParse(info.State.StartedAt, out var sTime) ? sTime.ToString("yyyy-MM-dd HH:mm:ss") : info.State.StartedAt;
                    FinishedAt = DateTime.TryParse(info.State.FinishedAt, out var fTime) ? fTime.ToString("yyyy-MM-dd HH:mm:ss") : info.State.FinishedAt;

                    // 3. 재시작 정책 (Restart Policy) - 사용자 코드 유지
                    if (info.HostConfig?.RestartPolicy != null)
                    {
                        string policy = info.HostConfig.RestartPolicy.Name.ToString().ToLower();
                        if (policy == "unlessstopped") policy = "unless-stopped";
                        else if (policy == "onfailure") policy = "on-failure";
                        RestartPolicy = policy;
                    }

                    // 4. [추가됨] 환경 변수 파싱 (Docker Compose Export용)
                    if (info.Config?.Env != null)
                    {
                        this.EnvironmentVariables = info.Config.Env.ToList();
                    }
                    else
                    {
                        this.EnvironmentVariables = new List<string>();
                    }

                    // 5. [추가됨] 포트 바인딩 파싱 (Docker Compose Export용)
                    // 포맷: "HostPort:ContainerPort" (예: "8080:80")
                    var portsList = new List<string>();
                    if (info.HostConfig?.PortBindings != null)
                    {
                        foreach (var kvp in info.HostConfig.PortBindings)
                        {
                            string containerPort = kvp.Key.Replace("/tcp", "").Replace("/udp", ""); // "80/tcp" -> "80"
                            foreach (var binding in kvp.Value)
                            {
                                if (!string.IsNullOrEmpty(binding.HostPort))
                                {
                                    portsList.Add($"{binding.HostPort}:{containerPort}");
                                }
                            }
                        }
                    }
                    this.PortBindings = portsList;

                    // 6. 네트워크 파싱 (IP 맵 등)
                    var nets = new List<string>();
                    var ips = new List<string>();

                    if (NetworkIpMap == null) NetworkIpMap = new Dictionary<string, string>(); // 안전장치
                    NetworkIpMap.Clear();

                    if (info.NetworkSettings?.Networks != null)
                    {
                        foreach (var net in info.NetworkSettings.Networks)
                        {
                            nets.Add(net.Key);
                            string ip = net.Value.IPAddress;
                            if (!string.IsNullOrEmpty(ip))
                            {
                                ips.Add(ip);
                                NetworkIpMap[net.Key] = ip;
                            }
                        }
                    }
                    ConnectedNetworks = nets.Count > 0 ? string.Join(", ", nets) : "None";
                    IpAddresses = ips.Count > 0 ? string.Join(", ", ips) : "-";

                    // 7. 볼륨 마운트 파싱
                    var vols = new List<string>();
                    if (info.Mounts != null)
                    {
                        foreach (var m in info.Mounts)
                        {
                            vols.Add($"{m.Source} -> {m.Destination}");
                        }
                    }
                    MountedVolumes = vols.Count > 0 ? string.Join("\n", vols) : "None";

                    // 8. 상태 색상 갱신
                    if (IsRunning) StatusColor = "#28a745";
                    else if (IsPaused) StatusColor = "#ffc107";
                    else StatusColor = "#dc3545";

                    OnPropertyChanged(nameof(NetworkIpMap));
                }
                // [CASE 2] 볼륨 상세 조회
                else if (Type == NodeType.Volume)
                {
                    var vol = await api.InspectVolumeAsync(Name);

                    DetailStatus = "Created";
                    Driver = vol.Driver;
                    Mountpoint = vol.Mountpoint;
                    CreatedDate = DateTime.TryParse(vol.CreatedAt, out var cTime) ? cTime.ToString("yyyy-MM-dd HH:mm:ss") : vol.CreatedAt;

                    var usedList = await api.GetContainersUsingVolumeAsync(Name);
                    UsedByContainers = usedList.Count > 0 ? string.Join(", ", usedList) : "None";

                    IsRunning = false;
                    IsPaused = false;
                    StatusColor = "#E67E22";
                }
                // [CASE 3] 네트워크 상세 조회
                else if (Type == NodeType.Network)
                {
                    if (string.IsNullOrEmpty(ContainerId)) return;

                    var net = await api.InspectNetworkAsync(ContainerId);

                    DetailStatus = "Active";
                    NetworkDriver = net.Driver;

                    if (net.IPAM?.Config != null && net.IPAM.Config.Count > 0)
                    {
                        Subnet = net.IPAM.Config[0].Subnet ?? "-";
                        Gateway = net.IPAM.Config[0].Gateway ?? "-";
                    }
                    else
                    {
                        Subnet = "-";
                        Gateway = "-";
                    }

                    if (net.Containers != null && net.Containers.Count > 0)
                    {
                        var names = net.Containers.Values.Select(c => c.Name).ToList();
                        NetworkContainers = string.Join(", ", names);
                    }
                    else
                    {
                        NetworkContainers = "None";
                    }

                    IsRunning = false;
                    IsPaused = false;
                    StatusColor = "#9B59B6";
                }

                // UI 갱신 강제
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Refresh Error: {ex.Message}");
                DetailStatus = "Error";
                IsRunning = false;
                IsPaused = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        // --- 5. 제어 액션 로직 ---
        private async Task ControlAction(string action)
        {
            if (string.IsNullOrEmpty(ContainerId)) return;

            var api = DockerApiService.Instance;
            try
            {
                switch (action)
                {
                    case "start": await api.StartContainerAsync(ContainerId); break;
                    case "stop": await api.StopContainerAsync(ContainerId); break;
                    case "pause":
                        if (DetailStatus == "paused") await api.UnpauseContainerAsync(ContainerId);
                        else await api.PauseContainerAsync(ContainerId);
                        break;
                    case "restart": await api.RestartContainerAsync(ContainerId); break;
                }
                // 액션 수행 후 정보 갱신 (상태 변경 반영)
                await RefreshDetailsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"동작 실패 : {ex.Message}");
            }
        }

        private void OpenTerminal()
        {
            if (string.IsNullOrEmpty(ContainerId)) return;
            var api = DockerApiService.Instance;
            try
            {
                api.OpenTerminal(ContainerId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"터미널 오류 : {ex.Message}");
            }
        }
    }
}