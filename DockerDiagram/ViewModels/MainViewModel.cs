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
        public System.Windows.Input.ICommand ExportComposeCommand { get; }

        private bool _isModified = false;
        public bool IsModified
        {
            get => _isModified;
            set { _isModified = value; OnPropertyChanged(); } // 상태 바인딩 가능
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
                    UpdateAvailableItems(); // 시트 변경 시 리스트 갱신
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

                // 2. 노드가 선택되었다면, 상세 정보(Inspect)를 비동기로 갱신. (Fire-and-forget 방식으로 실행하여 UI 끊김 방지)
                if (_selectedElement is NodeViewModel nodeVm)
                {
                    _ = nodeVm.RefreshDetailsAsync();
                }
            }
        }

        private bool _isSyncing = false;

        public bool IsDetailPanelOpen => _selectedElement != null;

        // --- 3. 아코디언 데이터 ---

        // (1) 템플릿 목록 (기본 + 빈도 추천)
        public ObservableCollection<TemplateItem> Templates { get; } = new();

        // (2) 기존 컨테이너 (싱글톤: 맵에 있으면 숨김)
        public ObservableCollection<DockerContainer> ExistingContainers { get; } = new();
        public ObservableCollection<DockerContainer> ExistingVolumes { get; } = new();
        public ObservableCollection<DockerContainer> ExistingNetworks { get; } = new();
        // (3) 도커 이미지 (삭제 관리)
        public ObservableCollection<DockerImage> LocalImages { get; } = new();

        // 내부 데이터 저장소
        private List<DockerContainer> _allContainers = new();
        private Dictionary<string, int> _usageStats = new Dictionary<string, int>(); // 사용 빈도 기록

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

        // [2] 삭제 커맨드 추가
        public ICommand DeleteContainerItemCommand { get; }
        public ICommand DeleteVolumeItemCommand { get; }
        public ICommand DeleteNetworkItemCommand { get; }

        public ICommand SaveCommand { get; }

        // --- 생성자 ---
        public MainViewModel()
        {
            // 기본 시트 추가
            Sheets.Add(new SheetViewModel("Dev Environment"));
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
            SaveCommand = new RelayCommand(_ => FileService.SaveDiagram(this));

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
            _autoSyncTimer.Interval = TimeSpan.FromSeconds(15);
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
            // 1. 이미 갱신 작업 중이라면? 이번 턴은 무시하고 돌아감 (Skip)
            if (_isSyncing) return;

            try
            {
                // 2. 작업 시작 표시 (깃발 올림)
                _isSyncing = true;

                // 3. 실제 Docker 갱신 요청
                await SyncWithDockerEngine();
            }
            catch (Exception ex)
            {
                // 에러 로그 (필요 시)
                Debug.WriteLine($"[DockerDiscovery] AutoSync Error: {ex.Message}");
            }
            finally
            {
                // 4. 작업이 끝나면 무조건 깃발 내림 (다음 턴을 위해)
                _isSyncing = false;
            }
        }

        private async Task DeleteContainerItemAsync(object? param)
        {
            if (param is DockerContainer c && MessageBox.Show($"컨테이너 '{c.Name}'을 영구 삭제하시겠습니까?", "확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try {
                    await DockerApiService.Instance.RemoveContainerAsync(c.Id);
                    await SyncWithDockerEngine();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"삭제 실패: {ex.Message}");
                }
            }
        }

        private async Task DeleteVolumeItemAsync(object? param)
        {
            if (param is DockerContainer v && MessageBox.Show($"볼륨 '{v.Name}'을 영구 삭제하시겠습니까?", "확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    await DockerApiService.Instance.RemoveVolumeAsync(v.Name);
                    await SyncWithDockerEngine(); // 목록 갱신
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"볼륨 삭제 실패: {ex.Message}");
                }
            }
        }

        private async Task DeleteNetworkItemAsync(object? param)
        {
            if (param is DockerContainer n && MessageBox.Show($"네트워크 '{n.Name}'을 영구 삭제하시겠습니까?", "확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try {
                    await DockerApiService.Instance.RemoveNetworkAsync(n.Id);
                    await SyncWithDockerEngine();
                }
                catch (Exception ex) {
                    MessageBox.Show($"네트워크 삭제 실패: {ex.Message}");
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
                var api = DockerApiService.Instance;
                if (!await api.PingAsync()) return;

                // 1. 원본 데이터 가져오기
                _rawContainers = await api.GetContainersAsync();
                _rawVolumes = await api.GetVolumesAsync();
                _rawNetworks = await api.GetNetworksAsync();
                _rawImages = await api.GetImagesAsync();

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
                .Where(v => !usedVolumeNames.Contains(v.Name)) // ★ 시트에 있는 이름이면 제외
                .Where(v => string.IsNullOrEmpty(VolumeSearchText) || v.Name.Contains(VolumeSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingVolumes, filteredVolumes, v => v.Name);

            // 5. 네트워크 필터링 (ID 기준 + 기본 네트워크 숨김)
            var defaultNetworks = new HashSet<string> { "bridge", "host", "none" };
            var filteredNetworks = _rawNetworks
                .Where(n => !usedNetworkIds.Contains(n.Id)) // ★ 시트에 있는 ID면 제외 (Canvas_Drop 수정 필수)
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
                if (MessageBox.Show($"이미지 '{img.Repository}'를 삭제하시겠습니까?", "이미지 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    var api = DockerApiService.Instance;
                    try
                    {
                        // 정상 삭제 시도
                        await api.DeleteImageAsync(img.Id, force: false);
                        LocalImages.Remove(img);
                    }
                    catch (Exception ex)
                    {
                        // 실패 시 강제 삭제 제안
                        var res = MessageBox.Show(
                            $"삭제 실패: 이미지가 사용 중일 수 있습니다.\n강제로 삭제하시겠습니까?\n({ex.Message})",
                            "강제 삭제 확인",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (res == MessageBoxResult.Yes)
                        {
                            try
                            {
                                await api.DeleteImageAsync(img.Id, force: true);
                                LocalImages.Remove(img);
                            }
                            catch (Exception forceEx)
                            {
                                MessageBox.Show($"강제 삭제 실패: {forceEx.Message}");
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

            // 1. Placeholder 노드 생성
            var node = new NodeViewModel
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
                var api = DockerApiService.Instance;
                await api.PullImageAsync(image, tag);

                // 2. 컨테이너 생성 및 실행
                string containerId = await api.CreateAndStartContainerAsync(
                    name, image, tag, ports, envs, volumes, restartPolicy, memoryMb, cpuCount);

                // 3. 완료 처리
                node.Name = name;

                node.ContainerId = containerId;

                node.PortInfo = string.Join(", ", ports);
                node.IsCreating = false;
                node.StatusColor = "#28a745";

                // ★ [핵심] 중복 생성(CreateNodeAt) 호출을 지우고 통계만 기록
                RegisterTemplateUsage($"{image}:{tag}");

                // ★ [핵심] 리스트 즉시 갱신 (목록에서 사라짐)
                UpdateAvailableItems();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        // 일반 노드 생성 (드래그 앤 드롭 등)
        public async Task CreateNodeAtAsync(DockerContainer container, double x, double y)
        {
            if (ActiveSheet == null) return;

            // 1. 컨테이너 노드 생성 (기존 로직)
            var containerNode = new NodeViewModel
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
            ActiveSheet.Nodes.Add(containerNode);
            RegisterTemplateUsage(container.Image); // 통계 기록
            IsModified = true;

            // ★ [NEW] 컨테이너가 생성된 후, 연결된 볼륨이 있는지 검사 (Auto-Discovery)
            if (container.Type == NodeType.Container && !string.IsNullOrEmpty(container.Id))
            {
                try
                {
                    var api = DockerApiService.Instance;
                    var info = await api.InspectContainerAsync(container.Id);

                    if (info.Mounts != null)
                    {
                        int volCount = 0; // 배치 위치 계산용

                        foreach (var mount in info.Mounts)
                        {
                            // "volume" 타입인 경우만 처리 (bind mount 제외)
                            // 필요 시 mount.Type == "bind" 도 추가 가능
                            if (mount.Type == "volume")
                            {
                                string volName = mount.Name;
                                string destination = mount.Destination; // 컨테이너 내부 경로

                                // 2. 이미 시트에 존재하는 볼륨인지 확인 (중복 생성 방지)
                                var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n => n.Type == NodeType.Volume && n.Name == volName);

                                NodeViewModel targetVolNode;

                                if (existingVolNode != null)
                                {
                                    // [Case A] 이미 있으면 그 놈을 타겟으로 잡음
                                    targetVolNode = existingVolNode;
                                }
                                else
                                {
                                    // [Case B] 없으면 새로 생성 (위치는 컨테이너 오른쪽)
                                    targetVolNode = new NodeViewModel
                                    {
                                        Name = volName,
                                        Type = NodeType.Volume,
                                        ImageName = mount.Driver ?? "local",
                                        // 위치: 컨테이너 오른쪽 250px, 여러 개면 아래로 쌓음
                                        X = x + 250,
                                        Y = y + (volCount * 120),
                                        Width = 160,
                                        Height = 80,
                                        StatusColor = "#E67E22" // 주황색
                                    };
                                    ActiveSheet.Nodes.Add(targetVolNode);
                                    volCount++;
                                }

                                // 3. 선 연결 (Connector)
                                // 컨테이너 -> 볼륨 방향으로 연결
                                // 이미 연결된 선이 있는지 확인
                                bool connExists = ActiveSheet.Connectors.Any(c =>
                                    (c.Source == containerNode && c.Target == targetVolNode) ||
                                    (c.Source == targetVolNode && c.Target == containerNode));

                                if (!connExists)
                                {
                                    var conn = new ConnectorViewModel(containerNode, targetVolNode, PortDirection.Right, PortDirection.Left);

                                    // ★ 중요: 마운트 경로 정보 입력
                                    conn.RelationType = RelationType.VolumeMount;
                                    conn.MountPath = destination;

                                    ActiveSheet.Connectors.Add(conn);
                                }
                            }
                        }
                    }
                    IsModified = true;
                }
                catch (Exception ex)
                {
                    // 볼륨 자동 연결 실패해도 컨테이너 생성은 유지
                    Debug.WriteLine($"자동 탐색 실패 : {ex.Message}");
                }
            }
        }

        // 시트 이벤트 연결
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

        // --- 8. 기타 시트 관리 및 Delegate ---

        private void AddSheet()
        {
            var newSheet = new SheetViewModel($"Sheet {Sheets.Count + 1}");
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
                MessageBox.Show("연결할 수 없는 조합입니다.");
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
                    var api = DockerApiService.Instance;

                    // 물리적 연결 시도 (docker network connect)
                    await api.ConnectNetworkAsync(finalTarget.ContainerId, finalSource.ContainerId);

                    // 성공하면 컨테이너 정보 갱신 (IP 주소 등 변경사항 반영)
                    await finalSource.RefreshDetailsAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"네트워크 연결 실패 : {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return; // 실패하면 선을 긋지 않음
                }
            }

            // 3. 중복 연결 방지 및 실제 선(Connector) 추가
            bool exists = ActiveSheet.Connectors.Any(c =>
                (c.Source == finalSource && c.Target == finalTarget) ||
                (c.Source == finalTarget && c.Target == finalSource));

            if (!exists)
            {
                var newConnector = new ConnectorViewModel(finalSource, finalTarget, finalSourceDir, finalTargetDir);

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
                    var result = MessageBox.Show(
                        $"컨테이너 '{conn.Source.Name}'를 네트워크 '{conn.Target.Name}'에서 분리하시겠습니까?\n(실제 Docker 연결이 해제됩니다.)",
                        "네트워크 연결 해제",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            var api = DockerApiService.Instance;
                            // 물리적 해제 시도 (docker network disconnect)
                            await api.DisconnectNetworkAsync(conn.Target.ContainerId, conn.Source.ContainerId);

                            // 성공 시 선 삭제 및 정보 갱신 (IP 변경 등 반영)
                            ActiveSheet.Connectors.Remove(conn);
                            await conn.Source.RefreshDetailsAsync();
                        }
                        catch (Exception ex)
                        {
                            // API 실패 시 사용자가 원하면 선만이라도 지워줌 (강제 삭제)
                            if (MessageBox.Show($"네트워크 해제 실패: {ex.Message}\n\n강제로 선만 삭제하시겠습니까?",
                                "오류", MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes)
                            {
                                ActiveSheet.Connectors.Remove(conn);
                            }
                        }
                    }
                }

                // 3. 컨테이너 ↔ 볼륨 (Volume Mount)
                // -> ★ [선택권 부여] 재생성(물리적) vs 선삭제(논리적)
                else if (conn.RelationType == RelationType.VolumeMount)
                {
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
                            var api = DockerApiService.Instance;
                            await Task.Run(async () =>
                            {
                                if (node.Type == NodeType.Container)
                                    await api.RemoveContainerAsync(node.ContainerId);
                                else if (node.Type == NodeType.Network)
                                    await api.RemoveNetworkAsync(node.ContainerId);
                                // 볼륨 삭제는 데이터 손실 위험이 크므로 보통 API 호출을 신중히 함 (여기선 생략)
                            });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Docker 리소스 삭제 실패: {ex.Message}\n\n목록에서 제거되지 않습니다.",
                                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
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
            var api = DockerApiService.Instance;
            string containerId = containerNode.ContainerId;
            string volumeNameToRemove = volumeNode.Name;

            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                // 1. 정지
                if (containerNode.IsRunning)
                {
                    await api.StopContainerAsync(containerId);
                }

                // 2. 정보 조회
                var inspect = await api.InspectContainerAsync(containerId);
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

                // ★★★ 6. 볼륨 필터링 (제거할 볼륨만 뺌) ★★★
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
                await api.RemoveContainerAsync(containerId);

                // 8. 재생성
                string newId = await api.CreateAndStartContainerAsync(
                    containerNode.Name, imgRepo, imgTag, ports, envs, newVolumes,
                    oldHostConfig.RestartPolicy.Name.ToString(), 0, 0
                );

                containerNode.ContainerId = newId;
                await containerNode.RefreshDetailsAsync();

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"해제 중 오류 발생: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        public async Task CreateNewVolumeNodeAsync(string name, string driver, double x, double y)
        {
            if (ActiveSheet == null) return;

            // Placeholder
            var node = new NodeViewModel
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
                var api = DockerApiService.Instance;
                await api.CreateVolumeAsync(name, driver);

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
                MessageBox.Show($"볼륨 생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        // 2. 네트워크 생성 (비동기)
        public async Task CreateNewNetworkNodeAsync(string name, string driver, double x, double y)
        {
            if (ActiveSheet == null) return;

            // Placeholder
            var node = new NodeViewModel
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
                var api = DockerApiService.Instance;
                string netId = await api.CreateNetworkAsync(name, driver);

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
                MessageBox.Show($"네트워크 생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
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

            var api = DockerApiService.Instance;
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
                    await api.StopContainerAsync(containerId);
                }

                // 호스트 임시 폴더 생성
                if (!System.IO.Directory.Exists(tempHostPath))
                    System.IO.Directory.CreateDirectory(tempHostPath);

                // 컨테이너 내부 데이터를 호스트 임시 폴더로 복사 (docker cp)
                await api.CopyFromContainerAsync(containerId, mountPath, tempHostPath);


                // ---------------------------------------------------------
                // [STEP 2] 기존 컨테이너 설정 조회 (설정 유지용)
                // ---------------------------------------------------------
                var inspect = await api.InspectContainerAsync(containerId);
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
                await api.RemoveContainerAsync(containerId);


                // ---------------------------------------------------------
                // [STEP 4] 컨테이너 재생성 (볼륨 마운트가 포함됨)
                // ---------------------------------------------------------
                string newId = await api.CreateAndStartContainerAsync(
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
                    await api.CopyToContainerAsync(newId, actualSourcePath, mountPath);
                }
                else
                {
                    // 만약 Docker가 폴더 없이 내용물만 줬거나 경로가 달랐을 경우에 대한 대비
                    await api.CopyToContainerAsync(newId, tempHostPath, mountPath);
                }


                // ---------------------------------------------------------
                // [STEP 6] 권한 수정 (Permission Fix) - chown
                // ---------------------------------------------------------
                // 복사 과정에서 소유권이 root로 바뀌는 문제를 해결
                if (!string.IsNullOrWhiteSpace(owner))
                {
                    // 예: "chown -R mysql:mysql /var/lib/mysql"
                    string cmd = $"chown -R {owner} {mountPath}";
                    await api.ExecuteCommandAsync(newId, cmd);
                }


                // ---------------------------------------------------------
                // [STEP 7] UI 갱신 및 마무리
                // ---------------------------------------------------------

                containerNode.ContainerId = newId;

                // 상태 표시 갱신 (Running 등)
                await containerNode.RefreshDetailsAsync();

                MessageBox.Show("볼륨 연결 및 데이터 마이그레이션이 완료되었습니다!", "성공", MessageBoxButton.OK, MessageBoxImage.Information);

                return true; // 성공! (선 그리기 허용)
            }
            catch (Exception ex)
            {
                keepBackup = true;

                Debug.WriteLine($"[ConnectVolume] ERROR: {ex}");
                Debug.WriteLine($"[ConnectVolume] Backup preserved at: {tempHostPath}");

                MessageBox.Show(
                    $"작업 중 오류 발생: {ex.Message}\n\n(백업 데이터는 '{tempHostPath}'에 보존되었습니다.)",
                    "오류", MessageBoxButton.OK, MessageBoxImage.Error);

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
    }
}