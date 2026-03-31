using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 다이어그램 애플리케이션의 전체 상태를 관장하는 최상위(Root) 뷰모델입니다.
    /// 멀티 탭(시트) 관리, 전역 커맨드 처리, 사이드바 목록 동기화, 파일 저장/불러오기 등을 담당하며,
    /// 사용자의 UI 조작과 하위 도커 백엔드 서비스 사이를 이어주는 중앙 통제 센터 역할을 수행합니다.
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        // 의존성 주입(DI)을 통해 전달받는 기본 도커 서비스 및 팝업창 관리 서비스
        private readonly IDockerService _defaultDockerService;
        private readonly IDialogService _dialogService;

        // 현재 활성화된 시트(로컬 PC 또는 원격 SSH)의 종류에 따라 적절한 도커 서비스로 동적 스위칭하여 접근하기 위한 헬퍼 프로퍼티들
        private IContainerService _containerService => ActiveSheet?.DockerService ?? _defaultDockerService;
        private IVolumeService _volumeService => ActiveSheet?.DockerService ?? _defaultDockerService;
        private INetworkService _networkService => ActiveSheet?.DockerService ?? _defaultDockerService;
        private IImageService _imageService => ActiveSheet?.DockerService ?? _defaultDockerService;
        private ISystemService _systemService => ActiveSheet?.DockerService ?? _defaultDockerService;

        // 도커 컴포즈(docker-compose.yml) 내보내기 커맨드
        public ICommand ExportComposeCommand { get; }

        // 현재 다이어그램에 저장되지 않은 변경사항이 있는지 여부를 나타내는 상태 플래그 (Dirty Flag)
        private bool _isModified = false;
        public bool IsModified
        {
            get => _isModified;
            set { _isModified = value; OnPropertyChanged(); }
        }

        // --- 1. 기본 맵 속성 ---
        // 캔버스(도화지)의 가로/세로 전체 크기를 나타냅니다.
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

        // 현재 작업 중인 다이어그램의 실제 저장 파일 경로 (없으면 null)
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
        // 여러 개의 다이어그램 탭(로컬, 원격 서버 등)을 보관하는 리스트
        public ObservableCollection<SheetViewModel> Sheets { get; set; } = new();

        // 현재 사용자가 화면에서 보고 있는 활성화된 시트(탭)
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

        // 캔버스 위에서 현재 선택된 요소(노드, 선, 그룹 등)
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

        // 도커 엔진과의 통신이 진행 중인지 여부를 나타내는 플래그
        private bool _isSyncing = false;

        // 상세 정보 사이드 패널의 열림/닫힘 상태
        public bool IsDetailPanelOpen => _selectedElement != null;

        // --- 3. 아코디언 데이터 ---
        // 왼쪽 사이드바(아코디언)에 표시될 템플릿 및 도커 실제 리소스 목록들
        public ObservableCollection<TemplateItem> Templates { get; } = new();
        public ObservableCollection<DockerContainer> ExistingContainers { get; } = new();
        public ObservableCollection<DockerVolume> ExistingVolumes { get; } = new();
        public ObservableCollection<DockerNetworkGroup> ExistingNetworks { get; } = new();
        public ObservableCollection<DockerImage> LocalImages { get; } = new();

        // 템플릿 추천을 위해 사용자가 자주 생성한 이미지 내역을 저장하는 딕셔너리
        private Dictionary<string, int> _usageStats = new Dictionary<string, int>();

        // --- 4. 명령(Commands) ---
        // 상단 메뉴나 단축키 등에 연결된 전역 커맨드들
        public ICommand AddSheetCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ClosePanelCommand { get; }
        public ICommand PrevSheetCommand { get; }
        public ICommand NextSheetCommand { get; }
        public ICommand DeleteImageCommand { get; }

        // 주기적으로 도커 엔진을 조회하여 실시간 상태를 가져오기 위한 타이머
        private DispatcherTimer _autoSyncTimer;

        // 마지막으로 도커 정보를 동기화한 시각 텍스트
        private string _lastSyncTime = "Syncing...";
        public string LastSyncTime
        {
            get => _lastSyncTime;
            set { _lastSyncTime = value; OnPropertyChanged(); }
        }

        // 사이드바 각 탭의 검색어 입력값
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

        // 사이드바 내 아이템 삭제 커맨드들
        public ICommand DeleteContainerItemCommand { get; }
        public ICommand DeleteVolumeItemCommand { get; }
        public ICommand DeleteNetworkItemCommand { get; }

        // 파일 처리 관련 커맨드들
        public ICommand SaveCommand { get; }
        public ICommand SaveAsCommand { get; }
        public ICommand LoadCommand { get; }

        // 캔버스/시트 삭제 관련 커맨드들
        public ICommand FlowClearCommand { get; }
        public ICommand FlowAllClearCommand { get; }
        public ICommand DeleteAllSheetCommand { get; }

        // 추가 확장 기능 커맨드들
        public ICommand ImportComposeCommand { get; }
        public ICommand SystemPruneCommand { get; }

        // --- 생성자 ---
        /// <summary>
        /// MainViewModel을 초기화합니다.
        /// 기본 도커 서비스를 할당하고, 첫 번째 'Local PC' 시트를 생성하며,
        /// UI 메뉴와 연결될 수많은 명령(커맨드)들을 세팅하고 도커 자동 동기화 타이머를 시작합니다.
        /// </summary>
        public MainViewModel(IDockerService dockerService, IDialogService dialogService)
        {
            // 유효성 검사 및 할당
            if (dockerService == null) throw new ArgumentNullException(nameof(dockerService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            // =================================================================
            // ★ [수정된 부분 2] 생성자에서 기본 서비스 할당 및 첫 시트 생성 방식 변경
            // =================================================================
            _defaultDockerService = dockerService;

            // 첫 번째 시트를 만들 때 '로컬 접속'이라는 명찰(Profile)을 달아서 넘겨줍니다.
            var defaultProfile = new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
            Sheets.Add(new SheetViewModel("Sheet 1", defaultProfile, _defaultDockerService, _dialogService));
            ActiveSheet = Sheets.First();
            // =================================================================

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

            SystemPruneCommand = new AsyncRelayCommand(ExecuteSystemPruneAsync);

            if (ActiveSheet != null) AttachSheetEvents();

            ExportComposeCommand = new RelayCommand(_ =>
            {
                if (ActiveSheet != null)
                {
                    ComposeExportService.ExportToCompose(ActiveSheet, _dialogService);
                }
            });

            ImportComposeCommand = new AsyncRelayCommand(async o =>
            {
                // 3. await로 도커 작업이 끝날 때까지 우아하게 대기
                await ComposeImportService.ImportFromCompose(
                    this,
                    _containerService,
                    _volumeService,
                    _networkService,
                    _dialogService
                );
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

        /// <summary>
        /// 캔버스에 새로운 요소가 추가되거나 변경되었을 때 호출되어, 아직 파일로 저장되지 않은 변경사항이 있음을 표시(Dirty)합니다.
        /// </summary>
        public void MarkAsModified()
        {
            IsModified = true;
        }

        /// <summary>
        /// 애플리케이션 시작 시, 사용자가 마지막으로 작업했던 다이어그램 파일 경로가 설정에 남아있다면
        /// 이를 감지하여 백그라운드에서 조용히 화면에 복원(Auto-Load)해주는 기능입니다.
        /// </summary>
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

        /// <summary>
        /// 사이드바의 '템플릿(Templates)' 목록을 갱신합니다.
        /// 항상 고정적으로 제공되는 기본 템플릿(Nginx, Redis, Ubuntu)에 더하여,
        /// 사용자가 자주 생성했던 이미지 상위 3개를 분석하여 '자주 사용하는 템플릿'으로 자동 추가합니다.
        /// </summary>
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

        /// <summary>
        /// 1초마다 주기적으로 실행되어 도커 엔진의 최신 상태를 폴링(Polling)하는 자동 동기화 타이머 이벤트입니다.
        /// 이전 동기화 작업이 끝나지 않았다면 중복 실행을 방지(_isSyncing)하여 앱의 멈춤이나 성능 저하를 막습니다.
        /// </summary>
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

        /// <summary>
        /// 사이드바 목록에서 사용자가 특정 컨테이너의 삭제(휴지통) 버튼을 눌렀을 때 호출됩니다.
        /// 확인 팝업을 거친 뒤, 실제 도커 엔진에서 해당 컨테이너를 영구적으로 삭제하고 UI를 갱신합니다.
        /// </summary>
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

        /// <summary>
        /// 사이드바 목록에서 사용자가 특정 볼륨의 삭제(휴지통) 버튼을 눌렀을 때 호출됩니다.
        /// 실제 도커 엔진에서 해당 볼륨을 영구적으로 삭제합니다.
        /// </summary>
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

        /// <summary>
        /// 사이드바 목록에서 사용자가 특정 네트워크의 삭제(휴지통) 버튼을 눌렀을 때 호출됩니다.
        /// 실제 도커 엔진에서 해당 가상 네트워크를 영구적으로 삭제합니다.
        /// </summary>
        private async Task DeleteNetworkItemAsync(object? param)
        {
            if (param is DockerNetworkGroup n)
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

        /// <summary>
        /// 다이어그램에 특정 이미지를 기반으로 컨테이너를 생성할 때마다 호출되어 사용 빈도를 기록하는 헬퍼 메서드입니다.
        /// 이 통계는 사이드바의 '자주 사용하는 템플릿' 목록을 동적으로 구성하는 데 사용됩니다.
        /// </summary>
        // 통계만 기록하는 헬퍼 함수
        private void RegisterTemplateUsage(string imageName)
        {
            if (string.IsNullOrEmpty(imageName)) return;
            if (!_usageStats.ContainsKey(imageName)) _usageStats[imageName] = 0;
            _usageStats[imageName]++;
            RefreshTemplates();
        }

        // --- 6. Docker 연동 로직 ---

        // 도커 엔진으로부터 가져온 필터링되지 않은 순수(Raw) 원본 데이터 캐시입니다.
        private List<DockerContainer> _rawContainers = new();
        private List<DockerVolume> _rawVolumes = new();
        private List<DockerNetworkGroup> _rawNetworks = new();
        private List<DockerImage> _rawImages = new();

        /// <summary>
        /// 도커 엔진(로컬 또는 원격)과 통신하여 현재 존재하는 모든 리소스(컨테이너, 볼륨, 네트워크, 이미지)의 최신 상태를 가져옵니다.
        /// 연결이 유효한지 확인한 후 원본 데이터를 갱신하고, UI 목록 필터링(UpdateAvailableItems)을 호출합니다.
        /// </summary>
        private async Task SyncWithDockerEngine()
        {
            // =================================================================
            // ★ [수정] 현재 시트가 "Local"일 때만 윈도우 프로세스를 검사하도록 조건 추가!
            // =================================================================
            if (ActiveSheet?.Profile.Type == EndpointType.Local)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!DockerServiceHelper.IsDockerRunning())
                    {
                        LastSyncTime = "Docker stopped";
                        return;
                    }
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

        /// <summary>
        /// 도커 엔진에서 가져온 전체 리소스 원본 목록에서 '이미 다이어그램 캔버스 위에 꺼내져 있는 항목'들을 제외(필터링)합니다.
        /// 이를 통해 사이드바에는 '아직 도화지에 그리지 않은 남은 리소스'들만 깔끔하게 표시되며,
        /// 사용자가 입력한 검색어 필터링 처리 및 UI 컬렉션의 스마트 동기화(화면 깜빡임 방지)를 함께 수행합니다.
        /// </summary>
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

        /// <summary>
        /// 사이드바의 '로컬 이미지' 목록에서 특정 이미지를 삭제할 때 호출됩니다.
        /// 사용 중인 이미지라 일반 삭제가 실패할 경우, 사용자에게 확인을 받아 강제 삭제(Force Remove)를 시도하는 안전장치가 포함되어 있습니다.
        /// </summary>
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

        /// <summary>
        /// 사용자가 사이드바 아코디언에서 항목을 캔버스로 드래그 앤 드롭했을 때 실행되는 최상위 라우팅 메서드입니다.
        /// 넘어온 객체의 타입(컨테이너, 볼륨, 네트워크 등)을 판별하여 적절한 도화지 배치(시각적 렌더링) 로직을 수행하며,
        /// 이미 존재하는 컨테이너일 경우 도커 엔진을 찔러 기존에 연결된 네트워크와 볼륨 선(Connector)을 자동으로 복구(Auto-wiring)해 줍니다.
        /// </summary>
        // ★ [중요] 드래그 앤 드롭 핸들러 수정 (Object 타입 수신 -> 타입별 분기)
        public async Task CreateNodeAtAsync(object item, double x, double y)
        {
            if (ActiveSheet == null) return;

            // [CASE 1] 컨테이너 (DockerContainer)
            if (item is DockerContainer container)
            {
                // 1. 메인 노드 생성 (★ 수정: 통합 메서드 CreateNodeAt 사용!)
                ActiveSheet.CreateNodeAt(container, x, y);
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
                                    // 그룹 생성 시 GroupType.Network를 마지막 인자로 넘김
                                    existingGroup = new GroupViewModel(x - 30, y - 40, 220, 150, _networkService, _dialogService, netName, GroupType.Network);
                                    ActiveSheet.AddGroup(existingGroup);
                                }

                                // 방금 만든 노드를 그룹에 추가 (자동으로 위치 조정 및 포함 처리)
                                var newNode = ActiveSheet.Nodes.Last();
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
                                        // 볼륨 노드 생성 (★ 수정: 통합 메서드 CreateNodeAt 사용!)
                                        var volModel = new DockerVolume { Name = volName };
                                        ActiveSheet.CreateNodeAt(volModel, x + 250, y + (volIndex * 120));
                                        targetVolNode = ActiveSheet.Nodes.Last();
                                    }

                                    // 선 연결
                                    var newNode = ActiveSheet.Nodes.LastOrDefault(n => n.ContainerId == container.Id);
                                    if (newNode != null)
                                    {
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
                ActiveSheet.CreateNodeAt(volume, x, y);
                IsModified = true;
            }
            // [CASE 3] 인터넷 (DockerInternet)
            else if (item is DockerInternet internet)
            {
                ActiveSheet.CreateNodeAt(internet, x, y);
                IsModified = true;
            }
            // [CASE 4] 네트워크 그룹 (DockerGroup)
            else if (item is DockerNetworkGroup network)
            {
                var groupVm = new GroupViewModel(x, y, 220, 150, _networkService, _dialogService, network.Name, GroupType.Network);
                ActiveSheet.AddGroup(groupVm);
                IsModified = true;
            }
        }

        /// <summary>
        /// UI(ContainerDialog)를 통해 입력받은 설정값들을 바탕으로 실제 도커 컨테이너를 생성하고 시작합니다.
        /// [사전 검증 -> 임시 노드 생성 -> 이미지 다운로드(Pull) -> 컨테이너 Run -> 네트워크/볼륨 자동 연결 -> UI 갱신]
        /// 이라는 복잡한 라이프사이클을 하나의 트랜잭션처럼 매끄럽게 처리하는 핵심 메서드입니다.
        /// </summary>
        public async Task CreateNewContainerNodeAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes, string restartPolicy, long memoryMb, double cpuCount, double x, double y, string networkName = "bridge", string command = "", bool tty = false)
        {
            if (ActiveSheet == null) return;

            // =================================================================
            // ★ [사전 검증 1] 컨테이너 이름 중복 검사 (즉시 차단)
            // =================================================================
            bool isNameUsed = ActiveSheet.Nodes.Any(n => n.Type == NodeType.Container && n.Name == name);
            if (isNameUsed)
            {
                _dialogService.ShowMessage($"'{name}'(은)는 이미 다이어그램에 존재하는 컨테이너 이름입니다.\n다른 이름을 사용해 주세요.");
                return;
            }

            // =================================================================
            // ★ [사전 검증 2] 호스트 포트 충돌 검사 (즉시 차단)
            // =================================================================
            if (ports != null && ports.Count > 0)
            {
                var newHostPorts = ports.Select(p => p.Split(':')[0]).ToList();
                var existingContainers = ActiveSheet.Nodes.Where(n => n.Type == NodeType.Container && n.PortBindings != null);

                foreach (var existingNode in existingContainers)
                {
                    var existingHostPorts = existingNode.PortBindings.Select(p => p.Split(':')[0]);
                    var conflictedPort = newHostPorts.FirstOrDefault(p => existingHostPorts.Contains(p));
                    if (conflictedPort != null)
                    {
                        _dialogService.ShowMessage($"⚠️ 포트 충돌 경고!\n\n호스트 포트 '{conflictedPort}'는 이미 '{existingNode.Name}' 컨테이너가 사용 중입니다.\n충돌을 방지하기 위해 작업을 취소합니다.");
                        return;
                    }
                }
            }
            // =================================================================

            // 로컬 폴더 마운트가 아닌 도커 Named 볼륨들만 따로 추려내어 화면에 그릴 준비를 합니다.
            var namedVolumesToDraw = new List<string>();
            foreach (var vol in volumes)
            {
                bool isBindMount = System.Text.RegularExpressions.Regex.IsMatch(vol, @"^([a-zA-Z]:[\\/]|/|\.|~)");
                if (!isBindMount) namedVolumesToDraw.Add(vol);
            }

            // 이미지 이름에 태그가 포함되어 있다면 분리합니다. (예: ubuntu:20.04 -> ubuntu / 20.04)
            if (image.Contains(":"))
            {
                int lastColon = image.LastIndexOf(':');
                tag = image.Substring(lastColon + 1);
                image = image.Substring(0, lastColon);
            }

            GroupViewModel? targetGroup = null;

            // 특정 네트워크를 지정했다면 해당 네트워크 그룹 안으로 쏙 들어가도록 좌표를 조정합니다.
            if (!string.IsNullOrWhiteSpace(networkName) && networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                targetGroup = ActiveSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == networkName);

                if (targetGroup == null)
                {
                    targetGroup = new GroupViewModel(x, y, 220, 150, _networkService, _dialogService, networkName, GroupType.Network);
                    ActiveSheet.AddGroup(targetGroup);

                    try { await _networkService.CreateNetworkAsync(networkName, "bridge"); }
                    catch (Exception ex)
                    {
                        if (!ex.Message.Contains("already exists") && !ex.Message.Contains("409"))
                        {
                            Debug.WriteLine($"[DockerDiscovery] 네트워크 '{networkName}' 자동 생성 실패: {ex.Message}");
                        }
                    }
                }

                x = targetGroup.X + 20;
                y = targetGroup.Y + 40 + (targetGroup.ContainedNodes.Count * 100);

                if (y + 80 > targetGroup.Y + targetGroup.Height)
                {
                    targetGroup.Height = (y - targetGroup.Y) + 100;
                }
            }

            // 다운로드 대기 중임을 보여주는 노란색 임시 노드 생성
            var node = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = $"{name} (Creating...)",
                ImageName = $"{image}:{tag}",
                Type = NodeType.Container,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107"
            };
            ActiveSheet.Nodes.Add(node);

            try
            {
                // 이미지 다운로드 시도 (없으면 에러 후 생성 취소)
                try { await _imageService.PullImageAsync(image, tag); }
                catch (Exception pullEx)
                {
                    Debug.WriteLine($"[Image Pull] 원격 이미지 다운로드 실패: {pullEx.Message}");
                    var localImages = await _imageService.GetImagesAsync();
                    bool existsLocally = localImages.Any(img => img.Repository == image && (img.Tag == tag || tag == "latest"));
                    if (!existsLocally)
                    {
                        _dialogService.ShowInfo($"이미지 '{image}:{tag}'를 다운로드할 수 없으며 로컬에도 없습니다.\n생성을 취소합니다.\n\n{pullEx.Message}", "이미지 없음");
                        ActiveSheet.Nodes.Remove(node);
                        return;
                    }
                }

                // 백엔드 서비스를 통해 실제 컨테이너 생성 및 구동
                string containerId = await _containerService.CreateAndStartContainerAsync(
                    name, image, tag, ports, envs, volumes, restartPolicy, memoryMb, cpuCount, command, tty);

                // 생성이 완료되면 노드 정보를 '실제 데이터'로 덮어씌우고 녹색으로 변경
                node.Name = name;
                node.ContainerId = containerId;
                node.PortInfo = string.Join(", ", ports);
                node.PortBindings = ports;
                node.EnvironmentVariables = envs;
                node.RestartPolicy = restartPolicy;
                node.IsCreating = false;
                node.StatusColor = "#28a745";

                if (targetGroup != null)
                {
                    targetGroup.AddNode(node);
                    ActiveSheet.UpdateGroupLayering();
                }

                RegisterTemplateUsage($"{image}:{tag}");

                // 생성된 컨테이너가 의존하는 볼륨 노드들을 도화지에 추가하고 선을 긋습니다.
                int volIndex = 0;
                foreach (var volStr in namedVolumesToDraw)
                {
                    string volName = volStr;
                    string mountPath = "/data";

                    int lastColon = volStr.LastIndexOf(':');
                    if (lastColon > 0)
                    {
                        volName = volStr.Substring(0, lastColon);
                        mountPath = volStr.Substring(lastColon + 1);
                    }

                    var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n => n.Type == NodeType.Volume && n.Name == volName);
                    NodeViewModel targetVolNode;

                    if (existingVolNode != null) targetVolNode = existingVolNode;
                    else
                    {
                        targetVolNode = new NodeViewModel(_containerService, _volumeService, _dialogService)
                        {
                            Name = volName,
                            Type = NodeType.Volume,
                            ImageName = "local",
                            X = x + 250,
                            Y = y + (volIndex * 100),
                            StatusColor = "#E67E22"
                        };
                        ActiveSheet.Nodes.Add(targetVolNode);
                    }

                    bool connExists = ActiveSheet.Connectors.Any(c =>
                        (c.Source == node && c.Target == targetVolNode) || (c.Source == targetVolNode && c.Target == node));

                    if (!connExists)
                    {
                        var conn = new ConnectorViewModel(node, targetVolNode, PortDirection.Right, PortDirection.Left, _dialogService)
                        {
                            RelationType = RelationType.VolumeMount,
                            MountPath = mountPath
                        };
                        ActiveSheet.Connectors.Add(conn);
                    }
                    volIndex++;
                }
                UpdateAvailableItems();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        /// <summary>
        /// 다이어그램 캔버스에 새로운 볼륨(Volume) 노드를 배치하고, 백그라운드 도커 엔진에 실제 볼륨 생성을 요청합니다.
        /// 생성 중에는 임시로 노란색 상태를 유지하다가, 생성이 완료되면 주황색(볼륨 고유 색상)으로 상태를 갱신합니다.
        /// </summary>
        public async Task CreateNewVolumeNodeAsync(string name, string driver, double x, double y)
        {
            if (ActiveSheet == null) return;

            // Placeholder (생성 대기 중인 임시 노드)
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

                // 완료 후 실제 데이터로 갱신
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

        // =========================================================
        // 파일 입출력 (Save / Load) 관련 커맨드 로직
        // =========================================================

        /// <summary>
        /// 현재 작업 중인 다이어그램을 파일로 저장합니다. 
        /// 이미 저장된 경로가 있다면 덮어쓰기(Quick Save)를 수행하고, 없다면 '다른 이름으로 저장' 창을 띄웁니다.
        /// </summary>
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

        /// <summary>
        /// 시스템 대화상자를 띄워 사용자가 지정한 새로운 경로에 다이어그램을 저장합니다.
        /// </summary>
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

        /// <summary>
        /// 저장된 다이어그램 파일(.json 등)을 불러옵니다.
        /// 변경사항이 있을 경우 경고를 띄우며, 로드가 완료되면 도커 엔진과 즉시 동기화(RestoreLiveState)를 수행합니다.
        /// </summary>
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

        /// <summary>
        /// 파일을 막 불러왔을 때, 도화지 위의 노드들은 단순한 그림(죽은 데이터)에 불과합니다.
        /// 이 메서드는 전체 시트를 순회하며 실제 도커 엔진을 찔러 컨테이너/볼륨의 생존 여부(Live State)를 조회하고
        /// 노드의 상태 색상과 상세 정보를 실시간 데이터로 덮어씌워 '깨우는' 역할을 합니다.
        /// </summary>
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

        // =========================================================
        // 멀티 탭 (시트) 관리 로직
        // =========================================================

        /// <summary>
        /// 새로운 다이어그램 탭(시트)을 생성하고 활성화합니다.
        /// </summary>
        private void AddSheet()
        {
            var defaultProfile = new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
            var newSheet = new SheetViewModel($"Sheet {Sheets.Count + 1}", defaultProfile, _defaultDockerService, _dialogService);
            Sheets.Add(newSheet);
            ActiveSheet = newSheet;
        }

        /// <summary>
        /// 현재 선택된 다이어그램 탭(시트)을 삭제합니다. 마지막 시트는 삭제할 수 없습니다.
        /// </summary>
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

        // =========================================================
        // UI 선 긋기 (Connector) 및 시각적 연결 로직
        // =========================================================

        /// <summary>
        /// 사용자가 캔버스에서 두 항목(노드 또는 그룹)을 마우스로 드래그하여 선(Connector)을 연결했을 때 호출됩니다.
        /// 특히 '컨테이너'와 '볼륨'을 연결하는 경우, 단순한 시각적 선 긋기를 넘어 백그라운드에서 실제 도커 데이터 백업/재생성/마운트 복원 과정을 트리거합니다.
        /// </summary>
        public async void AddConnection(IConnectableItem source, IConnectableItem target, PortDirection sourceDir, PortDirection targetDir)
        {
            if (ActiveSheet == null || source == target) return;

            // 1. 유효성 검사 (볼륨과 관련된 불가능한 조합 차단)
            if (!IsValidConnection(source, target))
            {
                _dialogService.ShowMessage("연결할 수 없는 조합입니다.\n(볼륨끼리 연결하거나, 인터넷과 볼륨은 연결할 수 없습니다.)");
                return;
            }

            // 2. 방향 정규화 (항상 컨테이너가 Source가 되도록, 타겟이 컨테이너면 뒤집기)
            IConnectableItem finalSource = source;
            IConnectableItem finalTarget = target;
            PortDirection finalSourceDir = sourceDir;
            PortDirection finalTargetDir = targetDir;

            if (!(source is NodeViewModel sNode && sNode.Type == NodeType.Container) &&
                 (target is NodeViewModel tNode && tNode.Type == NodeType.Container))
            {
                finalSource = target;
                finalTarget = source;
                finalSourceDir = targetDir;
                finalTargetDir = sourceDir;
            }

            // =========================================================
            // [CASE 1] 볼륨 마운트 연결 처리 (반드시 NodeViewModel끼리만 성립)
            // =========================================================
            if (finalSource is NodeViewModel fsNode && fsNode.Type == NodeType.Container &&
                finalTarget is NodeViewModel ftNode && ftNode.Type == NodeType.Volume)
            {
                bool isSuccess = await ConnectVolumeToContainerAsync(fsNode, ftNode);
                if (!isSuccess) return; // 사용자가 취소하거나 실패하면 선 안 그음
            }

            // 3. 중복 연결 방지 및 실제 선(Connector) 추가
            bool exists = ActiveSheet.Connectors.Any(c =>
                (c.Source == finalSource && c.Target == finalTarget) ||
                (c.Source == finalTarget && c.Target == finalSource));

            if (!exists)
            {
                var newConnector = new ConnectorViewModel(finalSource, finalTarget, finalSourceDir, finalTargetDir, _dialogService);

                if (finalTarget is NodeViewModel targetNode && targetNode.Type == NodeType.Volume)
                {
                    newConnector.RelationType = RelationType.VolumeMount;
                    newConnector.MountPath = "/data";
                }
                else
                {
                    // 그룹(네트워크) ↔ 컨테이너, 그룹 ↔ 인터넷 등은 모두 Dependency로 처리하여 선 표시
                    newConnector.RelationType = RelationType.Dependency;
                }

                ActiveSheet.Connectors.Add(newConnector);
            }

            IsModified = true;
        }

        /// <summary>
        /// 두 요소 간의 선 연결이 논리적으로 허용되는 조합인지 검사합니다.
        /// (예: 볼륨끼리 연결 불가, 볼륨과 인터넷 간 연결 불가 등)
        /// </summary>
        // 네트워크 타입 검사 로직 삭제 -> 오직 컨테이너와 볼륨 관계만 정의
        private bool IsValidConnection(IConnectableItem t1, IConnectableItem t2)
        {
            bool isT1Volume = t1 is NodeViewModel n1 && n1.Type == NodeType.Volume;
            bool isT2Volume = t2 is NodeViewModel n2 && n2.Type == NodeType.Volume;
            bool isT1Internet = t1 is NodeViewModel i1 && i1.Type == NodeType.Internet;
            bool isT2Internet = t2 is NodeViewModel i2 && i2.Type == NodeType.Internet;

            // 연결 불가능: 볼륨 ↔ 볼륨
            if (isT1Volume && isT2Volume) return false;

            // 연결 불가능: 볼륨 ↔ 인터넷
            if ((isT1Internet && isT2Volume) || (isT1Volume && isT2Internet)) return false;

            // 나머지는 모두 허용 (그룹 ↔ 컨테이너 연결 포함!)
            return true;
        }

        /// <summary>
        /// 현재 선택된 노드나 연결선(Connector)의 선택 상태를 해제합니다.
        /// </summary>
        public void ClearSelection() => SelectedElement = null;

        /// <summary>
        /// 캔버스에서 현재 선택된 요소(선, 컨테이너, 볼륨, 네트워크 그룹 등)를 삭제합니다.
        /// 단순한 화면상의 지우기를 넘어, 요소의 종류에 따라 도커 엔진에서 실제 리소스를 삭제할지 묻는 대화상자를 띄우고
        /// 볼륨 마운트 물리적 해제, 네트워크 제거 등 연관된 백엔드 해체 작업을 통합적으로 수행합니다.
        /// </summary>
        public async Task DeleteSelectedAsync()
        {
            if (SelectedElement == null || ActiveSheet == null) return;

            // =========================================================
            // [CASE 1] 연결선(Connector) 삭제 시
            // =========================================================
            if (SelectedElement is ConnectorViewModel conn)
            {
                if (conn.RelationType == RelationType.Dependency)
                {
                    ActiveSheet.Connectors.Remove(conn);
                }
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
                        // ★ [핵심] 볼륨 연결은 NodeViewModel 간의 관계이므로 안전하게 캐스팅하여 해제
                        if (conn.Source is NodeViewModel srcNode && conn.Target is NodeViewModel tgtNode)
                        {
                            bool success = await UnmountVolumeFromContainerAsync(srcNode, tgtNode);
                            if (success) ActiveSheet.Connectors.Remove(conn);
                        }
                        else
                        {
                            ActiveSheet.Connectors.Remove(conn);
                        }
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
                if (node.Type == NodeType.Internet)
                {
                    var related = ActiveSheet.Connectors
                        .Where(c => c.Source == (IConnectableItem)node || c.Target == (IConnectableItem)node).ToList();
                    foreach (var c in related) ActiveSheet.Connectors.Remove(c);

                    ActiveSheet.Nodes.Remove(node);
                    IsModified = true;
                    SelectedElement = null;
                    return;
                }

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
                    // 볼륨 노드 삭제 처리도 함께 할 수 있도록 조건 수정
                    if (!string.IsNullOrEmpty(node.ContainerId) || node.Type == NodeType.Volume)
                    {
                        try
                        {
                            if (node.Type == NodeType.Container)
                            {
                                await _containerService.RemoveContainerAsync(node.ContainerId);
                                _rawContainers.RemoveAll(c => c.Id == node.ContainerId);
                            }
                            else if (node.Type == NodeType.Volume)
                            {
                                await _volumeService.RemoveVolumeAsync(node.Name);
                                _rawVolumes.RemoveAll(v => v.Name == node.Name);
                            }
                        }
                        catch (Exception ex)
                        {
                            _dialogService.ShowMessage($"Docker 리소스 삭제 실패: {ex.Message}\n\n목록에서 제거되지 않습니다.");
                            return;
                        }
                    }
                }

                var relatedConnectors = ActiveSheet.Connectors
                    .Where(c => c.Source == (IConnectableItem)node || c.Target == (IConnectableItem)node).ToList();
                foreach (var c in relatedConnectors) ActiveSheet.Connectors.Remove(c);

                ActiveSheet.Nodes.Remove(node);
                IsModified = true;
            }

            // =========================================================
            // [CASE 3] 그룹(Group) 삭제 시
            // =========================================================
            else if (SelectedElement is GroupViewModel group)
            {
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

                if (group.ContainedNodes != null)
                {
                    foreach (var childNode in group.ContainedNodes.ToList())
                    {
                        childNode.X += group.X;
                        childNode.Y += group.Y;
                        ActiveSheet.Nodes.Add(childNode);
                    }
                }

                // 그룹에 연결된 선(Connector)들도 같이 삭제
                var relatedConnectors = ActiveSheet.Connectors
                    .Where(c => c.Source == (IConnectableItem)group || c.Target == (IConnectableItem)group).ToList();
                foreach (var c in relatedConnectors) ActiveSheet.Connectors.Remove(c);

                ActiveSheet.Groups.Remove(group);
                IsModified = true;
            }

            SelectedElement = null;
        }

        /// <summary>
        /// 다이어그램에서 사용자가 볼륨 연결선을 삭제할 때 호출되는 무결성 보장 트랜잭션 메서드입니다.
        /// 도커 엔진은 실행 중인 컨테이너에서 마운트된 볼륨만 '쏙' 빼내는 것을 허용하지 않으므로,
        /// 기존 데이터를 호스트 임시 폴더로 안전하게 백업한 뒤, 컨테이너를 삭제하고 기존 설정(포트, 환경변수, 명령 등)을 유지한 채 
        /// 볼륨만 제외하여 재생성하고, 마지막으로 백업된 데이터를 복원하는 정교한 작업을 수행합니다.
        /// </summary>
        private async Task<bool> UnmountVolumeFromContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode)
        {
            string containerId = containerNode.ContainerId;
            string volumeNameToRemove = volumeNode.Name;

            // 예기치 못한 에러 시 데이터 유실을 막기 위해 백업 폴더를 남겨둘지 결정하는 플래그
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

                // ★ 2-5. [버그 수정] 기존 명령어(Cmd) 및 TTY 설정 복구
                string command = oldConfig.Cmd != null ? string.Join(" ", oldConfig.Cmd) : "";
                bool tty = oldConfig.Tty;

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
                    0, 0,
                    command, // ★ 복원된 명령어 전달!
                    tty      // ★ 복원된 TTY 설정 전달!
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
                    }
                    catch (Exception delEx)
                    {
                        Debug.WriteLine($"[Cleanup] Delete exception: {delEx}");
                    }
                }
            }
        }

        /// <summary>
        /// 다이어그램에서 사용자가 컨테이너와 볼륨을 선으로 연결했을 때 호출되는 무결성 보장 마운트 메서드입니다.
        /// 도커는 실행 중인 컨테이너에 중간부터 볼륨을 끼워넣는 것을 허용하지 않으므로, 
        /// 기존 데이터를 안전하게 호스트 임시 폴더로 백업한 뒤, 컨테이너를 삭제하고 볼륨 마운트 정보가 추가된 상태로 재생성합니다.
        /// 생성 후 백업 데이터를 다시 밀어넣어(Restore) 데이터 유실 없이 물리적 볼륨 연결을 완성합니다.
        /// </summary>
        public async Task<bool> ConnectVolumeToContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode)
        {
            // 1. 다이얼로그로 경로와 소유자 정보 입력받기
            var dlg = new Views.MountDialog(_dialogService);
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

                try
                {
                    await _containerService.CopyFromContainerAsync(containerId, mountPath, tempHostPath);
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("No such") || ex.Message.Contains("NotFound") || ex.Message.Contains("404"))
                    {
                        Debug.WriteLine($"[Backup Skip] '{mountPath}' 경로가 컨테이너에 아직 존재하지 않아 백업을 생략합니다.");
                    }
                    else
                    {
                        bool proceed = _dialogService.ShowConfirm(
                            $"기존 데이터 백업 중 예상치 못한 오류가 발생했습니다.\n" +
                            $"이대로 진행하면 컨테이너 내부의 기존 데이터가 유실될 위험이 있습니다.\n\n" +
                            $"[오류 내용]\n{ex.Message}\n\n" +
                            $"위험을 감수하고 데이터 없이 마운트를 강행하시겠습니까?",
                            "⚠️ 데이터 백업 실패 경고"
                        );

                        if (!proceed)
                        {
                            if (System.IO.Directory.Exists(tempHostPath)) System.IO.Directory.Delete(tempHostPath, true);
                            return false;
                        }
                    }
                }

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
                if (oldHostConfig.Binds != null) volumes.AddRange(oldHostConfig.Binds);
                volumes.Add($"{volumeName}:{mountPath}");

                // ★ 2-5. [버그 수정] 기존 명령어(Cmd) 및 TTY 설정 복구
                string command = oldConfig.Cmd != null ? string.Join(" ", oldConfig.Cmd) : "";
                bool tty = oldConfig.Tty;

                // ---------------------------------------------------------
                // [STEP 3] 재생성
                // ---------------------------------------------------------
                await _containerService.RemoveContainerAsync(containerId);

                string newId = await _containerService.CreateAndStartContainerAsync(
                    containerNode.Name, imgRepo, imgTag, ports, envs, volumes,
                    oldHostConfig.RestartPolicy.Name.ToString(), 0, 0,
                    command, // ★ 복원된 명령어 전달!
                    tty      // ★ 복원된 TTY 설정 전달!
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

        /// <summary>
        /// 사이드바 UI 목록(ObservableCollection)과 도커 엔진에서 가져온 최신 데이터(List)를 비교하여 스마트하게 동기화합니다.
        /// 리스트 전체를 지우고 새로 할당하면 화면이 심하게 깜빡거리게 되므로(Flickering), 
        /// 실제 변경된 항목(추가/삭제)만 선별하여 반영하고, 이미 존재하는 컨테이너는 상태값만 조용히 업데이트합니다.
        /// </summary>
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

            // 3. 상태 업데이트: 만약 ID는 같은데 상태가 변했다면 여기서 속성만 복사해줄 수 있음
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

        /// <summary>
        /// 현재 활성화된 시트(ActiveSheet) 내부의 컬렉션(노드, 선, 그룹)에 변경 감지 이벤트를 부착합니다.
        /// 다이어그램에 항목이 추가되거나 속성이 변경될 때마다 IsModified 플래그를 true로 만들어 '저장 필요' 상태를 추적합니다.
        /// </summary>
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

        /// <summary>
        /// 시트에 노드가 새롭게 추가되거나 삭제될 때 호출되어, 개별 노드의 시각적 속성 변경(OnModified) 이벤트 구독을 관리합니다.
        /// 사이드바의 사용 가능 목록을 갱신하고 수정 상태(Dirty)를 마킹합니다.
        /// </summary>
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

        /// <summary>
        /// 개별 노드의 위치(X,Y)나 크기, 상태 등이 수정되었을 때 호출되어 앱 전체를 '수정됨(저장 필요)' 상태로 만듭니다.
        /// </summary>
        private void Node_OnModified(object? sender, EventArgs e)
        {
            MarkAsModified();
        }

        /// <summary>
        /// 시트에 연결선(Connector)이 새롭게 추가되거나 삭제될 때 호출되어 이벤트 구독을 관리하고 수정 상태를 마킹합니다.
        /// </summary>
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

        /// <summary>
        /// 선의 위치나 연결 정보가 변경되었을 때 호출되어 앱 전체를 '수정됨' 상태로 만듭니다.
        /// </summary>
        private void Connector_OnModified(object? sender, EventArgs e)
        {
            MarkAsModified();
        }

        /// <summary>
        /// 시트에 그룹(Group)이 새롭게 추가되거나 삭제될 때 호출되어, 그룹의 속성 변경 이벤트 구독을 관리합니다.
        /// </summary>
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

        /// <summary>
        /// 백그라운드에서 도커 데몬이 켜졌다는 신호를 받았을 때 실행됩니다.
        /// 즉시 엔진의 데이터를 동기화하고, 회색으로 죽어있던 다이어그램 노드들을 녹색(실행 중)으로 깨웁니다.
        /// </summary>
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

        /// <summary>
        /// 현재 활성화된 시트(도화지)의 모든 요소(노드, 선, 그룹)를 깨끗하게 지웁니다.
        /// </summary>
        private void ExecuteFlowClear(object? obj)
        {
            if (ActiveSheet != null && _dialogService.ShowConfirm("현재 시트의 모든 내용을 지우시겠습니까?", "Flow Clear"))
            {
                ActiveSheet.Nodes.Clear();
                ActiveSheet.Connectors.Clear();
                ActiveSheet.Groups.Clear();
            }
        }

        /// <summary>
        /// 열려있는 모든 시트의 내용을 일괄적으로 초기화합니다.
        /// </summary>
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

        /// <summary>
        /// 활성화된 시트를 포함하여 전체 시트를 삭제하고, 빈 시트 1개만 남깁니다.
        /// </summary>
        private void ExecuteDeleteAllSheet(object? obj)
        {
            if (_dialogService.ShowConfirm("모든 시트를 삭제하시겠습니까?", "Delete All Sheet"))
            {
                Sheets.Clear();
                if (AddSheetCommand.CanExecute(null)) AddSheetCommand.Execute(null);
            }
        }

        /// <summary>
        /// 캔버스에 새로운 가상 네트워크 그룹을 생성하고, 실제 도커 엔진에도 네트워크를 만듭니다.
        /// </summary>
        public async Task CreateNewNetworkGroupAsync(string name, string driver, double x, double y, double w, double h)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                string networkId = await _networkService.CreateNetworkAsync(name, driver);

                var newNetworkGroup = new GroupViewModel(x, y, w, h, _networkService, _dialogService, name, GroupType.Network)
                {
                    Id = networkId,            // 도커 ID 저장
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

        /// <summary>
        /// 사용자가 입력한 `docker run` 형태의 CLI 명령어를 분석하여 캔버스에 임시 노드를 배치하고, 
        /// 백그라운드 프로세스(cmd.exe)를 통해 실제 명령을 실행한 뒤, 완료되면 도커 정보를 가져와 UI를 완벽하게 갱신합니다.
        /// </summary>
        public async Task ProcessCliCommandAsync(string cliCommand, double x, double y)
        {
            if (ActiveSheet == null) return;

            // =================================================================
            // [STEP 1] 정규식으로 '도화지에 그릴 최소한의 정보'만 수집
            // =================================================================
            var regex = new System.Text.RegularExpressions.Regex(@"[\""].+?[\""]|['].+?[']|[^ ]+");
            var tokens = regex.Matches(cliCommand).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value.Trim('\"', '\'')).ToList();

            string name = $"cli-{Guid.NewGuid().ToString().Substring(0, 4)}"; // 지정 안 하면 랜덤 이름
            string image = "unknown";
            string networkName = "bridge";

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == "--name" && i + 1 < tokens.Count) name = tokens[i + 1];
                if ((tokens[i] == "--network" || tokens[i] == "--net") && i + 1 < tokens.Count) networkName = tokens[i + 1];
                if (!tokens[i].StartsWith("-") && tokens[i] != "docker" && tokens[i] != "run" && image == "unknown") image = tokens[i];
            }

            // =================================================================
            // ★ [STEP 1.5] 네트워크 존재 여부 사전 검사 (안전장치)
            // =================================================================
            if (networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                var existingNetworks = await _networkService.GetNetworksAsync();

                // 도커 엔진에 해당 네트워크가 진짜로 있는지 확인!
                if (!existingNetworks.Any(n => n.Name == networkName))
                {
                    // 없다면? CMD로 던지기 전에 에러 팝업 띄우고 즉시 컷트!
                    _dialogService.ShowInfo($"명령어 실행 실패!\n\n도커 엔진에 '{networkName}' 네트워크가 존재하지 않습니다.\n먼저 해당 네트워크를 생성한 후 다시 시도해 주세요.", "네트워크 없음");
                    return;
                }
            }
            // =================================================================

            // [STEP 2] 모아둔 정보로만 '임시 노드'를 도화지에 먼저 그림 (그룹 자동 입주)
            GroupViewModel? targetGroup = null;
            if (!string.IsNullOrWhiteSpace(networkName) && networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                targetGroup = ActiveSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == networkName);
                if (targetGroup == null)
                {
                    targetGroup = new GroupViewModel(x, y, 220, 150, _networkService, _dialogService, networkName, GroupType.Network);
                    ActiveSheet.AddGroup(targetGroup);
                }
                x = targetGroup.X + 20;
                y = targetGroup.Y + 40 + (targetGroup.ContainedNodes.Count * 100);
            }

            var dummyNode = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = $"{name} (Creating...)",
                ImageName = image,
                Type = NodeType.Container,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107" // 🟡 생성 중 (노란색)
            };
            ActiveSheet.Nodes.Add(dummyNode);
            if (targetGroup != null) targetGroup.AddNode(dummyNode);

            // [STEP 3] 명령어 통째로 CMD로 넘겨서 실행 (모든 옵션 100% 적용됨)
            try
            {
                await Task.Run(() =>
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {cliCommand}",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var process = Process.Start(startInfo))
                    {
                        process.WaitForExit(); // 도커가 컨테이너를 다 만들 때까지 대기
                    }
                });

                // [STEP 4] 도커에 계속 질의해서 완성된 '진짜 정보'로 노드 갱신!
                var allContainers = await _containerService.GetContainersAsync();

                // CMD로 만들어진 컨테이너를 이름으로 찾아냅니다. (도커는 이름 앞에 '/'가 붙기도 함)
                var realContainer = allContainers.FirstOrDefault(c => c.Name == name || c.Name == $"/{name}");

                if (realContainer != null)
                {
                    dummyNode.ContainerId = realContainer.Id;
                    dummyNode.Name = name;
                    dummyNode.IsCreating = false;
                    dummyNode.StatusColor = "#28a745"; // 🟢 성공 (녹색)

                    await dummyNode.RefreshDetailsAsync();

                    try
                    {
                        var inspectData = await _containerService.InspectContainerAsync(realContainer.Id);
                        if (inspectData?.Mounts != null)
                        {
                            int volIndex = 0;
                            foreach (var mount in inspectData.Mounts)
                            {
                                // bind 마운트(로컬 폴더)가 아닌 도커 볼륨(named volume)만 도화지에 그립니다.
                                if (mount.Type == "volume")
                                {
                                    string volName = mount.Name;
                                    string mountPath = mount.Destination;

                                    var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n => n.Type == NodeType.Volume && n.Name == volName);
                                    NodeViewModel targetVolNode;

                                    if (existingVolNode != null) targetVolNode = existingVolNode;
                                    else
                                    {
                                        // 볼륨 노드 생성
                                        targetVolNode = new NodeViewModel(_containerService, _volumeService, _dialogService)
                                        {
                                            Name = volName,
                                            Type = NodeType.Volume,
                                            ImageName = "local",
                                            X = dummyNode.X + 250,
                                            Y = dummyNode.Y + (volIndex * 100),
                                            StatusColor = "#E67E22"
                                        };
                                        ActiveSheet.Nodes.Add(targetVolNode);
                                    }

                                    // 컨테이너와 볼륨 선 긋기
                                    bool connExists = ActiveSheet.Connectors.Any(c =>
                                        (c.Source == dummyNode && c.Target == targetVolNode) || (c.Source == targetVolNode && c.Target == dummyNode));

                                    if (!connExists)
                                    {
                                        var conn = new ConnectorViewModel(dummyNode, targetVolNode, PortDirection.Right, PortDirection.Left, _dialogService)
                                        {
                                            RelationType = RelationType.VolumeMount,
                                            MountPath = mountPath
                                        };
                                        ActiveSheet.Connectors.Add(conn);
                                    }
                                    volIndex++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Inspect가 실패하더라도 컨테이너 자체는 생성된 상태이므로 노드를 지우지는 않습니다.
                        // 다만 볼륨 연결선 등 상세 토폴로지를 그릴 수 없음을 사용자에게 명확히 알립니다.
                        Debug.WriteLine($"[DockerDiscovery] Inspect 실패: {ex.Message}");

                        _dialogService.ShowInfo(
                            $"컨테이너 '{name}'(은)는 성공적으로 생성되었으나, 볼륨 마운트 등의 상세 정보를 불러오는데 실패했습니다.\n" +
                            $"컨테이너가 실행 직후 즉시 종료(Exit)되었거나 API 응답이 지연되었을 수 있습니다.\n\n" +
                            $"[상세 오류]\n{ex.Message}",
                            "⚠️ 상세 정보 동기화 경고"
                        );
                    }

                    UpdateAvailableItems();
                }
                else
                {
                    // 도커에서 못 찾았다면 명령어가 실패한 것 (오타 등)
                    _dialogService.ShowInfo($"명령어 실행 실패.\n도커가 컨테이너를 생성하지 못했습니다. 명령어를 다시 확인해 주세요.", "실패");
                    ActiveSheet.Nodes.Remove(dummyNode);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"CMD 실행 중 오류 발생: {ex.Message}");
                ActiveSheet.Nodes.Remove(dummyNode);
            }
        }

        /// <summary>
        /// 사용자가 작성한 Dockerfile을 바탕으로 백그라운드에서 빌드를 수행하고, 성공 시 새 컨테이너 노드를 캔버스에 생성합니다.
        /// </summary>
        public async Task BuildImageAndCreateNodeAsync(string targetImageName, string dockerfileContent, string uploadedFilePath, double x, double y)
        {
            if (ActiveSheet == null) return;
            if (string.IsNullOrWhiteSpace(targetImageName)) targetImageName = $"custom-app:{Guid.NewGuid().ToString().Substring(0, 4)}";

            string buildContextPath = "";
            string dockerfilePath = "";

            // 1. 업로드한 파일인지 vs 직접 입력한 텍스트인지 판별
            if (!string.IsNullOrEmpty(uploadedFilePath) && System.IO.File.Exists(uploadedFilePath))
            {
                // 파일을 업로드했다면 그 파일이 있는 폴더 전체를 빌드 컨텍스트로 사용
                dockerfilePath = uploadedFilePath;
                buildContextPath = Path.GetDirectoryName(uploadedFilePath);
            }
            else
            {
                // 직접 입력했다면 임시 폴더를 하나 만들어서 Dockerfile이라는 이름으로 저장해줌
                buildContextPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DockerDiagramBuild_" + Guid.NewGuid().ToString().Substring(0, 8));
                System.IO.Directory.CreateDirectory(buildContextPath);
                dockerfilePath = System.IO.Path.Combine(buildContextPath, "Dockerfile");
                await System.IO.File.WriteAllTextAsync(dockerfilePath, dockerfileContent);
            }

            // 2. 캔버스에 "빌드 중..." 이라는 파란색 임시 노드 생성
            var dummyNode = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = $"Building ({targetImageName})...",
                ImageName = "Building...",
                Type = NodeType.Container,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#17a2b8" // 정보(빌드) 색상
            };
            ActiveSheet.Nodes.Add(dummyNode);

            // 3. 백그라운드에서 CMD로 docker build 실행
            bool buildSuccess = false;
            try
            {
                await Task.Run(() =>
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c docker build -t {targetImageName} -f \"{dockerfilePath}\" \"{buildContextPath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var process = Process.Start(startInfo))
                    {
                        process.WaitForExit();
                        buildSuccess = process.ExitCode == 0; // 성공하면 ExitCode가 0
                    }
                });

                // 4. 빌드가 완료되면 임시 노드 지우고 진짜 컨테이너 노드 띄우기!
                ActiveSheet.Nodes.Remove(dummyNode);

                if (buildSuccess)
                {
                    // 방금 구워낸 따끈따끈한 이미지 이름으로 컨테이너 생성 로직 태우기
                    string containerName = targetImageName.Split(':')[0] + "-" + Guid.NewGuid().ToString().Substring(0, 4);

                    // 기존에 잘 만들어둔 메서드 재활용!
                    await CreateNewContainerNodeAsync(
                        containerName, targetImageName.Split(':')[0],
                        targetImageName.Contains(":") ? targetImageName.Split(':')[1] : "latest",
                        new List<string>(), new List<string>(), new List<string>(), "no", 0, 0, x, y);
                }
                else
                {
                    _dialogService.ShowMessage($"[{targetImageName}] 이미지 빌드에 실패했습니다. (도커파일 문법 확인)");
                }
            }
            catch (Exception ex)
            {
                ActiveSheet.Nodes.Remove(dummyNode);
                _dialogService.ShowMessage($"빌드 중 오류 발생: {ex.Message}");
            }
        }

        /// <summary>
        /// 도커 리소스 대청소(Prune) 명령을 실행하고 결과를 사용자에게 안내합니다.
        /// </summary>
        private async Task ExecuteSystemPruneAsync(object? obj)
        {
            // 1. 우리가 방금 만든 예쁜 팝업창 띄우기!
            var dlg = new Views.PruneDialog();
            dlg.Owner = Application.Current.MainWindow;

            // 사용자가 '취소'를 누르거나 X를 눌러 껐다면 즉시 중단
            if (dlg.ShowDialog() != true) return;

            // 2. 창에서 조립해준 도커 명령어 가져오기
            string targetCommand = dlg.FinalCommand;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                // 3. 백그라운드에서 선택한 명령어로 청소 실행!
                string pruneResult = "";
                await Task.Run(() =>
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {targetCommand}",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process != null)
                        {
                            pruneResult = process.StandardOutput.ReadToEnd();
                            process.WaitForExit();
                        }
                    }
                });

                // 4. 도커 엔진과 다시 동기화하여 UI 리스트 비우기
                await SyncWithDockerEngine();

                // 5. 결과 보고
                _dialogService.ShowInfo($"명령어 실행: {targetCommand}\n\n[결과]\n{pruneResult.Trim()}", "청소 완료");
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"청소 중 오류 발생: {ex.Message}");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}