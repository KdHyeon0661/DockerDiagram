using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DockerDiagram.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly IContainerService _containerService;
        private readonly IVolumeService _volumeService;
        private readonly INetworkService _networkService;
        private readonly IImageService _imageService;
        private readonly ISystemService _systemService;
        private readonly IDialogService _dialogService;

        public System.Windows.Input.ICommand ExportComposeCommand { get; }

        private bool _isModified = false;
        public bool IsModified
        {
            get => _isModified;
            set { _isModified = value; OnPropertyChanged(); }
        }

        // --- 1. 기본 맵 속성 ---
        public double MapWidth
        {
            get => ActiveSheet?.MapWidth ?? 2000;
            set { if (ActiveSheet != null) { ActiveSheet.MapWidth = value; OnPropertyChanged(); } }
        }
        public double MapHeight
        {
            get => ActiveSheet?.MapHeight ?? 2000;
            set { if (ActiveSheet != null) { ActiveSheet.MapHeight = value; OnPropertyChanged(); } }
        }

        private string? _currentFilePath;
        public string? CurrentFilePath
        {
            get => _currentFilePath;
            set
            {
                _currentFilePath = value;
                OnPropertyChanged();
            }
        }

        // --- 2. 시트 및 선택 관리 ---
        public ObservableCollection<SheetViewModel> Sheets { get; set; } = new();

        private SheetViewModel? _activeSheet;
        public SheetViewModel? ActiveSheet
        {
            get => _activeSheet;
            set
            {
                if (_activeSheet != null)
                {
                    _activeSheet.Nodes.CollectionChanged -= Nodes_CollectionChanged;
                    _activeSheet.Groups.CollectionChanged -= Groups_CollectionChanged;

                    // 이전 시트의 노드들 감시 해제 (메모리 누수 방지)
                    foreach (var node in _activeSheet.Nodes)
                    {
                        node.OnModified -= Node_OnModified;
                    }

                    // 이전 시트의 그룹들 감시 해제
                    foreach (var group in _activeSheet.Groups)
                    {
                        group.OnModified -= Node_OnModified;
                    }

                    // 이전 시트의 커넥터들 감시 해제
                    _activeSheet.Connectors.CollectionChanged -= Connectors_CollectionChanged;
                    foreach (var conn in _activeSheet.Connectors)
                    {
                        conn.OnModified -= Connector_OnModified;
                    }
                }

                _activeSheet = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MapWidth));
                OnPropertyChanged(nameof(MapHeight));
                SelectedElement = null;

                if (_activeSheet != null)
                {
                    AttachSheetEvents();
                    UpdateAvailableItems();
                    _activeSheet.UpdateGroupLayering();
                }
            }
        }

        private object? _selectedElement;
        public object? SelectedElement
        {
            get => _selectedElement;
            set
            {
                if (_selectedElement == value) return;

                _selectedElement = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDetailPanelOpen));

                // 1. 활성 시트 내의 시각적 선택 상태(IsSelected) 동기화
                if (ActiveSheet != null)
                {
                    foreach (var node in ActiveSheet.Nodes) node.IsSelected = (node == value);
                    foreach (var conn in ActiveSheet.Connectors) conn.IsSelected = (conn == value);
                    foreach (var group in ActiveSheet.Groups) group.IsSelected = (group == value);
                }

                // 2. 노드가 선택되었다면, 상세 정보(Inspect)를 비동기로 갱신.
                if (_selectedElement is NodeViewModel nodeVm)
                {
                    _ = nodeVm.RefreshDetailsAsync();
                }
            }
        }

        private bool _isSyncing = false;
        public bool IsDetailPanelOpen => _selectedElement != null;

        // --- 3. 아코디언 데이터 ---
        public ObservableCollection<TemplateItem> Templates { get; } = new();
        public ObservableCollection<DockerContainer> ExistingContainers { get; } = new();
        public ObservableCollection<DockerVolume> ExistingVolumes { get; } = new();
        public ObservableCollection<DockerGroup> ExistingNetworks { get; } = new();
        public ObservableCollection<DockerImage> LocalImages { get; } = new();

        private Dictionary<string, int> _usageStats = new Dictionary<string, int>();

        // --- 4. 명령(Commands) ---
        public ICommand AddSheetCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ClosePanelCommand { get; }
        public ICommand PrevSheetCommand { get; }
        public ICommand NextSheetCommand { get; }
        public ICommand DeleteImageCommand { get; }

        private DispatcherTimer _autoSyncTimer;

        private string _lastSyncTime = "Syncing...";
        public string LastSyncTime
        {
            get => _lastSyncTime;
            set { _lastSyncTime = value; OnPropertyChanged(); }
        }

        private string _containerSearchText = "";
        public string ContainerSearchText
        {
            get => _containerSearchText;
            set { _containerSearchText = value; OnPropertyChanged(); UpdateAvailableItems(); }
        }

        private string _volumeSearchText = "";
        public string VolumeSearchText
        {
            get => _volumeSearchText;
            set { _volumeSearchText = value; OnPropertyChanged(); UpdateAvailableItems(); }
        }

        private string _networkSearchText = "";
        public string NetworkSearchText
        {
            get => _networkSearchText;
            set { _networkSearchText = value; OnPropertyChanged(); UpdateAvailableItems(); }
        }

        private string _imageSearchText = "";
        public string ImageSearchText
        {
            get => _imageSearchText;
            set { _imageSearchText = value; OnPropertyChanged(); UpdateAvailableItems(); }
        }

        public ICommand DeleteContainerItemCommand { get; }
        public ICommand DeleteVolumeItemCommand { get; }
        public ICommand DeleteNetworkItemCommand { get; }

        public ICommand SaveCommand { get; }
        public ICommand SaveAsCommand { get; }
        public ICommand LoadCommand { get; }

        public ICommand FlowClearCommand { get; }
        public ICommand FlowAllClearCommand { get; }
        public ICommand DeleteAllSheetCommand { get; }

        // --- 생성자 ---
        public MainViewModel(IDockerService dockerService, IDialogService dialogService)
        {
            // Instance = this;

            // 유효성 검사 및 할당
            if (dockerService == null) throw new ArgumentNullException(nameof(dockerService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            // IDockerService를 각 인터페이스 필드에 분배
            _containerService = dockerService;
            _volumeService = dockerService;
            _networkService = dockerService;
            _imageService = dockerService;
            _systemService = dockerService;

            // 기본 시트 추가
            Sheets.Add(new SheetViewModel("Sheet 1", _containerService, _volumeService, _dialogService));
            ActiveSheet = Sheets.First();

            // 명령 초기화
            AddSheetCommand = new RelayCommand(_ => AddSheet());
            DeleteCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync());
            ClosePanelCommand = new RelayCommand(_ => SelectedElement = null);
            PrevSheetCommand = new RelayCommand(_ => NavigateSheet(-1));
            NextSheetCommand = new RelayCommand(_ => NavigateSheet(1));
            DeleteImageCommand = new AsyncRelayCommand(DeleteImageAsync);

            DeleteContainerItemCommand = new AsyncRelayCommand(DeleteContainerItemAsync);
            DeleteVolumeItemCommand = new AsyncRelayCommand(DeleteVolumeItemAsync);
            DeleteNetworkItemCommand = new AsyncRelayCommand(DeleteNetworkItemAsync);

            FlowClearCommand = new RelayCommand(ExecuteFlowClear);
            FlowAllClearCommand = new RelayCommand(ExecuteFlowAllClear);
            DeleteAllSheetCommand = new RelayCommand(ExecuteDeleteAllSheet);

            SaveCommand = new RelayCommand(SaveAction);
            SaveAsCommand = new RelayCommand(SaveAsAction);
            LoadCommand = new AsyncRelayCommand(LoadActionAsync);

            if (ActiveSheet != null) AttachSheetEvents();

            ExportComposeCommand = new RelayCommand(_ =>
            {
                if (ActiveSheet != null)
                {
                    ComposeExportService.ExportToCompose(ActiveSheet, _dialogService);
                }
            });

            // 템플릿 초기화
            RefreshTemplates();

            // 자동 동기화 타이머 시작
            _autoSyncTimer = new DispatcherTimer();
            _autoSyncTimer.Interval = TimeSpan.FromSeconds(1);
            _autoSyncTimer.Tick += AutoSync_Tick;
            _autoSyncTimer.Start();

            // 앱 시작 시 1회 실행
            _ = SyncWithDockerEngine();

            _ = LoadLastFileIfExistsAsync();
        }

        public void MarkAsModified()
        {
            IsModified = true;
        }

        private async Task LoadLastFileIfExistsAsync()
        {
            try
            {
                // 1. 저장된 경로 가져오기 (설정이 없으면 에러가 날 수 있으므로 try-catch)
                string lastPath = Properties.Settings.Default.LastFilePath;

                // 2. 경로가 유효하고 실제 파일이 존재하는지 확인
                if (!string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath))
                {
                    // 3. FileService의 "경로로 열기" 기능을 사용하여 로드 (Dialog 없음)
                    bool success = await FileService.LoadDiagramFromPathAsync(
                        this,
                        lastPath,
                        _containerService,
                        _volumeService,
                        _networkService,
                        _dialogService
                    );

                    if (success)
                    {
                        CurrentFilePath = lastPath;
                        IsModified = false;

                        // 4. 불러온 노드들의 상태(색상 등)를 도커와 동기화 (회색 -> 녹색/빨강)
                        await RestoreLiveState();

                        Debug.WriteLine($"[DockerDiscovery] Automatically loaded: {lastPath}");
                    }
                }
                else
                {
                    // 파일이 없으면 그냥 새 프로젝트(Sheet 1) 상태 유지
                    Debug.WriteLine("[DockerDiscovery] No last file found. Starting new.");
                }
            }
            catch (Exception ex)
            {
                // 자동 로드 실패 시 사용자에게 방해가 되지 않도록 로그만 남기고 무시
                Debug.WriteLine($"[DockerDiscovery] Failed: {ex.Message}");
            }
        }

        // --- 5. 템플릿 로직 ---
        private void RefreshTemplates()
        {
            Templates.Clear();

            // 1. 기본 템플릿 3개 (고정)
            var defaults = new List<TemplateItem>
            {
                new TemplateItem { Name = "Nginx Web", Image = "nginx:latest", Type = NodeType.Container, IsDefault = true },
                new TemplateItem { Name = "Redis DB", Image = "redis:alpine", Type = NodeType.Container, IsDefault = true },
                new TemplateItem { Name = "Ubuntu OS", Image = "ubuntu:latest", Type = NodeType.Container, IsDefault = true }
            };

            foreach (var item in defaults) Templates.Add(item);

            // 2. 자주 사용한 것 상위 3개 (기본 템플릿과 중복되지 않는 것만)
            var frequents = _usageStats
                .Where(kv => !defaults.Any(d => d.Image == kv.Key)) // 이미 기본에 있으면 제외
                .OrderByDescending(kv => kv.Value) // 빈도 내림차순
                .Take(3) // 최대 3개
                .Select(kv => new TemplateItem
                {
                    Name = kv.Key,
                    Image = kv.Key,
                    Type = NodeType.Container,
                    IsDefault = false
                });

            foreach (var item in frequents) Templates.Add(item);
        }

        private async void AutoSync_Tick(object? sender, EventArgs e)
        {
            if (_isSyncing) return;

            try
            {
                _isSyncing = true;
                await SyncWithDockerEngine();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] AutoSync Error: {ex.Message}");
            }
            finally
            {
                _isSyncing = false;
            }
        }

        private async Task DeleteContainerItemAsync(object? param)
        {
            if (param is DockerContainer c)
            {
                if (_dialogService.ShowConfirm($"컨테이너 '{c.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        await _containerService.RemoveContainerAsync(c.Id);
                        await SyncWithDockerEngine();
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowMessage($"삭제 실패: {ex.Message}");
                    }
                }
            }
        }

        private async Task DeleteVolumeItemAsync(object? param)
        {
            if (param is DockerVolume v)
            {
                // [변경] DialogService 사용
                if (_dialogService.ShowConfirm($"볼륨 '{v.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        await _volumeService.RemoveVolumeAsync(v.Name);
                        await SyncWithDockerEngine();
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowMessage($"볼륨 삭제 실패: {ex.Message}");
                    }
                }
            }
        }

        private async Task DeleteNetworkItemAsync(object? param)
        {
            if (param is DockerGroup n)
            {
                if (_dialogService.ShowConfirm($"네트워크 '{n.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        await _networkService.RemoveNetworkAsync(n.Id);
                        await SyncWithDockerEngine();
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowMessage($"네트워크 삭제 실패: {ex.Message}");
                    }
                }
            }
        }

        // 통계만 기록하는 헬퍼 함수
        private void RegisterTemplateUsage(string imageName)
        {
            if (string.IsNullOrEmpty(imageName)) return;
            if (!_usageStats.ContainsKey(imageName)) _usageStats[imageName] = 0;
            _usageStats[imageName]++;
            RefreshTemplates();
        }

        // --- 6. Docker 연동 로직 ---
        private List<DockerContainer> _rawContainers = new();
        private List<DockerVolume> _rawVolumes = new();
        private List<DockerGroup> _rawNetworks = new();
        private List<DockerImage> _rawImages = new();

        private async Task SyncWithDockerEngine()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!DockerServiceHelper.IsDockerRunning())
                {
                    LastSyncTime = "Docker stopped";
                    return;
                }
            }

            try
            {
                if (!await _systemService.PingAsync()) return;

                // 1. 원본 데이터 가져오기 (각 서비스 호출)
                _rawContainers = await _containerService.GetContainersAsync();
                _rawVolumes = await _volumeService.GetVolumesAsync();
                _rawNetworks = await _networkService.GetNetworksAsync();
                _rawImages = await _imageService.GetImagesAsync();

                // 2. 필터링 및 UI 갱신 호출
                UpdateAvailableItems();

                LastSyncTime = $"Last updated: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                LastSyncTime = $"Sync failed: {DateTime.Now:HH:mm:ss}";
                Debug.WriteLine($"[DockerDiscovery] {ex}");
            }
        }

        // 싱글톤 로직: 모든 시트(및 그룹 내부)를 검사하여 이미 배치된 컨테이너는 리스트에서 제외
        private void UpdateAvailableItems()
        {
            if (Sheets == null) return;

            var allNodes = new List<NodeViewModel>();
            foreach (var sheet in Sheets)
            {
                allNodes.AddRange(sheet.Nodes); // 시트 바로 위의 노드들
                foreach (var group in sheet.Groups)
                {
                    allNodes.AddRange(group.ContainedNodes); // 그룹 안의 노드들
                }
            }

            // 2. 사용 중인 ID 및 이름 수집 (정확한 타입 확인)
            var usedContainerIds = new HashSet<string>(allNodes.Where(n => n.Type == NodeType.Container).Select(n => n.ContainerId));
            var usedVolumeNames = new HashSet<string>(allNodes.Where(n => n.Type == NodeType.Volume).Select(n => n.Name));

            var usedNetworkNames = new HashSet<string>();
            foreach (var sheet in Sheets)
            {
                foreach (var grp in sheet.Groups)
                {
                    if (grp.Type == GroupType.Network)
                        usedNetworkNames.Add(grp.Title); // 보통 Title에 이름을 넣음
                }
            }

            // 3. 컨테이너 필터링
            var filteredContainers = _rawContainers
                .Where(c => !usedContainerIds.Contains(c.Id)) // 사용 중이면 제외
                .Where(c => string.IsNullOrEmpty(ContainerSearchText) || c.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingContainers, filteredContainers, c => c.Id);

            // 4. 볼륨 필터링 (이름 기준)
            var filteredVolumes = _rawVolumes
                .Where(v => !usedVolumeNames.Contains(v.Name)) // 시트에 있는 이름이면 제외
                .Where(v => string.IsNullOrEmpty(VolumeSearchText) || v.Name.Contains(VolumeSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingVolumes, filteredVolumes, v => v.Name);

            // 5. 네트워크 필터링 (ID/이름 기준 + 기본 네트워크 숨김)
            var defaultNetworks = new HashSet<string> { "bridge", "host", "none" };
            var filteredNetworks = _rawNetworks
                .Where(n => !usedNetworkNames.Contains(n.Name)) // 시트에 있는 이름이면 제외
                .Where(n => !defaultNetworks.Contains(n.Name)) // 기본 네트워크 제외
                .Where(n => string.IsNullOrEmpty(NetworkSearchText) || n.Name.Contains(NetworkSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingNetworks, filteredNetworks, n => n.Id);

            // 6. 이미지 필터링
            var filteredImages = _rawImages
                .Where(i => string.IsNullOrEmpty(ImageSearchText) || i.Repository.Contains(ImageSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(LocalImages, filteredImages, i => i.Id);
        }

        // 이미지 삭제 로직
        private async Task DeleteImageAsync(object? parameter)
        {
            if (parameter is DockerImage img)
            {
                if (_dialogService.ShowConfirm($"이미지 '{img.Repository}'를 삭제하시겠습니까?", "이미지 삭제"))
                {
                    try
                    {
                        await _imageService.DeleteImageAsync(img.Id, force: false);
                        LocalImages.Remove(img);
                    }
                    catch (Exception ex)
                    {
                        // 실패 시 강제 삭제 제안
                        if (_dialogService.ShowConfirm(
                            $"삭제 실패: 이미지가 사용 중일 수 있습니다.\n강제로 삭제하시겠습니까?\n({ex.Message})",
                            "강제 삭제 확인"))
                        {
                            try
                            {
                                await _imageService.DeleteImageAsync(img.Id, force: true);
                                LocalImages.Remove(img);
                            }
                            catch (Exception forceEx)
                            {
                                _dialogService.ShowMessage($"강제 삭제 실패: {forceEx.Message}");
                            }
                        }
                    }
                }
            }
        }

        // 7. 노드 생성 및 관리

        // ★ [중요] 드래그 앤 드롭 핸들러 수정 (Object 타입 수신 -> 타입별 분기)
        public async Task CreateNodeAtAsync(object item, double x, double y)
        {
            if (ActiveSheet == null) return;

            // [CASE 1] 컨테이너 (DockerContainer)
            if (item is DockerContainer container)
            {
                // 1. 메인 노드 생성
                ActiveSheet.CreateContainerAt(container, x, y);
                IsModified = true;
                RegisterTemplateUsage(container.Image);

                // 2. 연결 정보 자동 복구 (네트워크 & 볼륨)
                if (!string.IsNullOrEmpty(container.Id))
                {
                    try
                    {
                        var info = await _containerService.InspectContainerAsync(container.Id);

                        // -----------------------------------------------------------
                        // (1) 네트워크 연결: 해당 네트워크 그룹이 있으면 그 안으로 이동
                        // -----------------------------------------------------------
                        if (info.NetworkSettings != null && info.NetworkSettings.Networks != null)
                        {
                            foreach (var netKvp in info.NetworkSettings.Networks)
                            {
                                string netName = netKvp.Key;
                                if (netName == "bridge") continue;

                                // 시트에 해당 이름의 네트워크 그룹이 있는지 확인
                                var existingGroup = ActiveSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == netName);

                                if (existingGroup == null)
                                {
                                    // 없으면 그룹 생성
                                    existingGroup = new GroupViewModel(x - 50, y - 50, 300, 300, _networkService, _dialogService, netName)
                                    {
                                        Type = GroupType.Network
                                    };
                                    ActiveSheet.AddGroup(existingGroup);
                                }

                                // 방금 만든 노드를 그룹에 추가 (자동으로 위치 조정 및 포함 처리)
                                var newNode = ActiveSheet.Nodes.Last(); // 방금 추가한 노드
                                existingGroup.AddNode(newNode);
                            }
                        }

                        // -----------------------------------------------------------
                        // (2) 볼륨 연결 (오른쪽 배치)
                        // -----------------------------------------------------------
                        if (info.Mounts != null)
                        {
                            int volIndex = 0;
                            foreach (var mount in info.Mounts)
                            {
                                if (mount.Type == "volume")
                                {
                                    string volName = mount.Name;
                                    string destination = mount.Destination;

                                    var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n => n.Type == NodeType.Volume && n.Name == volName);
                                    NodeViewModel targetVolNode;

                                    if (existingVolNode != null)
                                    {
                                        targetVolNode = existingVolNode;
                                    }
                                    else
                                    {
                                        // 볼륨 노드 생성
                                        var volModel = new DockerVolume { Name = volName };
                                        ActiveSheet.CreateVolumeAt(volModel, x + 250, y + (volIndex * 120));
                                        targetVolNode = ActiveSheet.Nodes.Last();
                                    }

                                    // 선 연결
                                    var newNode = ActiveSheet.Nodes.LastOrDefault(n => n.ContainerId == container.Id);
                                    if (newNode != null)
                                    {
                                        // 커넥터 생성 로직이 SheetViewModel의 AddConnection은 단순해서 여기선 직접 추가
                                        bool connExists = ActiveSheet.Connectors.Any(c =>
                                            (c.Source == newNode && c.Target == targetVolNode) ||
                                            (c.Source == targetVolNode && c.Target == newNode));

                                        if (!connExists)
                                        {
                                            var conn = new ConnectorViewModel(
                                                newNode, targetVolNode,
                                                PortDirection.Right, PortDirection.Left,
                                                _dialogService)
                                            {
                                                RelationType = RelationType.VolumeMount,
                                                MountPath = destination
                                            };
                                            ActiveSheet.Connectors.Add(conn);
                                        }
                                    }
                                    volIndex++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowMessage($"연관 정보 로드 실패: {ex.Message}");
                    }
                }
            }
            // [CASE 2] 볼륨 (DockerVolume)
            else if (item is DockerVolume volume)
            {
                ActiveSheet.CreateVolumeAt(volume, x, y);
                IsModified = true;
            }
            else if (item is DockerInternet internet)
            {
                // 인터넷 노드는 복잡한 로직 없이 바로 생성
                ActiveSheet.CreateInternetAt(internet, x, y);
                IsModified = true;
            }
            // [CASE 4] 네트워크 (DockerGroup)
            else if (item is DockerGroup network)
            {
                var groupVm = new GroupViewModel(x, y, 300, 300, _networkService, _dialogService, network.Name)
                {
                    Type = GroupType.Network
                };
                ActiveSheet.AddGroup(groupVm);
                IsModified = true;
            }
        }

        // 비동기 컨테이너 생성 (모달 입력 처리용)
        public async Task CreateNewContainerNodeAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes,
    string restartPolicy, long memoryMb, double cpuCount, double x, double y)
        {
            if (ActiveSheet == null) return;

            // [1] 볼륨 리스트 분류
            var namedVolumesToDraw = new List<string>();

            foreach (var vol in volumes)
            {
                bool isBindMount = System.Text.RegularExpressions.Regex.IsMatch(vol, @"^([a-zA-Z]:[\\/]|/|\.|~)");

                if (!isBindMount)
                {
                    namedVolumesToDraw.Add(vol);
                }
            }

            // [2] 이미지 태그 처리
            if (image.Contains(":"))
            {
                int lastColon = image.LastIndexOf(':');
                tag = image.Substring(lastColon + 1);
                image = image.Substring(0, lastColon);
            }

            // [3] Placeholder 노드 생성 (임시 노란색)
            var node = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = $"{name} (Creating...)",
                ImageName = $"{image}:{tag}",
                Type = NodeType.Container,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107" // Creating...
            };
            ActiveSheet.Nodes.Add(node);

            try
            {
                // ★ [핵심 수정] 다운로드(Pull) 실패 시에도 로컬 이미지로 진행하도록 예외 처리 추가
                try
                {
                    // [4] 이미지 다운로드 시도
                    await _imageService.PullImageAsync(image, tag);
                }
                catch (Exception pullEx)
                {
                    // Pull 실패(권한 없음, 인터넷 없음 등) 시 로그만 남기고 무시 -> 로컬 이미지 확인으로 넘어감
                    Debug.WriteLine($"[DockerDiscovery] Pull failed: {pullEx.Message}. Trying to use local image...");
                }

                // [5] 컨테이너 생성 및 실행 (로컬에 이미지가 있으면 여기서 성공함)
                string containerId = await _containerService.CreateAndStartContainerAsync(
                    name, image, tag, ports, envs, volumes, restartPolicy, memoryMb, cpuCount);

                // [6] 노드 정보 갱신 (완료 상태)
                node.Name = name;
                node.ContainerId = containerId;

                node.PortInfo = string.Join(", ", ports);
                node.PortBindings = ports;
                node.EnvironmentVariables = envs;
                node.RestartPolicy = restartPolicy;

                node.IsCreating = false;
                node.StatusColor = "#28a745"; // Running (Green)

                // 통계 기록
                RegisterTemplateUsage($"{image}:{tag}");

                // =========================================================
                // [7] Named Volume만 시각화 (원기둥 노드 생성)
                // =========================================================
                int volIndex = 0;
                foreach (var volStr in namedVolumesToDraw)
                {
                    string volName = volStr;
                    string mountPath = "/data"; // 기본값

                    int lastColon = volStr.LastIndexOf(':');
                    if (lastColon > 0)
                    {
                        volName = volStr.Substring(0, lastColon);
                        mountPath = volStr.Substring(lastColon + 1);
                    }

                    // A. 이미 화면에 있는 볼륨 노드인지 확인 (재사용)
                    var existingVolNode = ActiveSheet.Nodes
                        .FirstOrDefault(n => n.Type == NodeType.Volume && n.Name == volName);

                    NodeViewModel targetVolNode;

                    if (existingVolNode != null)
                    {
                        targetVolNode = existingVolNode;
                    }
                    else
                    {
                        // B. 없으면 새로 생성 (컨테이너 오른쪽에 배치)
                        targetVolNode = new NodeViewModel(_containerService, _volumeService, _dialogService)
                        {
                            Name = volName,
                            Type = NodeType.Volume, // 🛢️ 원기둥
                            ImageName = "local",    // 드라이버명 등 표시용
                            X = x + 250,
                            Y = y + (volIndex * 100), // 아래로 쌓이게
                            StatusColor = "#E67E22"   // 주황색
                        };
                        ActiveSheet.Nodes.Add(targetVolNode);
                    }

                    // C. 선 연결 (Connector)
                    bool connExists = ActiveSheet.Connectors.Any(c =>
                        (c.Source == node && c.Target == targetVolNode) ||
                        (c.Source == targetVolNode && c.Target == node));

                    if (!connExists)
                    {
                        var conn = new ConnectorViewModel(
                            node, targetVolNode,
                            PortDirection.Right, PortDirection.Left,
                            _dialogService)
                        {
                            RelationType = RelationType.VolumeMount,
                            MountPath = mountPath
                        };
                        ActiveSheet.Connectors.Add(conn);
                    }
                    volIndex++;
                }

                // 전체 아이템 목록 갱신
                UpdateAvailableItems();
            }
            catch (Exception ex)
            {
                // 생성(CreateAndStartContainerAsync)조차 실패했을 때만 에러 출력 및 노드 삭제
                _dialogService.ShowMessage($"생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        public async Task CreateNewVolumeNodeAsync(string name, string driver, double x, double y)
        {
            if (ActiveSheet == null) return;

            // Placeholder
            var node = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = $"{name} (Creating...)",
                ImageName = driver,
                Type = NodeType.Volume,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107"
            };
            ActiveSheet.Nodes.Add(node);

            try
            {
                await _volumeService.CreateVolumeAsync(name, driver);

                // 완료
                node.Name = name;
                node.ContainerId = ""; // 볼륨은 보통 ID가 이름과 같거나 별도 관리됨.
                node.IsCreating = false;
                node.StatusColor = "#E67E22"; // 주황색
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"볼륨 생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        // 2. 네트워크 생성 (비동기) -> 그룹 생성
        public async Task CreateNewNetworkNodeAsync(string name, string driver, double x, double y, double w = 300, double h = 300)
        {
            if (ActiveSheet == null) return;

            // 사용자가 그린 크기(w, h)로 그룹 생성
            var groupVm = new GroupViewModel(x, y, w, h, _networkService, _dialogService, $"{name} (Creating...)")
            {
                Type = GroupType.Network
            };
            ActiveSheet.AddGroup(groupVm);

            try
            {
                // 실제 도커 네트워크 생성
                string netId = await _networkService.CreateNetworkAsync(name, driver);

                // 성공 시 이름 확정 (그룹은 ID 대신 이름을 주로 씀)
                groupVm.Title = name;
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"네트워크 생성 실패: {ex.Message}");
                // 실패하면 껍데기만 남은 그룹 제거
                ActiveSheet.Groups.Remove(groupVm);
            }
        }

        private void SaveAction(object? obj)
        {
            // 경로가 이미 잡혀있으면 -> 덮어쓰기 (QuickSave)
            if (!string.IsNullOrEmpty(CurrentFilePath))
            {
                bool success = FileService.QuickSave(this, CurrentFilePath);
                if (success)
                {
                    _dialogService.ShowMessage("저장되었습니다.");
                    IsModified = false;
                }
            }
            else
            {
                // 경로가 없으면 -> 다른 이름으로 저장 로직 수행
                SaveAsAction(obj);
            }
        }

        private void SaveAsAction(object? obj)
        {
            // FileService에서 대화상자를 띄우고, 저장한 경로를 받아옴
            string? savedPath = FileService.SaveDiagramAs(this, _dialogService);

            // 저장을 성공적으로 했으면, 현재 경로 업데이트
            if (!string.IsNullOrEmpty(savedPath))
            {
                CurrentFilePath = savedPath;
                IsModified = false;
            }
        }

        private async Task LoadActionAsync(object? obj)
        {
            // 변경사항이 있다면 물어보기
            if (IsModified)
            {
                if (!_dialogService.ShowConfirm("변경 사항이 저장되지 않았습니다. 계속하시겠습니까?", "확인"))
                    return;
            }

            // 파일 불러오기 시도
            // 3개 서비스 + 다이얼로그 서비스 전달
            string? loadedPath = await FileService.LoadDiagramWithDialogAsync(this, _containerService, _volumeService, _networkService, _dialogService);

            // 성공적으로 불러왔다면 경로 업데이트 및 상태 갱신
            if (!string.IsNullOrEmpty(loadedPath))
            {
                CurrentFilePath = loadedPath;
                IsModified = false;

                // ★ [핵심 추가] 불러온 노드들을 "깨우는" 로직 실행 (회색 -> 녹색/빨간색)
                await RestoreLiveState();
            }
        }

        private async Task RestoreLiveState()
        {
            if (Sheets == null) return;

            foreach (var sheet in Sheets)
            {
                // 1. 일반 노드 갱신
                foreach (var node in sheet.Nodes)
                {
                    // 연결 정보(선) 갱신을 위해 부모 시트 재설정 (파일 로드 시 끊겨있을 수 있음)
                    node.ParentSheet = sheet;

                    // 도커 상태 조회 (회색 -> 녹색/빨간색, IP 주소 등 갱신)
                    await node.RefreshDetailsAsync();
                }

                // 2. 그룹 내부 노드 갱신 (네트워크 그룹 등)
                foreach (var group in sheet.Groups)
                {
                    group.ParentSheet = sheet;
                    if (group.ContainedNodes != null)
                    {
                        foreach (var node in group.ContainedNodes)
                        {
                            node.ParentSheet = sheet;
                            await node.RefreshDetailsAsync();
                        }
                    }
                }
            }
        }

        private void AddSheet()
        {
            // ★ SheetViewModel 생성 시 서비스 전달
            var newSheet = new SheetViewModel($"Sheet {Sheets.Count + 1}", _containerService, _volumeService, _dialogService);
            Sheets.Add(newSheet);
            ActiveSheet = newSheet;
        }

        public void DeleteSheet(SheetViewModel sheet)
        {
            if (Sheets.Count <= 1) return;

            if (ActiveSheet == sheet)
            {
                int index = Sheets.IndexOf(sheet);
                int nextIndex = index > 0 ? index - 1 : index + 1;
                if (nextIndex >= 0 && nextIndex < Sheets.Count)
                    ActiveSheet = Sheets[nextIndex];
            }
            Sheets.Remove(sheet);
        }

        public void MoveSheet(int oldIndex, int newIndex)
        {
            if (oldIndex < 0 || oldIndex >= Sheets.Count || newIndex < 0 || newIndex >= Sheets.Count) return;
            Sheets.Move(oldIndex, newIndex);
        }

        public void RenameSheet(SheetViewModel sheet, string newName)
        {
            if (sheet != null && !string.IsNullOrWhiteSpace(newName)) sheet.Title = newName;
        }

        private void NavigateSheet(int direction)
        {
            if (ActiveSheet == null || Sheets.Count <= 1) return;
            int currentIndex = Sheets.IndexOf(ActiveSheet);
            int newIndex = currentIndex + direction;
            if (newIndex >= 0 && newIndex < Sheets.Count) ActiveSheet = Sheets[newIndex];
        }

        public async void AddConnection(NodeViewModel source, NodeViewModel target, PortDirection sourceDir, PortDirection targetDir)
        {
            if (ActiveSheet == null || source == target) return;

            // 1. 유효성 검사 (네트워크 관련 로직 완전 삭제)
            if (!IsValidConnection(source.Type, target.Type))
            {
                _dialogService.ShowMessage("연결할 수 없는 조합입니다.\n(볼륨끼리는 연결할 수 없으며, 네트워크 연결은 그룹 안으로 드래그하여 넣으세요.)");
                return;
            }

            // 2. 방향 정규화 (Container가 항상 Source가 되도록)
            NodeViewModel finalSource = source;
            NodeViewModel finalTarget = target;
            PortDirection finalSourceDir = sourceDir;
            PortDirection finalTargetDir = targetDir;

            // 만약 타겟이 컨테이너고 소스가 다른거라면(예: 볼륨) 방향 반대로 뒤집기
            if (source.Type != NodeType.Container && target.Type == NodeType.Container)
            {
                finalSource = target;
                finalTarget = source;
                finalSourceDir = targetDir;
                finalTargetDir = sourceDir;
            }

            // =========================================================
            // [CASE 1] 컨테이너 <-> 볼륨 (물리적 연결 시도)
            // =========================================================
            if (finalSource.Type == NodeType.Container && finalTarget.Type == NodeType.Volume)
            {
                bool isSuccess = await ConnectVolumeToContainerAsync(finalSource, finalTarget);
                if (!isSuccess) return; // 실패하면 선 안 그음
            }

            // [삭제됨] CASE 2: 네트워크 연결 로직
            // 이유: 님 말씀대로 네트워크는 '그룹 영역'이므로 선(Connector)으로 연결하지 않음.
            //       MainViewModel.CreateNodeAtAsync나 GroupViewModel.AddNode에서 처리됨.

            // 3. 중복 연결 방지 및 실제 선(Connector) 추가
            bool exists = ActiveSheet.Connectors.Any(c =>
                (c.Source == finalSource && c.Target == finalTarget) ||
                (c.Source == finalTarget && c.Target == finalSource));

            if (!exists)
            {
                // ConnectorViewModel 생성 (서비스는 DialogService만 필요)
                var newConnector = new ConnectorViewModel(finalSource, finalTarget, finalSourceDir, finalTargetDir, _dialogService);

                // 관계 타입 설정
                if (finalTarget.Type == NodeType.Volume)
                {
                    newConnector.RelationType = RelationType.VolumeMount;
                    // 볼륨 마운트 경로는 ConnectVolumeToContainerAsync 내부 로직에서 결정되지만,
                    // UI상 표시를 위해 기본값 혹은 추후 동기화 로직 필요
                    newConnector.MountPath = "/data";
                }
                else
                {
                    // 남은 경우의 수는 컨테이너 <-> 컨테이너 (Dependency) 뿐임
                    newConnector.RelationType = RelationType.Dependency;
                }

                ActiveSheet.Connectors.Add(newConnector);
            }

            IsModified = true;
        }

        // 네트워크 타입 검사 로직 삭제 -> 오직 컨테이너와 볼륨 관계만 정의
        private bool IsValidConnection(NodeType t1, NodeType t2)
        {
            // 연결 설정 불가능(볼륨 + 인터넷, 볼륨 + 볼륨)
            if (t1 == NodeType.Volume && t2 == NodeType.Volume || ((t1 == NodeType.Internet && t2 == NodeType.Volume) ||
                (t1 == NodeType.Volume && t2 == NodeType.Internet))) return false;

            // 그 외 모든 경우 가능
            return true;
        }

        public void ClearSelection() => SelectedElement = null;

        public async Task DeleteSelectedAsync()
        {
            if (SelectedElement == null || ActiveSheet == null) return;

            // =========================================================
            // [CASE 1] 연결선(Connector) 삭제 시
            // =========================================================
            if (SelectedElement is ConnectorViewModel conn)
            {
                // 1. 컨테이너 ↔ 컨테이너 (단순 의존성)
                if (conn.RelationType == RelationType.Dependency)
                {
                    ActiveSheet.Connectors.Remove(conn);
                }
                // 2. 컨테이너 ↔ 볼륨 (Volume Mount)
                else if (conn.RelationType == RelationType.VolumeMount)
                {
                    var result = MessageBox.Show(
                        "실제 Docker 컨테이너에서도 볼륨 연결을 해제하시겠습니까?\n" +
                        "[예(Yes)] : Docker에서 해제 (물리적 해제 - 재생성)\n" +
                        "[아니요(No)] : 시트에서만 제거 (논리적 삭제)\n" +
                        "[취소(Cancel)] : 작업 취소",
                        "볼륨 연결 해제",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel) return;

                    if (result == MessageBoxResult.Yes)
                    {
                        bool success = await UnmountVolumeFromContainerAsync(conn.Source, conn.Target);
                        if (success) ActiveSheet.Connectors.Remove(conn);
                    }
                    else
                    {
                        ActiveSheet.Connectors.Remove(conn);
                    }
                }
            }

            // =========================================================
            // [CASE 2] 노드(Node) 삭제 시
            // =========================================================
            else if (SelectedElement is NodeViewModel node)
            {
                // 인터넷 노드 전용 로직: 질문 없이 즉시 삭제
                if (node.Type == NodeType.Internet)
                {
                    // 연결된 선(커넥터)들 먼저 제거
                    var related = ActiveSheet.Connectors
                        .Where(c => c.Source == node || c.Target == node).ToList();
                    foreach (var c in related) ActiveSheet.Connectors.Remove(c);

                    // 시트에서 노드 제거
                    ActiveSheet.Nodes.Remove(node);
                    IsModified = true;
                    SelectedElement = null;
                    return; // 인터넷 노드는 여기서 로직 종료
                }

                // --- 실체가 있는 노드(컨테이너/볼륨) 삭제 로직 ---
                var result = MessageBox.Show(
                    "선택한 항목을 삭제하시겠습니까?\n" +
                    "[예(Yes)] : Docker에서도 영구 삭제\n" +
                    "[아니요(No)] : 시트에서만 제거\n" +
                    "[취소(Cancel)] : 취소",
                    "삭제 옵션",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel) return;

                if (result == MessageBoxResult.Yes)
                {
                    if (!string.IsNullOrEmpty(node.ContainerId))
                    {
                        try
                        {
                            await Task.Run(async () =>
                            {
                                if (node.Type == NodeType.Container)
                                    await _containerService.RemoveContainerAsync(node.ContainerId);
                                // 볼륨 등 추가 리소스 삭제가 필요하다면 여기에 추가
                            });
                        }
                        catch (Exception ex)
                        {
                            _dialogService.ShowMessage($"Docker 리소스 삭제 실패: {ex.Message}\n\n목록에서 제거되지 않습니다.");
                            return;
                        }
                    }
                }

                // 공통: 연결된 선 제거 후 노드 제거
                var relatedConnectors = ActiveSheet.Connectors
                    .Where(c => c.Source == node || c.Target == node).ToList();
                foreach (var c in relatedConnectors) ActiveSheet.Connectors.Remove(c);

                ActiveSheet.Nodes.Remove(node);
                IsModified = true;
            }

            // =========================================================
            // [CASE 3] 그룹(Group) 삭제 시
            // =========================================================
            else if (SelectedElement is GroupViewModel group)
            {
                // 1. 네트워크 그룹일 경우 -> 실제 Docker 네트워크 삭제 시도
                if (group.Type == GroupType.Network)
                {
                    try
                    {
                        await _networkService.RemoveNetworkAsync(group.Title);
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowMessage($"네트워크 삭제 실패: {ex.Message}");
                        return;
                    }
                }

                // 2. 그룹 해제 (Ungroup): 내부 노드 방생
                if (group.ContainedNodes != null)
                {
                    foreach (var childNode in group.ContainedNodes.ToList())
                    {
                        childNode.X += group.X;
                        childNode.Y += group.Y;
                        ActiveSheet.Nodes.Add(childNode);
                    }
                }

                // 3. 그룹 삭제
                ActiveSheet.Groups.Remove(group);
                IsModified = true;
            }

            SelectedElement = null;
        }

        private async Task<bool> UnmountVolumeFromContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode)
        {
            string containerId = containerNode.ContainerId;
            string volumeNameToRemove = volumeNode.Name;

            bool keepBackup = false;

            // 호스트 임시 백업 폴더 경로 생성
            string tempHostPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docker_backup_" + Guid.NewGuid());

            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                // ---------------------------------------------------------
                // [STEP 1] 데이터 백업 (Backup)
                // ---------------------------------------------------------
                if (containerNode.IsRunning)
                {
                    await _containerService.StopContainerAsync(containerId);
                }

                if (!System.IO.Directory.Exists(tempHostPath))
                    System.IO.Directory.CreateDirectory(tempHostPath);

                // 마운트 경로 찾기 (Inspect)
                var inspect = await _containerService.InspectContainerAsync(containerId);
                string mountPath = "/data";
                foreach (var m in inspect.Mounts)
                {
                    if (m.Name == volumeNameToRemove) { mountPath = m.Destination; break; }
                }

                await _containerService.CopyFromContainerAsync(containerId, mountPath, tempHostPath);


                // ---------------------------------------------------------
                // [STEP 2] 기존 컨테이너 설정 조회 (설정 유지용)
                // ---------------------------------------------------------
                var oldConfig = inspect.Config;
                var oldHostConfig = inspect.HostConfig;

                // 2-1. 이미지 정보 (repo:tag 분리)
                string imageName = oldConfig.Image;
                string imgRepo = imageName;
                string imgTag = "latest";

                int lastColonIndex = imageName.LastIndexOf(':');
                if (lastColonIndex > 0)
                {
                    imgRepo = imageName[..lastColonIndex];
                    imgTag = imageName[(lastColonIndex + 1)..];
                }
                else
                {
                    imgRepo = imageName;
                    imgTag = "latest";
                }

                // 2-2. 환경변수 복사
                var envs = oldConfig.Env != null ? oldConfig.Env.ToList() : new List<string>();

                // 2-3. 포트 바인딩 복구
                var ports = new List<string>();
                if (oldHostConfig.PortBindings != null)
                {
                    foreach (var pb in oldHostConfig.PortBindings)
                    {
                        string containerPort = pb.Key.Split('/')[0];
                        if (pb.Value != null && pb.Value.Count > 0)
                        {
                            string hostPort = pb.Value[0].HostPort;
                            ports.Add($"{hostPort}:{containerPort}");
                        }
                    }
                }

                // 2-4. 기존 볼륨 목록에서 제거할 볼륨 제외
                var newVolumes = new List<string>();
                if (oldHostConfig.Binds != null)
                {
                    foreach (var bind in oldHostConfig.Binds)
                    {
                        // bind 문자열 예시: "my_vol:/var/lib/mysql"
                        if (!bind.StartsWith(volumeNameToRemove + ":"))
                        {
                            newVolumes.Add(bind);
                        }
                    }
                }

                // ---------------------------------------------------------
                // [STEP 3] 기존 컨테이너 삭제
                // ---------------------------------------------------------
                await _containerService.RemoveContainerAsync(containerId);


                // ---------------------------------------------------------
                // [STEP 4] 컨테이너 재생성 (볼륨 제외됨)
                // ---------------------------------------------------------
                string newId = await _containerService.CreateAndStartContainerAsync(
                    containerNode.Name,
                    imgRepo,
                    imgTag,
                    ports,
                    envs,
                    newVolumes,
                    oldHostConfig.RestartPolicy.Name.ToString(),
                    0, 0
                );


                // ---------------------------------------------------------
                // [STEP 5] 데이터 복원 (Restore)
                // ---------------------------------------------------------
                containerNode.ContainerId = newId;

                string folderName = System.IO.Path.GetFileName(mountPath.TrimEnd('/'));
                string actualSourcePath = System.IO.Path.Combine(tempHostPath, folderName);

                if (System.IO.Directory.Exists(actualSourcePath))
                {
                    await _containerService.CopyToContainerAsync(newId, actualSourcePath, mountPath);
                }
                else
                {
                    await _containerService.CopyToContainerAsync(newId, tempHostPath, mountPath);
                }

                // UI 갱신
                await containerNode.RefreshDetailsAsync();

                _dialogService.ShowMessage("볼륨 연결 해제 및 컨테이너 재생성이 완료되었습니다.");

                return true;
            }
            catch (Exception ex)
            {
                keepBackup = true;
                _dialogService.ShowMessage($"해제 중 오류 발생: {ex.Message}\n\n백업: {tempHostPath}");
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;

                if (!keepBackup && Directory.Exists(tempHostPath))
                {
                    try
                    {
                        Directory.Delete(tempHostPath, true);
                        Debug.WriteLine($"[DockerDiscovery] Deleted temp backup folder: {tempHostPath}");
                    }
                    catch (Exception delEx)
                    {
                        Debug.WriteLine($"[DockerDiscovery] Failed to delete temp backup folder: {tempHostPath}");
                        Debug.WriteLine($"[Cleanup] Delete exception: {delEx}");
                    }
                }
                else if (keepBackup)
                {
                    Debug.WriteLine($"[DockerDiscovery] Keeping temp backup folder (due to failure): {tempHostPath}");
                }
            }
        }

        public async Task<bool> ConnectVolumeToContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode)
        {
            // 1. 다이얼로그로 경로와 소유자 정보 입력받기
            var dlg = new Views.MountDialog();
            dlg.Owner = Application.Current.MainWindow;

            if (dlg.ShowDialog() != true) return false;

            string mountPath = dlg.MountPath; // 예: /var/lib/mysql
            string owner = dlg.VolumeOwner;   // 예: mysql:mysql

            string containerId = containerNode.ContainerId;
            string volumeName = volumeNode.Name;

            bool keepBackup = false;

            // 호스트 임시 백업 폴더 경로 생성
            string tempHostPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docker_backup_" + Guid.NewGuid());

            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                // ---------------------------------------------------------
                // [STEP 1] 데이터 백업
                // ---------------------------------------------------------
                if (containerNode.IsRunning)
                {
                    await _containerService.StopContainerAsync(containerId);
                }

                if (!System.IO.Directory.Exists(tempHostPath))
                    System.IO.Directory.CreateDirectory(tempHostPath);

                // 기존 데이터가 있다면 백업 (경로 없으면 에러 날 수 있으니 try 감쌈)
                try
                {
                    await _containerService.CopyFromContainerAsync(containerId, mountPath, tempHostPath);
                }
                catch { /* 경로 없으면 백업 스킵 */ }


                // ---------------------------------------------------------
                // [STEP 2] 기존 컨테이너 설정 조회
                // ---------------------------------------------------------
                var inspect = await _containerService.InspectContainerAsync(containerId);
                var oldConfig = inspect.Config;
                var oldHostConfig = inspect.HostConfig;

                // 이미지 파싱
                string imageName = oldConfig.Image;
                string imgRepo = imageName;
                string imgTag = "latest";
                int lastColonIndex = imageName.LastIndexOf(':');
                if (lastColonIndex > 0)
                {
                    imgRepo = imageName[..lastColonIndex];
                    imgTag = imageName[(lastColonIndex + 1)..];
                }
                else
                {
                    imgRepo = imageName;
                    imgTag = "latest";
                }

                // 포트 복구
                var ports = new List<string>();
                if (oldHostConfig.PortBindings != null)
                {
                    foreach (var pb in oldHostConfig.PortBindings)
                    {
                        string containerPort = pb.Key.Split('/')[0];
                        if (pb.Value != null && pb.Value.Count > 0)
                            ports.Add($"{pb.Value[0].HostPort}:{containerPort}");
                    }
                }

                // 환경변수 복구
                var envs = oldConfig.Env != null ? oldConfig.Env.ToList() : new List<string>();

                // 볼륨 추가
                var volumes = new List<string>();
                if (oldHostConfig.Binds != null)
                {
                    volumes.AddRange(oldHostConfig.Binds);
                }
                volumes.Add($"{volumeName}:{mountPath}");


                // ---------------------------------------------------------
                // [STEP 3] 재생성
                // ---------------------------------------------------------
                await _containerService.RemoveContainerAsync(containerId);

                string newId = await _containerService.CreateAndStartContainerAsync(
                    containerNode.Name, imgRepo, imgTag, ports, envs, volumes,
                    oldHostConfig.RestartPolicy.Name.ToString(), 0, 0
                );


                // ---------------------------------------------------------
                // [STEP 4] 데이터 복원
                // ---------------------------------------------------------
                string folderName = System.IO.Path.GetFileName(mountPath.TrimEnd('/'));
                string actualSourcePath = System.IO.Path.Combine(tempHostPath, folderName);

                if (System.IO.Directory.Exists(actualSourcePath))
                {
                    await _containerService.CopyToContainerAsync(newId, actualSourcePath, mountPath);
                }
                else
                {
                    await _containerService.CopyToContainerAsync(newId, tempHostPath, mountPath);
                }


                // ---------------------------------------------------------
                // [STEP 5] 권한 수정
                // ---------------------------------------------------------
                if (!string.IsNullOrWhiteSpace(owner))
                {
                    string cmd = $"chown -R {owner} {mountPath}";
                    await _containerService.ExecuteCommandAsync(newId, cmd);
                }

                // ---------------------------------------------------------
                // [STEP 6] 마무리
                // ---------------------------------------------------------
                containerNode.ContainerId = newId;
                await containerNode.RefreshDetailsAsync();

                _dialogService.ShowMessage("볼륨 연결 완료!");
                return true;
            }
            catch (Exception ex)
            {
                keepBackup = true;
                _dialogService.ShowMessage($"오류 발생: {ex.Message}");
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
                if (!keepBackup && Directory.Exists(tempHostPath)) Directory.Delete(tempHostPath, true);
            }
        }

        private void SyncCollection<T>(ObservableCollection<T> uiCollection, List<T> newItems, Func<T, string> keySelector)
        {
            // 1. 제거해야 할 항목 찾기
            var newKeys = new HashSet<string>(newItems.Select(keySelector));
            var toRemove = uiCollection.Where(item => !newKeys.Contains(keySelector(item))).ToList();

            foreach (var item in toRemove)
            {
                uiCollection.Remove(item);
            }

            // 2. 추가해야 할 항목 찾기
            var currentKeys = new HashSet<string>(uiCollection.Select(keySelector));
            foreach (var item in newItems)
            {
                if (!currentKeys.Contains(keySelector(item)))
                {
                    uiCollection.Add(item);
                }
            }

            // 상태 업데이트: 만약 ID는 같은데 상태가 변했다면 여기서 속성만 복사해줄 수 있음
            if (typeof(T) == typeof(DockerContainer))
            {
                var newItemMap = newItems.ToDictionary(keySelector);
                foreach (var item in uiCollection)
                {
                    if (newItemMap.TryGetValue(keySelector(item), out var newItem))
                    {
                        var oldContainer = item as DockerContainer;
                        var newContainer = newItem as DockerContainer;
                        if (oldContainer != null && newContainer != null)
                        {
                            // 상태나 색상이 다를 때만 갱신 (화면 깜빡임 방지)
                            if (oldContainer.State != newContainer.State)
                            {
                                oldContainer.State = newContainer.State;
                                oldContainer.StateColor = newContainer.StateColor;
                            }
                        }
                    }
                }
            }
        }
        private void AttachSheetEvents()
        {
            if (ActiveSheet != null)
            {
                ActiveSheet.Nodes.CollectionChanged -= Nodes_CollectionChanged;
                ActiveSheet.Nodes.CollectionChanged += Nodes_CollectionChanged;

                ActiveSheet.Groups.CollectionChanged -= Groups_CollectionChanged;
                ActiveSheet.Groups.CollectionChanged += Groups_CollectionChanged;

                // 이미 화면에 있는 기존 노드들도 감시를 붙여야 함
                foreach (var node in ActiveSheet.Nodes)
                {
                    node.OnModified -= Node_OnModified; // 중복 구독 방지
                    node.OnModified += Node_OnModified;
                }

                // 이미 화면에 있는 기존 그룹들도 감시를 붙여야 함
                foreach (var group in ActiveSheet.Groups)
                {
                    group.OnModified -= Node_OnModified;
                    group.OnModified += Node_OnModified;
                }

                // 커넥터 리스트 변경 감시
                ActiveSheet.Connectors.CollectionChanged -= Connectors_CollectionChanged;
                ActiveSheet.Connectors.CollectionChanged += Connectors_CollectionChanged;

                // 이미 화면에 있는 기존 커넥터들도 감시를 붙여야 함
                foreach (var conn in ActiveSheet.Connectors)
                {
                    conn.OnModified -= Connector_OnModified;
                    conn.OnModified += Connector_OnModified;
                }
            }
        }

        private void Nodes_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (NodeViewModel node in e.NewItems)
                {
                    node.OnModified -= Node_OnModified;
                    node.OnModified += Node_OnModified;
                }
            }
            if (e.OldItems != null)
            {
                foreach (NodeViewModel node in e.OldItems)
                {
                    node.OnModified -= Node_OnModified;
                }
            }
            UpdateAvailableItems();
            MarkAsModified();
        }

        private void Node_OnModified(object? sender, EventArgs e)
        {
            MarkAsModified();
        }

        private void Connectors_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (ConnectorViewModel conn in e.NewItems)
                {
                    conn.OnModified -= Connector_OnModified;
                    conn.OnModified += Connector_OnModified;
                }
            }
            if (e.OldItems != null)
            {
                foreach (ConnectorViewModel conn in e.OldItems)
                {
                    conn.OnModified -= Connector_OnModified;
                }
            }
            MarkAsModified();
        }

        private void Connector_OnModified(object? sender, EventArgs e)
        {
            MarkAsModified();
        }

        private void Groups_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (GroupViewModel group in e.NewItems)
                {
                    group.OnModified -= Node_OnModified;
                    group.OnModified += Node_OnModified;
                }
            }
            if (e.OldItems != null)
            {
                foreach (GroupViewModel group in e.OldItems)
                {
                    group.OnModified -= Node_OnModified;
                }
            }
            UpdateAvailableItems();
            MarkAsModified();
        }

        public async Task OnDockerStartedAsync()
        {
            Debug.WriteLine("[MainViewModel] Docker started signal received. Refreshing...");

            // DockerServiceHelper에서 이미 Ping 대기를 마쳤으므로, 
            // 여기서는 바로 데이터 동기화와 상태 갱신을 진행합니다.

            // 1. 전체 목록(이미지, 네트워크, 컨테이너 등) 다시 가져오기
            await SyncWithDockerEngine();

            // 2. 화면에 있는 노드들의 상태(Running/Stopped/Color) 다시 조회하여 녹색으로 변경
            await RestoreLiveState();
        }

        private void ExecuteFlowClear(object? obj)
        {
            if (ActiveSheet != null && _dialogService.ShowConfirm("현재 시트의 모든 내용을 지우시겠습니까?", "Flow Clear"))
            {
                ActiveSheet.Nodes.Clear();
                ActiveSheet.Connectors.Clear();
                ActiveSheet.Groups.Clear();
            }
        }

        private void ExecuteFlowAllClear(object? obj)
        {
            if (_dialogService.ShowConfirm("모든 시트의 내용을 초기화 하시겠습니까?", "Flow All Clear"))
            {
                foreach (var sheet in Sheets)
                {
                    sheet.Nodes.Clear();
                    sheet.Connectors.Clear();
                    sheet.Groups.Clear();
                }
            }
        }

        private void ExecuteDeleteAllSheet(object? obj)
        {
            if (_dialogService.ShowConfirm("모든 시트를 삭제하시겠습니까?", "Delete All Sheet"))
            {
                Sheets.Clear();
                if (AddSheetCommand.CanExecute(null)) AddSheetCommand.Execute(null);
            }
        }

        public async Task CreateNewNetworkGroupAsync(string name, string driver, double x, double y, double w, double h)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                string networkId = await _networkService.CreateNetworkAsync(name, driver);
                var newNetworkGroup = new GroupViewModel(x, y, w, h, _networkService, _dialogService, name)
                {
                    Id = networkId,           // 도커 ID 저장
                    Type = GroupType.Network, // ★ 중요: 파란 점선 모양 적용
                    Driver = driver,
                    ParentSheet = this.ActiveSheet
                };

                ActiveSheet.Groups.Add(newNetworkGroup);
                ActiveSheet.UpdateGroupLayering();
                ActiveSheet.RefreshGroupContainment(newNetworkGroup);

                SelectedElement = newNetworkGroup;
                IsModified = true;
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"네트워크 생성 실패: {ex.Message}");
            }
        }
    }
}