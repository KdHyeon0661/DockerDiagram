using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Docker.DotNet.Models;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 좌측 사이드바의 리소스 탐색기(템플릿, 컨테이너, 이미지 등)를 관리하는 Sub-ViewModel입니다.
    /// </summary>
    public class ResourceExplorerViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVm;
        private readonly IDockerService _defaultDockerService;
        private readonly IDialogService _dialogService;

        // --- 1. 데이터 컬렉션 ---
        public ObservableCollection<TemplateItem> Templates { get; } = new();
        public ObservableCollection<DockerContainer> ExistingContainers { get; } = new();
        public ObservableCollection<DockerVolume> ExistingVolumes { get; } = new();
        public ObservableCollection<DockerNetworkGroup> ExistingNetworks { get; } = new();
        public ObservableCollection<DockerImage> LocalImages { get; } = new();
        public ObservableCollection<ImageSearchResponse> HubSearchResults { get; } = new();

        // --- 2. 검색 및 상태 필드 ---
        private string _containerSearchText = "";
        public string ContainerSearchText { get => _containerSearchText; set { SetProperty(ref _containerSearchText, value); UpdateAvailableItems(); } }

        private string _volumeSearchText = "";
        public string VolumeSearchText { get => _volumeSearchText; set { SetProperty(ref _volumeSearchText, value); UpdateAvailableItems(); } }

        private string _networkSearchText = "";
        public string NetworkSearchText { get => _networkSearchText; set { SetProperty(ref _networkSearchText, value); UpdateAvailableItems(); } }

        private string _imageSearchText = "";
        public string ImageSearchText { get => _imageSearchText; set { SetProperty(ref _imageSearchText, value); UpdateAvailableItems(); } }

        private string _hubSearchTerm = "";
        public string HubSearchTerm { get => _hubSearchTerm; set => SetProperty(ref _hubSearchTerm, value); }

        private bool _isSearchingHub;
        public bool IsSearchingHub { get => _isSearchingHub; set => SetProperty(ref _isSearchingHub, value); }

        private bool _isPulling;
        public bool IsPulling { get => _isPulling; set => SetProperty(ref _isPulling, value); }

        private double _pullProgressValue;
        public double PullProgressValue { get => _pullProgressValue; set => SetProperty(ref _pullProgressValue, value); }

        private string _pullProgressMessage = "";
        public string PullProgressMessage { get => _pullProgressMessage; set => SetProperty(ref _pullProgressMessage, value); }

        private string _lastSyncTime = "Ready";
        public string LastSyncTime { get => _lastSyncTime; set => SetProperty(ref _lastSyncTime, value); }

        // --- 3. 로컬 캐시 (필터링 전 원본) ---
        private List<DockerContainer> _rawContainers = new();
        private List<DockerVolume> _rawVolumes = new();
        private List<DockerNetworkGroup> _rawNetworks = new();
        private List<DockerImage> _rawImages = new();
        private Dictionary<string, int> _usageStats = new();

        // --- 4. 명령(Commands) ---
        public ICommand DeleteContainerItemCommand { get; }
        public ICommand DeleteVolumeItemCommand { get; }
        public ICommand DeleteNetworkItemCommand { get; }
        public ICommand DeleteImageCommand { get; }
        public ICommand SearchHubCommand { get; }
        public ICommand PullImageCommand { get; }

        public ResourceExplorerViewModel(MainViewModel mainVm, IDockerService defaultDockerService, IDialogService dialogService)
        {
            _mainVm = mainVm;
            _defaultDockerService = defaultDockerService;
            _dialogService = dialogService;

            DeleteContainerItemCommand = new AsyncRelayCommand(DeleteContainerItemAsync);
            DeleteVolumeItemCommand = new AsyncRelayCommand(DeleteVolumeItemAsync);
            DeleteNetworkItemCommand = new AsyncRelayCommand(DeleteNetworkItemAsync);
            DeleteImageCommand = new AsyncRelayCommand(DeleteImageAsync);
            SearchHubCommand = new AsyncRelayCommand(ExecuteSearchHubAsync);
            PullImageCommand = new AsyncRelayCommand(ExecutePullImageAsync);

            RefreshTemplates();
        }

        // --- 5. 비즈니스 로직 ---

        public async Task SyncWithDockerEngineAsync()
        {
            var sheet = _mainVm.ActiveSheet;
            var service = sheet?.DockerService ?? _defaultDockerService;
            if (service == null) return;

            // 로컬인 경우 프로세스 체크
            if (sheet?.Profile.Type == EndpointType.Local && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!DockerServiceHelper.IsDockerRunning())
                {
                    LastSyncTime = "Docker stopped";
                    return;
                }
            }

            try
            {
                if (!await ((ISystemService)service).PingAsync()) return;

                _rawContainers = await ((IContainerService)service).GetContainersAsync();
                _rawVolumes = await ((IVolumeService)service).GetVolumesAsync();
                _rawNetworks = await ((INetworkService)service).GetNetworksAsync();
                _rawImages = await ((IImageService)service).GetImagesAsync();

                UpdateAvailableItems();
                UpdateDiagramConnectionStates();
                LastSyncTime = $"Last updated: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                LastSyncTime = "Sync failed";
                Debug.WriteLine($"[ResourceExplorer] Sync Error: {ex.Message}");
            }
        }

        public void UpdateAvailableItems()
        {
            if (_mainVm.Sheets == null) return;

            // 1. 모든 시트에서 사용 중인 요소들 긁어모으기
            var allNodes = new List<NodeViewModel>();
            foreach (var sheet in _mainVm.Sheets)
            {
                allNodes.AddRange(sheet.Nodes);
                foreach (var group in sheet.Groups)
                {
                    allNodes.AddRange(group.ContainedNodes);
                }
            }

            var usedContainerIds = new HashSet<string>(allNodes.Where(n => n.Type == NodeType.Container).Select(n => n.ContainerId));
            var usedVolumeNames = new HashSet<string>(allNodes.Where(n => n.Type == NodeType.Volume).Select(n => n.Name));

            var usedNetworkNames = new HashSet<string>();
            foreach (var sheet in _mainVm.Sheets)
            {
                foreach (var grp in sheet.Groups)
                {
                    if (grp.Type == GroupType.Network)
                        usedNetworkNames.Add(grp.Title);
                }
            }

            // 2. 필터링 로직
            var filteredContainers = _rawContainers
                .Where(c => !usedContainerIds.Contains(c.Id))
                .Where(c => string.IsNullOrEmpty(ContainerSearchText) || c.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingContainers, filteredContainers, c => c.Id);

            var filteredVolumes = _rawVolumes
                .Where(v => !usedVolumeNames.Contains(v.Name))
                .Where(v => string.IsNullOrEmpty(VolumeSearchText) || v.Name.Contains(VolumeSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingVolumes, filteredVolumes, v => v.Name);

            var defaultNetworks = new HashSet<string> { "bridge", "host", "none" };
            var filteredNetworks = _rawNetworks
                .Where(n => !usedNetworkNames.Contains(n.Name))
                .Where(n => !defaultNetworks.Contains(n.Name))
                .Where(n => string.IsNullOrEmpty(NetworkSearchText) || n.Name.Contains(NetworkSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingNetworks, filteredNetworks, n => n.Id);

            var filteredImages = _rawImages
                .Where(i => string.IsNullOrEmpty(ImageSearchText) || i.Repository.Contains(ImageSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(LocalImages, filteredImages, i => i.Id);
        }

        public void UpdateDiagramConnectionStates()
        {
            if (_mainVm.Sheets == null) return;

            var containerIds = _rawContainers
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => c.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var volumeNames = _rawVolumes
                .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                .Select(v => v.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var networkIds = _rawNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Id))
                .Select(n => n.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var networkNames = _rawNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Name))
                .Select(n => n.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var sheet in _mainVm.Sheets)
            {
                foreach (var node in sheet.Nodes)
                {
                    node.IsDockerConnected = node.Type switch
                    {
                        NodeType.Container => !string.IsNullOrWhiteSpace(node.ContainerId) && containerIds.Contains(node.ContainerId),
                        NodeType.Volume => volumeNames.Contains(node.Name),
                        NodeType.Internet => true,
                        _ => false
                    };
                }

                foreach (var group in sheet.Groups)
                {
                    group.IsDockerConnected = group.Type != GroupType.Network ||
                                              (!string.IsNullOrWhiteSpace(group.Id) && networkIds.Contains(group.Id)) ||
                                              networkNames.Contains(group.Title);
                }
            }
        }

        // --- 💡 여기서부터 생략되었던 핵심 삭제/통신 로직들입니다! ---

        private async Task DeleteContainerItemAsync(object? param)
        {
            if (param is DockerContainer c)
            {
                if (_dialogService.ShowConfirm($"컨테이너 '{c.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        var service = (IContainerService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                        await service.RemoveContainerAsync(c.Id);
                        await SyncWithDockerEngineAsync();
                    }
                    catch (Exception ex) { _dialogService.ShowMessage($"삭제 실패: {ex.Message}"); }
                }
            }
        }

        private async Task DeleteVolumeItemAsync(object? param)
        {
            if (param is DockerVolume v)
            {
                if (_dialogService.ShowConfirm($"볼륨 '{v.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        var service = (IVolumeService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                        await service.RemoveVolumeAsync(v.Name);
                        await SyncWithDockerEngineAsync();
                    }
                    catch (Exception ex) { _dialogService.ShowMessage($"볼륨 삭제 실패: {ex.Message}"); }
                }
            }
        }

        private async Task DeleteNetworkItemAsync(object? param)
        {
            if (param is DockerNetworkGroup n)
            {
                if (_dialogService.ShowConfirm($"네트워크 '{n.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        var service = (INetworkService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                        await service.RemoveNetworkAsync(n.Id);
                        await SyncWithDockerEngineAsync();
                    }
                    catch (Exception ex) { _dialogService.ShowMessage($"네트워크 삭제 실패: {ex.Message}"); }
                }
            }
        }

        private async Task DeleteImageAsync(object? parameter)
        {
            if (parameter is DockerImage img)
            {
                if (_dialogService.ShowConfirm($"이미지 '{img.Repository}'를 삭제하시겠습니까?", "이미지 삭제"))
                {
                    var service = (IImageService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                    try
                    {
                        await service.DeleteImageAsync(img.Id, force: false);
                        LocalImages.Remove(img);
                    }
                    catch (Exception ex)
                    {
                        if (_dialogService.ShowConfirm($"삭제 실패: 이미지가 사용 중일 수 있습니다.\n강제로 삭제하시겠습니까?\n({ex.Message})", "강제 삭제 확인"))
                        {
                            try
                            {
                                await service.DeleteImageAsync(img.Id, force: true);
                                LocalImages.Remove(img);
                            }
                            catch (Exception forceEx) { _dialogService.ShowMessage($"강제 삭제 실패: {forceEx.Message}"); }
                        }
                    }
                }
            }
        }

        private async Task ExecuteSearchHubAsync(object? parameter)
        {
            if (string.IsNullOrWhiteSpace(HubSearchTerm)) return;
            IsSearchingHub = true;
            HubSearchResults.Clear();
            try
            {
                var service = (IImageService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                var results = await service.SearchImagesAsync(HubSearchTerm);
                foreach (var res in results) HubSearchResults.Add(res);
            }
            catch (Exception ex) { _dialogService.ShowError($"검색 중 오류가 발생했습니다: {ex.Message}", "검색 실패"); }
            finally { IsSearchingHub = false; }
        }

        private async Task ExecutePullImageAsync(object? parameter)
        {
            if (parameter is not ImageSearchResponse selectedImage) return;
            string targetImage = selectedImage.Name;
            string targetTag = "latest";

            if (!_dialogService.ShowConfirm($"[{targetImage}:{targetTag}] 이미지를 다운로드하시겠습니까?", "이미지 Pull")) return;

            IsPulling = true;
            PullProgressValue = 0;
            PullProgressMessage = "다운로드 준비 중...";

            try
            {
                var service = (IImageService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                var progress = new Progress<JSONMessage>(message =>
                {
                    PullProgressMessage = $"{message.Status} {message.ProgressMessage}";
                    if (message.Progress != null && message.Progress.Total > 0)
                        PullProgressValue = ((double)message.Progress.Current / message.Progress.Total) * 100;
                });

                await service.PullImageWithProgressAsync(targetImage, targetTag, progress);

                PullProgressValue = 100;
                PullProgressMessage = "다운로드 완료!";
                _dialogService.ShowInfo($"[{targetImage}] 이미지 다운로드가 완료되었습니다.", "Pull 성공");

                await SyncWithDockerEngineAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"이미지 다운로드 실패: {ex.Message}", "Pull 오류");
                PullProgressMessage = "오류 발생";
            }
            finally { IsPulling = false; }
        }

        public void RegisterTemplateUsage(string imageName)
        {
            if (string.IsNullOrEmpty(imageName)) return;
            if (!_usageStats.ContainsKey(imageName)) _usageStats[imageName] = 0;
            _usageStats[imageName]++;
            RefreshTemplates();
        }

        private void RefreshTemplates()
        {
            Templates.Clear();
            Templates.Add(new TemplateItem { Name = "Nginx Web", Image = "nginx:latest", Type = NodeType.Container, IsDefault = true });
            Templates.Add(new TemplateItem { Name = "Redis DB", Image = "redis:alpine", Type = NodeType.Container, IsDefault = true });
            Templates.Add(new TemplateItem { Name = "Ubuntu OS", Image = "ubuntu:latest", Type = NodeType.Container, IsDefault = true });

            var frequents = _usageStats.OrderByDescending(kv => kv.Value).Take(3);
            foreach (var f in frequents) Templates.Add(new TemplateItem { Name = f.Key, Image = f.Key, Type = NodeType.Container, IsDefault = false });
        }

        // 화면 깜빡임 방지용 스마트 컬렉션 동기화 로직
        private void SyncCollection<T>(ObservableCollection<T> uiCollection, List<T> newItems, Func<T, string> keySelector)
        {
            var newKeys = new HashSet<string>(newItems.Select(keySelector));
            var toRemove = uiCollection.Where(item => !newKeys.Contains(keySelector(item))).ToList();

            foreach (var item in toRemove) uiCollection.Remove(item);

            var currentKeys = new HashSet<string>(uiCollection.Select(keySelector));
            foreach (var item in newItems)
            {
                if (!currentKeys.Contains(keySelector(item)))
                {
                    uiCollection.Add(item);
                }
            }

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
    }
}
