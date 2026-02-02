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
        private readonly IDockerService _dockerService;
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
                if (_activeSheet != null) _activeSheet.Nodes.CollectionChanged -= Nodes_CollectionChanged;

                _activeSheet = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MapWidth));
                OnPropertyChanged(nameof(MapHeight));
                SelectedElement = null;

                if (_activeSheet != null)
                {
                    AttachSheetEvents();
                    UpdateAvailableItems();
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
        public ObservableCollection<DockerContainer> ExistingVolumes { get; } = new();
        public ObservableCollection<DockerContainer> ExistingNetworks { get; } = new();
        public ObservableCollection<DockerImage> LocalImages { get; } = new();

        private List<DockerContainer> _allContainers = new();
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

        // --- 생성자 ---
        // ★ [DI] 생성자 수정: IDockerService와 IDialogService 주입
        public MainViewModel(IDockerService dockerService, IDialogService dialogService)
        {
            _dockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            // 기본 시트 추가 (★ 시트 생성 시 서비스들 전달)
            Sheets.Add(new SheetViewModel("Sheet 1", _dockerService, _dialogService));
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

            SaveCommand = new RelayCommand(SaveAction);
            SaveAsCommand = new RelayCommand(SaveAsAction);
            LoadCommand = new AsyncRelayCommand(LoadActionAsync);

            if (ActiveSheet != null) AttachSheetEvents();

            ExportComposeCommand = new Helpers.RelayCommand(_ =>
            {
                if (ActiveSheet != null)
                {
                    Helpers.ComposeExportService.ExportToCompose(ActiveSheet);
                }
            });

            // 템플릿 초기화 (기본값)
            RefreshTemplates();

            // 자동 동기화 타이머 시작 15초 간격
            _autoSyncTimer = new DispatcherTimer();
            _autoSyncTimer.Interval = TimeSpan.FromSeconds(1);
            _autoSyncTimer.Tick += AutoSync_Tick;
            _autoSyncTimer.Start();

            // 앱 시작 시 1회 실행
            _ = SyncWithDockerEngine();
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
                // [변경] DialogService 사용
                if (_dialogService.ShowConfirm($"컨테이너 '{c.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        // [변경] _dockerService 사용
                        await _dockerService.RemoveContainerAsync(c.Id);
                        await SyncWithDockerEngine();
                    }
                    catch (Exception ex)
                    {
                        // [변경] DialogService 사용
                        _dialogService.ShowMessage($"삭제 실패: {ex.Message}");
                    }
                }
            }
        }

        private async Task DeleteVolumeItemAsync(object? param)
        {
            if (param is DockerContainer v)
            {
                // [변경] DialogService 사용
                if (_dialogService.ShowConfirm($"볼륨 '{v.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        // [변경] _dockerService 사용
                        await _dockerService.RemoveVolumeAsync(v.Name);
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
            if (param is DockerContainer n)
            {
                if (_dialogService.ShowConfirm($"네트워크 '{n.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        await _dockerService.RemoveNetworkAsync(n.Id);
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
        private List<DockerContainer> _rawVolumes = new();
        private List<DockerContainer> _rawNetworks = new();
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
                if (!await _dockerService.PingAsync()) return;

                // 1. 원본 데이터 가져오기
                _rawContainers = await _dockerService.GetContainersAsync();
                _rawVolumes = await _dockerService.GetVolumesAsync();
                _rawNetworks = await _dockerService.GetNetworksAsync();
                _rawImages = await _dockerService.GetImagesAsync();

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

        // 싱글톤 로직: 모든 시트를 검사하여 이미 배치된 컨테이너는 리스트에서 제외
        private void UpdateAvailableItems()
        {
            if (Sheets == null) return;

            // 1. 모든 시트의 노드를 하나로 합침
            var allNodes = Sheets.SelectMany(s => s.Nodes).ToList();

            // 2. 사용 중인 ID 및 이름 수집 (정확한 타입 확인)
            var usedContainerIds = new HashSet<string>(allNodes.Where(n => n.Type == NodeType.Container).Select(n => n.ContainerId));
            var usedVolumeNames = new HashSet<string>(allNodes.Where(n => n.Type == NodeType.Volume).Select(n => n.Name));
            var usedNetworkIds = new HashSet<string>(allNodes.Where(n => n.Type == NodeType.Network).Select(n => n.ContainerId));

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

            // 5. 네트워크 필터링 (ID 기준 + 기본 네트워크 숨김)
            var defaultNetworks = new HashSet<string> { "bridge", "host", "none" };
            var filteredNetworks = _rawNetworks
                .Where(n => !usedNetworkIds.Contains(n.Id)) // 시트에 있는 ID면 제외 (Canvas_Drop 수정 필수)
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
                        await _dockerService.DeleteImageAsync(img.Id, force: false);
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
                                await _dockerService.DeleteImageAsync(img.Id, force: true);
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

        // 비동기 컨테이너 생성 (모달 입력 처리용)
        public async Task CreateNewContainerNodeAsync(
    string name, string image, string tag,
    List<string> ports, List<string> envs, List<string> volumes, string restartPolicy,
    long memoryMb, double cpuCount,
    double x, double y)
        {
            if (ActiveSheet == null) return;

            // [필수 추가] 사용자가 "이미지:태그" 형식으로 입력했을 때 이를 강제로 분리하는 로직
            // 이 코드가 없으면 image="repo:tag", tag="latest"가 되어 404 에러가 발생합니다.
            if (image.Contains(":"))
            {
                int lastColon = image.LastIndexOf(':');
                // 예: image가 "owasp/modsecurity-crs:nginx"라면
                tag = image.Substring(lastColon + 1);   // tag를 "nginx"로 덮어씌움
                image = image.Substring(0, lastColon);  // image를 "owasp/modsecurity-crs"로 자름
            }

            // 1. Placeholder 노드 생성
            var node = new NodeViewModel(_dockerService, _dialogService)
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
                // 2. 이미지 다운로드 (Pull)
                // 위에서 tag가 "nginx"로 수정되었으므로, 정확한 버전을 다운로드합니다.
                await _dockerService.PullImageAsync(image, tag);

                // 3. 컨테이너 생성 및 실행
                string containerId = await _dockerService.CreateAndStartContainerAsync(
                    name, image, tag, ports, envs, volumes, restartPolicy, memoryMb, cpuCount);

                // 4. 완료 처리
                node.Name = name;
                node.ContainerId = containerId;
                node.PortInfo = string.Join(", ", ports);
                node.PortBindings = ports;
                node.EnvironmentVariables = envs;
                node.RestartPolicy = restartPolicy;

                node.IsCreating = false;
                node.StatusColor = "#28a745";

                RegisterTemplateUsage($"{image}:{tag}");
                UpdateAvailableItems();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        // 일반 노드 생성 (드래그 앤 드롭 등)
        public async Task CreateNodeAtAsync(DockerContainer container, double x, double y)
        {
            if (ActiveSheet == null) return;

            // 1. 메인 노드 생성 (컨테이너 또는 네트워크)
            var mainNode = new NodeViewModel(_dockerService, _dialogService)
            {
                Name = container.Name,
                ImageName = container.Image,
                StatusColor = container.StateColor,
                PortInfo = container.Ports,
                Type = container.Type,
                ContainerId = container.Id,
                X = x,
                Y = y,
                Width = 160,
                Height = 80
            };
            ActiveSheet.Nodes.Add(mainNode);
            RegisterTemplateUsage(container.Image); // 통계 기록
            IsModified = true;

            // =========================================================
            // CASE A: 컨테이너를 올렸을 때 -> (1) 네트워크 -> (2) 볼륨 순서로 [오른쪽] 배치
            // =========================================================
            if (container.Type == NodeType.Container && !string.IsNullOrEmpty(container.Id))
            {
                try
                {
                    var info = await _dockerService.InspectContainerAsync(container.Id);

                    // ★ 오른쪽에 배치할 아이템들의 수직 위치를 잡기 위한 공용 인덱스
                    int rightSideItemCount = 0;

                    // -----------------------------------------------------------
                    // (1) 네트워크 연결 (오른쪽 배치)
                    // -----------------------------------------------------------
                    if (info.NetworkSettings != null && info.NetworkSettings.Networks != null)
                    {
                        foreach (var netKvp in info.NetworkSettings.Networks)
                        {
                            string netName = netKvp.Key;

                            // 기본 bridge 제외
                            if (netName == "bridge") continue;

                            string netId = netKvp.Value.NetworkID;

                            // 이미 시트에 있는지 확인
                            var existingNet = ActiveSheet.Nodes.FirstOrDefault(n => n.Type == NodeType.Network && (n.Name == netName || n.ContainerId == netId));
                            NodeViewModel targetNetNode;

                            if (existingNet != null)
                            {
                                targetNetNode = existingNet;
                            }
                            else
                            {
                                // 없으면 생성 (오른쪽)
                                targetNetNode = new NodeViewModel(_dockerService, _dialogService)
                                {
                                    Name = netName,
                                    Type = NodeType.Network,
                                    ContainerId = netId,
                                    ImageName = "Network",
                                    X = x + 250, // ★ 오른쪽
                                    Y = y + (rightSideItemCount * 120), // 아래로 쌓임
                                    Width = 160,
                                    Height = 80,
                                    StatusColor = "#9B59B6" // 보라색
                                };
                                ActiveSheet.Nodes.Add(targetNetNode);
                                rightSideItemCount++; // 다음 아이템을 위해 인덱스 증가
                            }

                            // 선 연결 (컨테이너 -> 네트워크)
                            bool connExists = ActiveSheet.Connectors.Any(c =>
                                (c.Source == mainNode && c.Target == targetNetNode) ||
                                (c.Source == targetNetNode && c.Target == mainNode));

                            if (!connExists)
                            {
                                var conn = new ConnectorViewModel(
                                    mainNode, targetNetNode,
                                    PortDirection.Right, PortDirection.Left, // Container(Right) -> Net(Left)
                                    _dockerService, _dialogService);

                                conn.RelationType = RelationType.NetworkAttach;
                                ActiveSheet.Connectors.Add(conn);
                            }
                        }
                    }

                    // -----------------------------------------------------------
                    // (2) 볼륨 연결 (오른쪽 배치, 네트워크 밑에 이어서)
                    // -----------------------------------------------------------
                    if (info.Mounts != null)
                    {
                        foreach (var mount in info.Mounts)
                        {
                            if (mount.Type == "volume")
                            {
                                string volName = mount.Name;
                                string destination = mount.Destination;

                                // 이미 시트에 있는지 확인
                                var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n => n.Type == NodeType.Volume && n.Name == volName);
                                NodeViewModel targetVolNode;

                                if (existingVolNode != null)
                                {
                                    targetVolNode = existingVolNode;
                                }
                                else
                                {
                                    // 없으면 생성 (오른쪽)
                                    targetVolNode = new NodeViewModel(_dockerService, _dialogService)
                                    {
                                        Name = volName,
                                        Type = NodeType.Volume,
                                        ImageName = mount.Driver ?? "local",
                                        X = x + 250, // ★ 오른쪽
                                        Y = y + (rightSideItemCount * 120), // 네트워크 개수 다음부터 이어서 배치
                                        Width = 160,
                                        Height = 80,
                                        StatusColor = "#E67E22" // 주황색
                                    };
                                    ActiveSheet.Nodes.Add(targetVolNode);
                                    rightSideItemCount++; // 인덱스 증가
                                }

                                // 선 연결 (컨테이너 -> 볼륨)
                                bool connExists = ActiveSheet.Connectors.Any(c =>
                                    (c.Source == mainNode && c.Target == targetVolNode) ||
                                    (c.Source == targetVolNode && c.Target == mainNode));

                                if (!connExists)
                                {
                                    var conn = new ConnectorViewModel(
                                        mainNode, targetVolNode,
                                        PortDirection.Right, PortDirection.Left, // Container(Right) -> Volume(Left)
                                        _dockerService, _dialogService);

                                    conn.RelationType = RelationType.VolumeMount;
                                    conn.MountPath = destination;

                                    ActiveSheet.Connectors.Add(conn);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage($"컨테이너 연관 노드 자동 탐색 실패 : {ex.Message}");
                }
            }

            // =========================================================
            // CASE B: 네트워크를 올렸을 때 -> 연결된 '컨테이너' 자동 찾기 (기존 유지)
            // =========================================================
            else if (container.Type == NodeType.Network && !string.IsNullOrEmpty(container.Id))
            {
                try
                {
                    var networkInfo = await _dockerService.InspectNetworkAsync(container.Id);

                    if (networkInfo.Containers != null && networkInfo.Containers.Count > 0)
                    {
                        int containerCount = 0;

                        foreach (var containerPair in networkInfo.Containers)
                        {
                            string cId = containerPair.Key;
                            string cName = containerPair.Value.Name;

                            string foundImageName = "Unknown";
                            try
                            {
                                var detailedInfo = await _dockerService.InspectContainerAsync(cId);
                                foundImageName = detailedInfo.Config.Image;
                            }
                            catch { }

                            var existingContainerNode = ActiveSheet.Nodes
                                .FirstOrDefault(n => n.ContainerId == cId || n.Name == cName);

                            NodeViewModel targetContainerNode;

                            if (existingContainerNode != null)
                            {
                                targetContainerNode = existingContainerNode;
                                if (string.IsNullOrEmpty(targetContainerNode.ImageName))
                                {
                                    targetContainerNode.ImageName = foundImageName;
                                }
                            }
                            else
                            {
                                targetContainerNode = new NodeViewModel(_dockerService, _dialogService)
                                {
                                    Name = cName,
                                    ContainerId = cId,
                                    Type = NodeType.Container,
                                    ImageName = foundImageName,
                                    X = x + 250,
                                    Y = y + (containerCount * 120),
                                    Width = 160,
                                    Height = 80,
                                    StatusColor = "#28a745"
                                };
                                ActiveSheet.Nodes.Add(targetContainerNode);
                                containerCount++;
                            }

                            bool connExists = ActiveSheet.Connectors.Any(c =>
                                (c.Source == mainNode && c.Target == targetContainerNode) ||
                                (c.Source == targetContainerNode && c.Target == mainNode));

                            if (!connExists)
                            {
                                var conn = new ConnectorViewModel(
                                    mainNode, targetContainerNode,
                                    PortDirection.Right, PortDirection.Left,
                                    _dockerService, _dialogService);

                                ActiveSheet.Connectors.Add(conn);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage($"네트워크 하위 컨테이너 연결 실패 : {ex.Message}");
                }
            }

            IsModified = true;
        }

        private void SyncCollection<T>(ObservableCollection<T> uiCollection, List<T> newItems, Func<T, string> keySelector)
        {
            // 1. 제거해야 할 항목 찾기 (UI에는 있는데, 새 데이터에는 없는 것)
            var newKeys = new HashSet<string>(newItems.Select(keySelector));
            var toRemove = uiCollection.Where(item => !newKeys.Contains(keySelector(item))).ToList();

            foreach (var item in toRemove)
            {
                uiCollection.Remove(item);
            }

            // 2. 추가해야 할 항목 찾기 (새 데이터에는 있는데, UI에는 없는 것)
            var currentKeys = new HashSet<string>(uiCollection.Select(keySelector));
            foreach (var item in newItems)
            {
                if (!currentKeys.Contains(keySelector(item)))
                {
                    uiCollection.Add(item);
                }
            }

            // (선택 사항) 상태 업데이트: 만약 ID는 같은데 상태가 변했다면 여기서 속성만 복사해줄 수 있음
            // 컨테이너의 경우 Running <-> Exited 상태 변화 반영을 위해 필요할 수 있음
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

        public async Task<bool> ConnectVolumeToContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode)
        {
            // 1. 다이얼로그로 경로와 소유자 정보 입력받기
            var dlg = new DockerDiagram.Views.MountDialog();
            dlg.Owner = Application.Current.MainWindow;

            // 취소 버튼을 누르면 즉시 종료 및 false 반환 (선 연결 취소)
            if (dlg.ShowDialog() != true) return false;

            string mountPath = dlg.MountPath; // 예: /var/lib/mysql
            string owner = dlg.VolumeOwner;   // 예: mysql:mysql

            // [변경] _dockerService 사용
            string containerId = containerNode.ContainerId;
            string volumeName = volumeNode.Name;

            bool keepBackup = false;

            // 호스트 임시 백업 폴더 경로 생성
            string tempHostPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docker_backup_" + Guid.NewGuid());

            Mouse.OverrideCursor = Cursors.Wait; // 로딩 커서 표시

            try
            {
                // ---------------------------------------------------------
                // [STEP 1] 데이터 백업 (Backup)
                // ---------------------------------------------------------
                // 데이터 정합성을 위해 컨테이너가 실행 중이면 잠시 정지
                if (containerNode.IsRunning)
                {
                    await _dockerService.StopContainerAsync(containerId);
                }

                // 호스트 임시 폴더 생성
                if (!System.IO.Directory.Exists(tempHostPath))
                    System.IO.Directory.CreateDirectory(tempHostPath);

                // 컨테이너 내부 데이터를 호스트 임시 폴더로 복사 (docker cp)
                await _dockerService.CopyFromContainerAsync(containerId, mountPath, tempHostPath);


                // ---------------------------------------------------------
                // [STEP 2] 기존 컨테이너 설정 조회 (설정 유지용)
                // ---------------------------------------------------------
                var inspect = await _dockerService.InspectContainerAsync(containerId);
                var oldConfig = inspect.Config;
                var oldHostConfig = inspect.HostConfig;

                // 2-1. 이미지 정보 (repo:tag 분리)
                string imageName = oldConfig.Image; // 예: "mysql:5.7"
                string imgRepo = imageName;
                string imgTag = "latest";

                int lastColonIndex = imageName.LastIndexOf(':');

                // 콜론이 존재하고, 맨 앞자리(0번 인덱스)가 아닌 경우 (예: ":latest" 방지)
                if (lastColonIndex > 0)
                {
                    // 마지막 콜론 앞부분 전체 (예: localhost:5000/my-image)
                    imgRepo = imageName[..lastColonIndex];

                    // 마지막 콜론 뒷부분 (예: latest)
                    imgTag = imageName[(lastColonIndex + 1)..];
                }
                else
                {
                    // 콜론이 없는 경우 (예: nginx)
                    imgRepo = imageName;
                    imgTag = "latest";
                }

                // 2-2. 환경변수 복사
                var envs = oldConfig.Env != null ? oldConfig.Env.ToList() : new List<string>();

                // 2-3. 포트 바인딩 복구 (Dictionary -> List<string> "8080:80" 형식 변환)
                var ports = new List<string>();
                if (oldHostConfig.PortBindings != null)
                {
                    foreach (var pb in oldHostConfig.PortBindings)
                    {
                        // pb.Key는 "80/tcp", pb.Value는 [{HostIp, HostPort}] 형태
                        string containerPort = pb.Key.Split('/')[0]; // "80"
                        if (pb.Value != null && pb.Value.Count > 0)
                        {
                            string hostPort = pb.Value[0].HostPort;
                            ports.Add($"{hostPort}:{containerPort}");
                        }
                    }
                }

                // 2-4. 기존 볼륨 목록 유지 + 새 볼륨 추가
                var volumes = new List<string>();
                if (oldHostConfig.Binds != null)
                {
                    volumes.AddRange(oldHostConfig.Binds);
                }
                // 새 볼륨 추가: "볼륨이름:컨테이너경로"
                volumes.Add($"{volumeName}:{mountPath}");

                // 2-5. 리소스 제한 및 정책 (간단히 RestartPolicy만 복구, 필요 시 Memory/CPU 추가 파싱)
                string restartPolicy = oldHostConfig.RestartPolicy.Name.ToString();


                // ---------------------------------------------------------
                // [STEP 3] 기존 컨테이너 삭제
                // ---------------------------------------------------------
                await _dockerService.RemoveContainerAsync(containerId);


                // ---------------------------------------------------------
                // [STEP 4] 컨테이너 재생성 (볼륨 마운트가 포함됨)
                // ---------------------------------------------------------
                string newId = await _dockerService.CreateAndStartContainerAsync(
                    containerNode.Name,
                    imgRepo,
                    imgTag,
                    ports,
                    envs,
                    volumes,
                    restartPolicy,
                    0, 0 // 메모리/CPU 제한은 편의상 무제한(0)으로 설정 (필요시 inspect 값 사용)
                );


                // ---------------------------------------------------------
                // [STEP 5] 데이터 복원 (Restore)
                // ---------------------------------------------------------
                // 호스트 임시 폴더의 데이터를 새로 만든 컨테이너로 복사
                // (이때 데이터는 컨테이너 내부가 아닌, 마운트된 볼륨으로 들어갑니다)
                string folderName = System.IO.Path.GetFileName(mountPath.TrimEnd('/'));
                string actualSourcePath = System.IO.Path.Combine(tempHostPath, folderName);

                // 하위 폴더가 실제로 존재하면 그 안의 내용물을 복사하고, 아니면(파일 단위 복사 등 예외) 기존 경로 사용
                if (System.IO.Directory.Exists(actualSourcePath))
                {
                    await _dockerService.CopyToContainerAsync(newId, actualSourcePath, mountPath);
                }
                else
                {
                    // 만약 Docker가 폴더 없이 내용물만 줬거나 경로가 달랐을 경우에 대한 대비
                    await _dockerService.CopyToContainerAsync(newId, tempHostPath, mountPath);
                }


                // ---------------------------------------------------------
                // [STEP 6] 권한 수정 (Permission Fix) - chown
                // ---------------------------------------------------------
                // 복사 과정에서 소유권이 root로 바뀌는 문제를 해결
                if (!string.IsNullOrWhiteSpace(owner))
                {
                    // 예: "chown -R mysql:mysql /var/lib/mysql"
                    string cmd = $"chown -R {owner} {mountPath}";
                    await _dockerService.ExecuteCommandAsync(newId, cmd);
                }


                // ---------------------------------------------------------
                // [STEP 7] UI 갱신 및 마무리
                // ---------------------------------------------------------

                containerNode.ContainerId = newId;

                // 상태 표시 갱신 (Running 등)
                await containerNode.RefreshDetailsAsync();

                // [변경] DialogService 사용
                _dialogService.ShowMessage("볼륨 연결 및 데이터 마이그레이션이 완료되었습니다!");

                return true; // 성공! (선 그리기 허용)
            }
            catch (Exception ex)
            {
                keepBackup = true;

                Debug.WriteLine($"[ConnectVolume] ERROR: {ex}");
                Debug.WriteLine($"[ConnectVolume] Backup preserved at: {tempHostPath}");

                // [변경] DialogService 사용
                _dialogService.ShowMessage($"작업 중 오류 발생: {ex.Message}\n\n(백업 데이터는 '{tempHostPath}'에 보존되었습니다.)");

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

        public async Task CreateNewVolumeNodeAsync(string name, string driver, double x, double y)
        {
            if (ActiveSheet == null) return;

            // Placeholder (★ NodeViewModel 생성 시 서비스 전달)
            var node = new NodeViewModel(_dockerService, _dialogService)
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
                // [변경] _dockerService 사용
                await _dockerService.CreateVolumeAsync(name, driver);

                // 완료
                node.Name = name;
                node.ContainerId = ""; // 볼륨은 보통 ID가 이름과 같거나 별도 관리됨. 일단 비워두거나 Name을 씀.
                node.IsCreating = false;

                // 볼륨은 보통 Running/Stop 개념이 없으므로 기본 색상 유지 또는 변경
                node.StatusColor = "#E67E22"; // 주황색

                // 통계나 리스트 갱신이 필요하면 호출
                // UpdateAvailableItems(); 
            }
            catch (Exception ex)
            {
                // [변경] DialogService 사용
                _dialogService.ShowMessage($"볼륨 생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        // 2. 네트워크 생성 (비동기)
        public async Task CreateNewNetworkNodeAsync(string name, string driver, double x, double y)
        {
            if (ActiveSheet == null) return;

            // Placeholder (★ NodeViewModel 생성 시 서비스 전달)
            var node = new NodeViewModel(_dockerService, _dialogService)
            {
                Name = $"{name} (Creating...)",
                ImageName = driver,
                Type = NodeType.Network,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107"
            };
            ActiveSheet.Nodes.Add(node);

            try
            {
                // [변경] _dockerService 사용
                string netId = await _dockerService.CreateNetworkAsync(name, driver);

                // 완료
                node.Name = name;
                // ID 12자리 자르기 (컨테이너와 동일 규칙)
                node.ContainerId = netId;

                node.IsCreating = false;
                node.StatusColor = "#9B59B6"; // 보라색

                // UpdateAvailableItems();
            }
            catch (Exception ex)
            {
                // [변경] DialogService 사용
                _dialogService.ShowMessage($"네트워크 생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
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
                    // [변경] DialogService 사용
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
            string? savedPath = FileService.SaveDiagramAs(this);

            // 저장을 성공적으로 했으면, 현재 경로 업데이트
            if (!string.IsNullOrEmpty(savedPath))
            {
                CurrentFilePath = savedPath;
                IsModified = false;
            }
        }

        private async Task LoadActionAsync(object? obj)
        {
            // 변경사항이 있다면 물어보기 (선택 사항)
            if (IsModified)
            {
                // [변경] DialogService 사용
                if (!_dialogService.ShowConfirm("변경 사항이 저장되지 않았습니다. 계속하시겠습니까?", "확인"))
                    return;
            }

            // 파일 불러오기 시도
            // ★ [수정] _dockerService와 _dialogService를 모두 전달해야 함
            string? loadedPath = await FileService.LoadDiagramWithDialogAsync(this, _dockerService, _dialogService);

            // 성공적으로 불러왔다면 경로 업데이트
            if (!string.IsNullOrEmpty(loadedPath))
            {
                CurrentFilePath = loadedPath;
                IsModified = false;
            }
        }

        private void AddSheet()
        {
            // ★ SheetViewModel 생성 시 서비스 전달
            var newSheet = new SheetViewModel($"Sheet {Sheets.Count + 1}", _dockerService, _dialogService);
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

            // 1. 유효성 검사
            if (!IsValidConnection(source.Type, target.Type))
            {
                // [변경] DialogService 사용
                _dialogService.ShowMessage("연결할 수 없는 조합입니다.");
                return;
            }

            // 2. 방향 정규화 (Container가 항상 Source가 되도록)
            NodeViewModel finalSource = source;
            NodeViewModel finalTarget = target;
            PortDirection finalSourceDir = sourceDir;
            PortDirection finalTargetDir = targetDir;

            if (source.Type != NodeType.Container && target.Type == NodeType.Container)
            {
                finalSource = target;
                finalTarget = source;
                finalSourceDir = targetDir;
                finalTargetDir = sourceDir;
            }

            // =========================================================
            // ★ [CASE 1] 컨테이너 <-> 볼륨 (기존 로직 유지)
            // =========================================================
            if (finalSource.Type == NodeType.Container && finalTarget.Type == NodeType.Volume)
            {
                bool isSuccess = await ConnectVolumeToContainerAsync(finalSource, finalTarget);
                if (!isSuccess) return;
            }

            // =========================================================
            // ★ [CASE 2] 컨테이너 <-> 네트워크 (신규 추가: 물리적 연결)
            // =========================================================
            if (finalSource.Type == NodeType.Container && finalTarget.Type == NodeType.Network)
            {
                try
                {
                    // [변경] _dockerService 사용
                    // 물리적 연결 시도 (docker network connect)
                    await _dockerService.ConnectNetworkAsync(finalTarget.ContainerId, finalSource.ContainerId);

                    // 성공하면 컨테이너 정보 갱신 (IP 주소 등 변경사항 반영)
                    await finalSource.RefreshDetailsAsync();
                }
                catch (Exception ex)
                {
                    // [변경] DialogService 사용
                    _dialogService.ShowMessage($"네트워크 연결 실패 : {ex.Message}");
                    return; // 실패하면 선을 긋지 않음
                }
            }

            // 3. 중복 연결 방지 및 실제 선(Connector) 추가
            bool exists = ActiveSheet.Connectors.Any(c =>
                (c.Source == finalSource && c.Target == finalTarget) ||
                (c.Source == finalTarget && c.Target == finalSource));

            if (!exists)
            {
                // ★ ConnectorViewModel 생성 시 서비스 전달
                var newConnector = new ConnectorViewModel(finalSource, finalTarget, finalSourceDir, finalTargetDir, _dockerService, _dialogService);

                // 관계 타입 설정
                if (finalTarget.Type == NodeType.Volume)
                {
                    newConnector.RelationType = RelationType.VolumeMount;

                    // ★ [추가된 부분] 볼륨일 경우, 사용자가 입력했던 경로를 가져와야 함
                    // 하지만 AddConnection은 경로를 인자로 안 받고 있죠?
                    // 임시 방편: 기본값을 넣거나, 추후 다이얼로그 결과를 여기에 전달해야 함.

                    // ※ 일단 기본값 대신 "사용자가 나중에 속성창에서 수정 가능하게" 만듭니다.
                    newConnector.MountPath = "/var/lib/mysql"; // (임시 기본값)
                }
                else if (finalTarget.Type == NodeType.Network)
                {
                    newConnector.RelationType = RelationType.NetworkAttach;
                }
                else
                {
                    newConnector.RelationType = RelationType.Dependency;
                }

                ActiveSheet.Connectors.Add(newConnector);
            }

            IsModified = true;
        }

        private bool IsValidConnection(NodeType t1, NodeType t2)
        {
            // 1. 볼륨끼리, 네트워크끼리, 볼륨-네트워크 연결 불가
            if (t1 != NodeType.Container && t2 != NodeType.Container) return false;

            // 위의 조건만 통과하면 나머지는 (적어도 한쪽은 컨테이너이므로) 모두 허용
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
                // -> Docker 설정 아님. 그림만 지움.
                if (conn.RelationType == RelationType.Dependency)
                {
                    ActiveSheet.Connectors.Remove(conn);
                }

                // 2. 컨테이너 ↔ 네트워크 (Network Attach)
                // -> ★ [물리적 해제] API 호출로 실제 연결 끊기
                else if (conn.RelationType == RelationType.NetworkAttach)
                {
                    // [변경] DialogService 사용
                    if (_dialogService.ShowConfirm(
                        $"컨테이너 '{conn.Source.Name}'를 네트워크 '{conn.Target.Name}'에서 분리하시겠습니까?\n(실제 Docker 연결이 해제됩니다.)",
                        "네트워크 연결 해제"))
                    {
                        try
                        {
                            // [변경] _dockerService 사용
                            // 물리적 해제 시도 (docker network disconnect)
                            await _dockerService.DisconnectNetworkAsync(conn.Target.ContainerId, conn.Source.ContainerId);

                            // 성공 시 선 삭제 및 정보 갱신 (IP 변경 등 반영)
                            ActiveSheet.Connectors.Remove(conn);
                            await conn.Source.RefreshDetailsAsync();
                        }
                        catch (Exception ex)
                        {
                            // API 실패 시 사용자가 원하면 선만이라도 지워줌 (강제 삭제)
                            if (_dialogService.ShowConfirm($"네트워크 해제 실패: {ex.Message}\n\n강제로 선만 삭제하시겠습니까?", "오류"))
                            {
                                ActiveSheet.Connectors.Remove(conn);
                            }
                        }
                    }
                }

                // 3. 컨테이너 ↔ 볼륨 (Volume Mount)
                // -> [선택권 부여] 재생성(물리적) vs 선삭제(논리적)
                else if (conn.RelationType == RelationType.VolumeMount)
                {
                    // ★ 중요: 3단계 분기(Yes/No/Cancel)는 IDialogService(True/False)로 완벽 대응이 불가능하므로
                    // 기존 MessageBox 로직을 유지합니다. (핵심 로직 보존)
                    var result = MessageBox.Show(
                        "실제 Docker 컨테이너에서도 볼륨 연결을 해제하시겠습니까?\n" +
                        "(컨테이너가 재생성됩니다. 데이터는 볼륨에 안전하게 남습니다.)\n\n" +
                        "[예(Yes)] : Docker에서 해제 (물리적 해제 - 재생성)\n" +
                        "[아니요(No)] : 선만 삭제 (논리적 삭제)\n" +
                        "[취소(Cancel)] : 작업 취소",
                        "볼륨 연결 해제",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel) return;

                    if (result == MessageBoxResult.Yes) // 물리적 해제
                    {
                        // 헬퍼 메서드 호출 (UnmountVolumeFromContainerAsync)
                        bool success = await UnmountVolumeFromContainerAsync(conn.Source, conn.Target);
                        if (success)
                        {
                            ActiveSheet.Connectors.Remove(conn);
                        }
                    }
                    else // 논리적 삭제
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
                // ★ 중요: 3단계 분기(Yes/No/Cancel) 유지
                var result = MessageBox.Show(
                    "선택한 항목을 삭제하시겠습니까?\n\n" +
                    "[예(Yes)] : Docker에서도 영구 삭제 (완전 삭제)\n" +
                    "[아니요(No)] : 시트에서만 제거 (목록 제거)\n" +
                    "[취소(Cancel)] : 작업 취소",
                    "삭제 옵션",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel) return;

                // 1. Docker 영구 삭제 시도
                if (result == MessageBoxResult.Yes)
                {
                    if (!string.IsNullOrEmpty(node.ContainerId))
                    {
                        try
                        {
                            // [변경] _dockerService 사용
                            await Task.Run(async () =>
                            {
                                if (node.Type == NodeType.Container)
                                    await _dockerService.RemoveContainerAsync(node.ContainerId);
                                else if (node.Type == NodeType.Network)
                                    await _dockerService.RemoveNetworkAsync(node.ContainerId);
                                // 볼륨 삭제는 데이터 손실 위험이 크므로 보통 API 호출을 신중히 함 (여기선 생략)
                            });
                        }
                        catch (Exception ex)
                        {
                            // [변경] DialogService 사용
                            _dialogService.ShowMessage($"Docker 리소스 삭제 실패: {ex.Message}\n\n목록에서 제거되지 않습니다.");
                            return; // 에러 발생 시 시트에서 지우지 않고 중단
                        }
                    }
                }

                // 2. 시트에서 제거 (공통 수행)
                // 연결된 선 먼저 제거
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
                ActiveSheet.Groups.Remove(group);
            }

            // 삭제 후 선택 해제
            SelectedElement = null;
        }

        private async Task<bool> UnmountVolumeFromContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode)
        {
            // [변경] _dockerService 사용
            string containerId = containerNode.ContainerId;
            string volumeNameToRemove = volumeNode.Name;

            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                // 1. 정지
                if (containerNode.IsRunning)
                {
                    await _dockerService.StopContainerAsync(containerId);
                }

                // 2. 정보 조회
                var inspect = await _dockerService.InspectContainerAsync(containerId);
                var oldConfig = inspect.Config;
                var oldHostConfig = inspect.HostConfig;

                // 3. 이미지 정보 파싱
                string imageName = oldConfig.Image;
                string imgRepo = imageName;
                string imgTag = "latest";
                int lastColonIndex = imageName.LastIndexOf(':');

                // 콜론이 존재하고, 맨 앞자리(0번 인덱스)가 아닌 경우 (예: ":latest" 방지)
                if (lastColonIndex > 0)
                {
                    // 마지막 콜론 앞부분 전체 (예: localhost:5000/my-image)
                    imgRepo = imageName.Substring(0, lastColonIndex);

                    // 마지막 콜론 뒷부분 (예: latest)
                    imgTag = imageName.Substring(lastColonIndex + 1);
                }
                else
                {
                    // 콜론이 없는 경우 (예: nginx)
                    imgRepo = imageName;
                    imgTag = "latest";
                }

                // 4. 포트 복구
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

                // 5. 환경변수 복구
                var envs = oldConfig.Env != null ? oldConfig.Env.ToList() : new List<string>();

                // 6. 볼륨 필터링 (제거할 볼륨만 뺌)
                var newVolumes = new List<string>();
                if (oldHostConfig.Binds != null)
                {
                    foreach (var bind in oldHostConfig.Binds)
                    {
                        // bind 문자열 예시: "my_vol:/var/lib/mysql"
                        // 앞부분이 제거하려는 볼륨 이름과 다를 때만 유지
                        if (!bind.StartsWith(volumeNameToRemove + ":"))
                        {
                            newVolumes.Add(bind);
                        }
                    }
                }

                // 7. 기존 컨테이너 삭제
                await _dockerService.RemoveContainerAsync(containerId);

                // 8. 재생성
                string newId = await _dockerService.CreateAndStartContainerAsync(
                    containerNode.Name, imgRepo, imgTag, ports, envs, newVolumes,
                    oldHostConfig.RestartPolicy.Name.ToString(), 0, 0
                );

                containerNode.ContainerId = newId;
                await containerNode.RefreshDetailsAsync();

                return true;
            }
            catch (Exception ex)
            {
                // [변경] DialogService 사용
                _dialogService.ShowMessage($"해제 중 오류 발생: {ex.Message}");
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void AttachSheetEvents()
        {
            if (ActiveSheet != null)
            {
                ActiveSheet.Nodes.CollectionChanged -= Nodes_CollectionChanged;
                ActiveSheet.Nodes.CollectionChanged += Nodes_CollectionChanged;
            }
        }

        private void Nodes_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateAvailableItems();
        }
    }
}