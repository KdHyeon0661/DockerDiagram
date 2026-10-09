using DockerDiagram.Infrastructure;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Docker.DotNet.Models;
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
        public ObservableCollection<StackTemplateDefinition> StackTemplates { get; } = new();
        public ObservableCollection<DockerContainer> ExistingContainers { get; } = new();
        public ObservableCollection<DockerVolume> ExistingVolumes { get; } = new();
        public ObservableCollection<DockerNetworkGroup> ExistingNetworks { get; } = new();
        public ObservableCollection<DockerImage> LocalImages { get; } = new();
        public ObservableCollection<DockerComposeProject> ComposeProjects { get; } = new();
        public ObservableCollection<DockerSwarmNode> SwarmNodes { get; } = new();
        public ObservableCollection<SwarmDataResourceSnapshot> SwarmSecrets { get; } = new();
        public ObservableCollection<SwarmDataResourceSnapshot> SwarmConfigs { get; } = new();
        public ObservableCollection<DockerKubernetesNode> KubernetesNodes { get; } = new();
        public ObservableCollection<DockerContainer> KubernetesDeployments { get; } = new();
        public ObservableCollection<DockerContainer> KubernetesReplicaSets { get; } = new();
        public ObservableCollection<DockerContainer> KubernetesServices { get; } = new();
        public ObservableCollection<DockerContainer> KubernetesConfigMaps { get; } = new();
        public ObservableCollection<DockerContainer> KubernetesSecrets { get; } = new();
        public ObservableCollection<DockerContainer> KubernetesIngresses { get; } = new();
        public ObservableCollection<DockerContainer> KubernetesPersistentVolumeClaims { get; } = new();
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
        public bool IsSwarmRuntime => _mainVm.ActiveSheet?.RuntimeKind == RuntimeKind.DockerSwarm;
        public bool IsKubernetesRuntime => _mainVm.ActiveSheet?.RuntimeKind == RuntimeKind.Kubernetes;
        public bool IsDockerRuntime => !IsSwarmRuntime && !IsKubernetesRuntime;
        public string ResourceHeader => IsSwarmRuntime ? "Swarm Resources" : IsKubernetesRuntime ? "Kubernetes Resources" : "Docker Resources";
        public string PrimaryResourceLabel => IsSwarmRuntime ? "Services" : IsKubernetesRuntime ? "Pods" : "Containers";
        public string PrimaryResourceSearchPlaceholder => IsSwarmRuntime ? "Search services" : IsKubernetesRuntime ? "Search pods" : "Search containers";
        public int KubernetesResourceCount =>
            KubernetesDeployments.Count +
            KubernetesReplicaSets.Count +
            KubernetesServices.Count +
            KubernetesConfigMaps.Count +
            KubernetesSecrets.Count +
            KubernetesIngresses.Count +
            KubernetesPersistentVolumeClaims.Count;

        // --- 3. 로컬 캐시 (필터링 전 원본) ---
        private List<DockerContainer> _rawContainers = new();
        private List<DockerVolume> _rawVolumes = new();
        private List<DockerNetworkGroup> _rawNetworks = new();
        private List<DockerImage> _rawImages = new();
        private List<DockerComposeProject> _rawComposeProjects = new();
        private List<SwarmDataResourceSnapshot> _rawSwarmSecrets = new();
        private List<SwarmDataResourceSnapshot> _rawSwarmConfigs = new();
        private List<DockerContainer> _rawKubernetesDeployments = new();
        private List<DockerContainer> _rawKubernetesReplicaSets = new();
        private List<DockerContainer> _rawKubernetesServices = new();
        private List<DockerContainer> _rawKubernetesConfigMaps = new();
        private List<DockerContainer> _rawKubernetesSecrets = new();
        private List<DockerContainer> _rawKubernetesIngresses = new();
        private List<DockerContainer> _rawKubernetesPersistentVolumeClaims = new();
        private Dictionary<string, int> _usageStats = new();
        private int _availableItemsUpdateDeferral;
        private bool _availableItemsUpdatePending;

        // --- 4. 명령(Commands) ---
        public ICommand DeleteContainerItemCommand { get; }
        public ICommand DeleteVolumeItemCommand { get; }
        public ICommand DeleteNetworkItemCommand { get; }
        public ICommand DeleteSwarmDataResourceItemCommand { get; }
        public ICommand DeleteImageCommand { get; }
        public ICommand TagImageCommand { get; }
        public ICommand PushImageCommand { get; }
        public ICommand SaveImageCommand { get; }
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
            DeleteSwarmDataResourceItemCommand = new AsyncRelayCommand(DeleteSwarmDataResourceItemAsync);
            DeleteImageCommand = new AsyncRelayCommand(DeleteImageAsync);
            TagImageCommand = new AsyncRelayCommand(TagImageAsync);
            PushImageCommand = new AsyncRelayCommand(PushImageAsync);
            SaveImageCommand = new AsyncRelayCommand(SaveImageAsync);
            SearchHubCommand = new AsyncRelayCommand(ExecuteSearchHubAsync);
            PullImageCommand = new AsyncRelayCommand(ExecutePullImageAsync);

            RefreshTemplates();
            foreach (var template in StackTemplateCatalog.LoadBuiltIn())
                StackTemplates.Add(template);
        }

        // --- 5. 비즈니스 로직 ---

        internal async Task SyncCoreWithDockerEngineAsync(
            SheetViewModel? sheet,
            IDockerService service)
        {
            if (!IsSyncContextCurrent(sheet, service)) return;
            RaiseRuntimeLabelsChanged();

            bool usesDockerEngine = sheet?.RuntimeKind != RuntimeKind.Kubernetes;

            // 로컬 Docker 런타임인 경우 프로세스 체크
            if (usesDockerEngine && sheet?.Profile.Type == EndpointType.Local && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!DockerServiceHelper.IsDockerRunning())
                {
                    LastSyncTime = "Docker stopped";
                    MarkRuntimeUnavailable(sheet, "Docker engine is not running. This sheet is shown as an offline snapshot.");
                    return;
                }
            }

            try
            {
                if (sheet?.RuntimeKind == RuntimeKind.Kubernetes)
                {
                    try
                    {
                        var kubernetesService = (IKubernetesService)service;
                        List<DockerContainer> pods = await kubernetesService.GetKubernetesPodsAsync();
                        List<DockerKubernetesNode> nodes = await kubernetesService.GetKubernetesNodesAsync();
                        List<DockerContainer> deployments = await kubernetesService.GetKubernetesDeploymentsAsync();
                        List<DockerContainer> replicaSets = await kubernetesService.GetKubernetesReplicaSetsAsync();
                        List<DockerContainer> services = await kubernetesService.GetKubernetesServicesAsync();
                        List<DockerContainer> configMaps = await kubernetesService.GetKubernetesConfigMapsAsync();
                        List<DockerContainer> secrets = await kubernetesService.GetKubernetesSecretsAsync();
                        List<DockerContainer> ingresses = await kubernetesService.GetKubernetesIngressesAsync();
                        List<DockerContainer> claims = await kubernetesService.GetKubernetesPersistentVolumeClaimsAsync();

                        if (!IsSyncContextCurrent(sheet, service)) return;

                        _rawContainers = pods;
                        SyncCollection(KubernetesNodes, nodes, node => node.Id);
                        _rawKubernetesDeployments = deployments;
                        _rawKubernetesReplicaSets = replicaSets;
                        _rawKubernetesServices = services;
                        _rawKubernetesConfigMaps = configMaps;
                        _rawKubernetesSecrets = secrets;
                        _rawKubernetesIngresses = ingresses;
                        _rawKubernetesPersistentVolumeClaims = claims;
                    }
                    catch (Exception kubernetesEx)
                    {
                        if (!IsSyncContextCurrent(sheet, service)) return;

                        MarkRuntimeUnavailable(
                            sheet,
                            "Kubernetes runtime is unavailable. Saved resources remain visible as an offline snapshot; live controls are disabled.");
                        _rawContainers.Clear();
                        _rawVolumes.Clear();
                        _rawNetworks.Clear();
                        _rawImages.Clear();
                        ComposeProjects.Clear();
                        SwarmNodes.Clear();
                        ClearSwarmDataResources();
                        ClearKubernetesResourceCollections(clearNodes: true);
                        UpdateAvailableItems();
                        LastSyncTime = "Kubernetes unavailable";
                        Debug.WriteLine($"[ResourceExplorer] Kubernetes Sync Error: {kubernetesEx.Message}");
                        return;
                    }

                    _rawVolumes.Clear();
                    _rawNetworks.Clear();
                    _rawImages.Clear();
                    ComposeProjects.Clear();
                    SwarmNodes.Clear();
                    ClearSwarmDataResources();
                }
                else if (!await ((ISystemService)service).PingAsync())
                {
                    if (!IsSyncContextCurrent(sheet, service)) return;
                    MarkRuntimeUnavailable(sheet, "Docker engine is not reachable. This sheet is shown as an offline snapshot.");
                    return;
                }
                else if (sheet?.RuntimeKind == RuntimeKind.DockerSwarm)
                {
                    if (!IsSyncContextCurrent(sheet, service)) return;

                    var failedResources = new List<string>();
                    var swarmService = (ISwarmService)service;
                    var networkService = (INetworkService)service;
                    var volumeService = (IVolumeService)service;
                    Task<List<DockerContainer>> servicesTask = swarmService.GetSwarmServicesAsync();
                    Task<List<DockerSwarmNode>> nodesTask = swarmService.GetSwarmNodesAsync();
                    Task<List<DockerNetworkGroup>> networksTask = networkService.GetNetworksAsync();
                    Task<List<DockerVolume>> volumesTask = volumeService.GetVolumesAsync();
                    ISwarmDataResourceQueryService? dataResourceQuery = service as ISwarmDataResourceQueryService;
                    Task<IReadOnlyList<SwarmDataResourceSnapshot>>? secretsTask =
                        dataResourceQuery?.GetSwarmDataResourcesAsync(SwarmDataResourceKind.Secret);
                    Task<IReadOnlyList<SwarmDataResourceSnapshot>>? configsTask =
                        dataResourceQuery?.GetSwarmDataResourcesAsync(SwarmDataResourceKind.Config);
                    var containers = new List<DockerContainer>();
                    var nodes = new List<DockerSwarmNode>();
                    var networks = new List<DockerNetworkGroup>();
                    var volumes = new List<DockerVolume>();
                    var secrets = new List<SwarmDataResourceSnapshot>();
                    var configs = new List<SwarmDataResourceSnapshot>();

                    try
                    {
                        containers = await servicesTask;
                    }
                    catch (Exception swarmEx)
                    {
                        failedResources.Add("Services");
                        Debug.WriteLine($"[ResourceExplorer] Swarm Services Sync Error: {swarmEx.Message}");
                    }

                    try
                    {
                        nodes = await nodesTask;
                    }
                    catch (Exception nodeEx)
                    {
                        failedResources.Add("Nodes");
                        Debug.WriteLine($"[ResourceExplorer] Swarm Nodes Sync Error: {nodeEx.Message}");
                    }

                    try
                    {
                        networks = (await networksTask)
                            .Where(SwarmResourceFilter.IsOverlayNetwork)
                            .ToList();
                    }
                    catch (Exception networkEx)
                    {
                        failedResources.Add("Overlay Networks");
                        Debug.WriteLine($"[ResourceExplorer] Swarm Overlay Networks Sync Error: {networkEx.Message}");
                    }

                    try
                    {
                        volumes = await volumesTask;
                    }
                    catch (Exception volumeEx)
                    {
                        failedResources.Add("Manager Host Volumes");
                        Debug.WriteLine($"[ResourceExplorer] Swarm Manager Volumes Sync Error: {volumeEx.Message}");
                    }

                    if (dataResourceQuery != null && secretsTask != null && configsTask != null)
                    {
                        try
                        {
                            secrets = (await secretsTask).ToList();
                        }
                        catch (Exception secretEx)
                        {
                            failedResources.Add("Secrets");
                            Debug.WriteLine($"[ResourceExplorer] Swarm Secrets Sync Error: {secretEx.Message}");
                        }

                        try
                        {
                            configs = (await configsTask).ToList();
                        }
                        catch (Exception configEx)
                        {
                            failedResources.Add("Configs");
                            Debug.WriteLine($"[ResourceExplorer] Swarm Configs Sync Error: {configEx.Message}");
                        }
                    }
                    else
                    {
                        failedResources.Add("Secrets/Configs");
                    }

                    if (!IsSyncContextCurrent(sheet, service)) return;

                    _rawContainers = containers;
                    SyncCollection(SwarmNodes, nodes, node => node.Id);
                    _rawNetworks = networks;
                    _rawVolumes = volumes;
                    _rawSwarmSecrets = secrets;
                    _rawSwarmConfigs = configs;

                    _rawImages.Clear();
                    ClearKubernetesResourceCollections(clearNodes: true);

                    if (failedResources.Contains("Services") && failedResources.Contains("Nodes"))
                    {
                        MarkRuntimeUnavailable(
                            sheet,
                            "Swarm Manager APIs are unavailable. Saved services remain visible as an offline snapshot; live controls are disabled.");
                    }
                    else
                    {
                        ClearRuntimeUnavailable(sheet);
                    }

                    UpdateComposeProjects();
                    UpdateAvailableItems();
                    UpdateDiagramConnectionStates();
                    LastSyncTime = failedResources.Count == 0
                        ? $"Last updated: {DateTime.Now:HH:mm:ss}"
                        : $"Partial sync: {string.Join(", ", failedResources)}";
                    return;
                }
                else
                {
                    if (!IsSyncContextCurrent(sheet, service)) return;

                    List<DockerContainer> containers = await ((IContainerService)service).GetContainersAsync();
                    List<DockerVolume> volumes = await ((IVolumeService)service).GetVolumesAsync();
                    List<DockerNetworkGroup> networks = await ((INetworkService)service).GetNetworksAsync();
                    List<DockerImage> images = await ((IImageService)service).GetImagesAsync();

                    if (!IsSyncContextCurrent(sheet, service)) return;

                    ClearRuntimeUnavailable(sheet);
                    _rawContainers = containers;
                    _rawVolumes = volumes;
                    _rawNetworks = networks;
                    _rawImages = images;
                    SwarmNodes.Clear();
                    ClearSwarmDataResources();
                    ClearKubernetesResourceCollections(clearNodes: true);
                }

                ClearRuntimeUnavailable(sheet);
                UpdateComposeProjects();
                UpdateAvailableItems();
                UpdateDiagramConnectionStates();
                LastSyncTime = $"Last updated: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                if (!IsSyncContextCurrent(sheet, service)) return;

                LastSyncTime = "Sync failed";
                MarkRuntimeUnavailable(sheet, "Runtime sync failed. This sheet is shown as an offline snapshot.");
                Debug.WriteLine($"[ResourceExplorer] Sync Error: {ex.Message}");
            }
        }

        private bool IsSyncContextCurrent(SheetViewModel? sheet, IDockerService service) =>
            ReferenceEquals(sheet, _mainVm.ActiveSheet) &&
            ReferenceEquals(service, sheet?.DockerService ?? _defaultDockerService);

        private static void MarkRuntimeUnavailable(SheetViewModel? sheet, string message) =>
            RuntimeAvailabilityMarker.MarkUnavailable(sheet, message);

        private static void ClearRuntimeUnavailable(SheetViewModel? sheet)
        {
            if (sheet == null) return;

            sheet.IsRuntimeUnavailable = false;
            sheet.RuntimeStatusMessage = string.Empty;
        }

        public void UpdateAvailableItems()
        {
            if (_availableItemsUpdateDeferral > 0)
            {
                _availableItemsUpdatePending = true;
                return;
            }

            if (_mainVm.Sheets == null) return;
            RaiseRuntimeLabelsChanged();

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
            var usedVolumeNames = new HashSet<string>(
                allNodes
                    .Where(n => n.Type == NodeType.Volume)
                    .SelectMany(n => new[] { n.Name, n.EffectiveVolumeName })
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

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
                .Where(c => !IsDockerRuntime || !c.IsComposeManaged)
                .Where(c => !usedContainerIds.Contains(c.Id))
                .Where(c => string.IsNullOrEmpty(ContainerSearchText) || c.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingContainers, filteredContainers, c => c.Id);

            var filteredVolumes = _rawVolumes
                .Where(v => !IsDockerRuntime || !v.IsComposeManaged)
                .Where(v => !usedVolumeNames.Contains(v.Name))
                .Where(v => string.IsNullOrEmpty(VolumeSearchText) || v.Name.Contains(VolumeSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingVolumes, filteredVolumes, v => v.Name);

            var defaultNetworks = new HashSet<string> { "bridge", "host", "none" };
            var filteredNetworks = _rawNetworks
                .Where(n => !IsDockerRuntime || !n.IsComposeManaged)
                .Where(n => !usedNetworkNames.Contains(n.Name))
                .Where(n => !defaultNetworks.Contains(n.Name))
                .Where(n => string.IsNullOrEmpty(NetworkSearchText) || n.Name.Contains(NetworkSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(ExistingNetworks, filteredNetworks, n => n.Id);

            var usedSwarmDataIds = new HashSet<string>(
                allNodes
                    .Where(node => node.ResourceKind is RuntimeResourceKind.SwarmSecret or RuntimeResourceKind.SwarmConfig)
                    .Select(node => node.ContainerId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            var usedSwarmSecretNames = new HashSet<string>(
                allNodes.Where(node => node.ResourceKind == RuntimeResourceKind.SwarmSecret).Select(node => node.Name),
                StringComparer.OrdinalIgnoreCase);
            var usedSwarmConfigNames = new HashSet<string>(
                allNodes.Where(node => node.ResourceKind == RuntimeResourceKind.SwarmConfig).Select(node => node.Name),
                StringComparer.OrdinalIgnoreCase);
            SyncCollection(
                SwarmSecrets,
                _rawSwarmSecrets.Where(resource =>
                    !usedSwarmDataIds.Contains(resource.Id) && !usedSwarmSecretNames.Contains(resource.Name)).ToList(),
                resource => resource.Id);
            SyncCollection(
                SwarmConfigs,
                _rawSwarmConfigs.Where(resource =>
                    !usedSwarmDataIds.Contains(resource.Id) && !usedSwarmConfigNames.Contains(resource.Name)).ToList(),
                resource => resource.Id);

            var filteredImages = _rawImages
                .Where(i => string.IsNullOrEmpty(ImageSearchText) || i.Repository.Contains(ImageSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(LocalImages, filteredImages, i => i.Id);

            SyncCollection(
                KubernetesDeployments,
                FilterKubernetesResources(_rawKubernetesDeployments, usedContainerIds),
                resource => resource.Id);
            SyncCollection(
                KubernetesReplicaSets,
                FilterKubernetesResources(_rawKubernetesReplicaSets, usedContainerIds),
                resource => resource.Id);
            SyncCollection(
                KubernetesServices,
                FilterKubernetesResources(_rawKubernetesServices, usedContainerIds),
                resource => resource.Id);
            SyncCollection(
                KubernetesConfigMaps,
                FilterKubernetesResources(_rawKubernetesConfigMaps, usedContainerIds),
                resource => resource.Id);
            SyncCollection(
                KubernetesSecrets,
                FilterKubernetesResources(_rawKubernetesSecrets, usedContainerIds),
                resource => resource.Id);
            SyncCollection(
                KubernetesIngresses,
                FilterKubernetesResources(_rawKubernetesIngresses, usedContainerIds),
                resource => resource.Id);
            SyncCollection(
                KubernetesPersistentVolumeClaims,
                FilterKubernetesResources(_rawKubernetesPersistentVolumeClaims, usedContainerIds),
                resource => resource.Id);
            OnPropertyChanged(nameof(KubernetesResourceCount));
            UpdateAvailableComposeProjects();
        }

        private List<DockerContainer> FilterKubernetesResources(IEnumerable<DockerContainer> resources, HashSet<string> usedIds)
        {
            return resources
                .Where(resource => !usedIds.Contains(resource.Id))
                .Where(resource => string.IsNullOrWhiteSpace(ContainerSearchText) ||
                                   resource.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase) ||
                                   resource.KubernetesKind.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void ClearKubernetesResourceCollections(bool clearNodes)
        {
            if (clearNodes)
                KubernetesNodes.Clear();

            _rawKubernetesDeployments.Clear();
            _rawKubernetesReplicaSets.Clear();
            _rawKubernetesServices.Clear();
            _rawKubernetesConfigMaps.Clear();
            _rawKubernetesSecrets.Clear();
            _rawKubernetesIngresses.Clear();
            _rawKubernetesPersistentVolumeClaims.Clear();

            KubernetesDeployments.Clear();
            KubernetesReplicaSets.Clear();
            KubernetesServices.Clear();
            KubernetesConfigMaps.Clear();
            KubernetesSecrets.Clear();
            KubernetesIngresses.Clear();
            KubernetesPersistentVolumeClaims.Clear();
            OnPropertyChanged(nameof(KubernetesResourceCount));
        }

        private void ClearSwarmDataResources()
        {
            _rawSwarmSecrets.Clear();
            _rawSwarmConfigs.Clear();
            SwarmSecrets.Clear();
            SwarmConfigs.Clear();
        }

        public IReadOnlyList<SwarmDataResourceSnapshot> GetSwarmDataResources(SwarmDataResourceKind kind) =>
            kind == SwarmDataResourceKind.Secret
                ? _rawSwarmSecrets.ToArray()
                : _rawSwarmConfigs.ToArray();

        internal IReadOnlyList<DockerContainer> GetSwarmServicesSnapshot() => _rawContainers.ToArray();
        internal IReadOnlyList<DockerNetworkGroup> GetSwarmNetworksSnapshot() => _rawNetworks.ToArray();

        internal IDisposable DeferAvailableItemsUpdates()
        {
            _availableItemsUpdateDeferral++;
            return new AvailableItemsUpdateScope(this);
        }

        private void EndAvailableItemsUpdateDeferral()
        {
            if (_availableItemsUpdateDeferral == 0) return;
            _availableItemsUpdateDeferral--;
            if (_availableItemsUpdateDeferral != 0 || !_availableItemsUpdatePending) return;

            _availableItemsUpdatePending = false;
            UpdateAvailableItems();
        }

        private sealed class AvailableItemsUpdateScope : IDisposable
        {
            private ResourceExplorerViewModel? _owner;

            public AvailableItemsUpdateScope(ResourceExplorerViewModel owner) => _owner = owner;

            public void Dispose()
            {
                ResourceExplorerViewModel? owner = Interlocked.Exchange(ref _owner, null);
                owner?.EndAvailableItemsUpdateDeferral();
            }
        }

        private void UpdateComposeProjects()
        {
            if (!IsDockerRuntime)
            {
                _rawComposeProjects.Clear();
                ComposeProjects.Clear();
                return;
            }

            var expandedProjectKeys = _rawComposeProjects
                .Where(project => project.IsDetailsExpanded)
                .Select(project => project.IdentityKey)
                .ToHashSet(StringComparer.Ordinal);

            var projectGroups = _rawContainers.Cast<DockerResource>()
                .Concat(_rawVolumes)
                .Concat(_rawNetworks)
                .Where(resource => resource.IsComposeManaged)
                .Where(resource => resource is not DockerContainer { IsComposeOneOff: true })
                .GroupBy(
                    resource => $"{NormalizeProjectSource(resource.ProjectSource)}\u001F{resource.ComposeProjectName}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.First().ComposeProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => NormalizeProjectSource(group.First().ProjectSource), StringComparer.OrdinalIgnoreCase)
                .ToList();

            _rawComposeProjects.Clear();
            foreach (var projectGroup in projectGroups)
            {
                var sourceResource = projectGroup.First();
                string projectName = sourceResource.ComposeProjectName;
                string source = NormalizeProjectSource(sourceResource.ProjectSource);

                var containers = _rawContainers
                    .Where(container => IsSameProject(container, projectName, source))
                    .Where(container => !container.IsComposeOneOff)
                    .OrderBy(container => container.ComposeServiceName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(container => container.ComposeContainerNumber)
                    .ToList();
                var volumes = _rawVolumes
                    .Where(volume => IsSameProject(volume, projectName, source))
                    .OrderBy(volume => volume.ComposeResourceName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var networks = _rawNetworks
                    .Where(network => IsSameProject(network, projectName, source))
                    .OrderBy(network => network.ComposeResourceName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var projectSource = containers.Cast<DockerResource>()
                    .Concat(volumes)
                    .Concat(networks)
                    .FirstOrDefault(resource => !string.IsNullOrWhiteSpace(resource.ComposeWorkingDirectory))
                    ?? containers.Cast<DockerResource>().Concat(volumes).Concat(networks).First();

                var project = new DockerComposeProject
                {
                    Name = projectName,
                    WorkingDirectory = projectSource.ComposeWorkingDirectory,
                    ConfigFiles = projectSource.ComposeConfigFiles,
                    Source = source,
                    Containers = containers,
                    Volumes = volumes,
                    Networks = networks
                };
                project.IsDetailsExpanded = expandedProjectKeys.Contains(project.IdentityKey);
                _rawComposeProjects.Add(project);
            }

            UpdateAvailableComposeProjects();
        }

        private void UpdateAvailableComposeProjects()
        {
            if (!IsDockerRuntime)
            {
                ComposeProjects.Clear();
                return;
            }

            var usedProjectIdentities = _mainVm.Sheets
                .SelectMany(sheet =>
                    sheet.Nodes.Select(node => node.ComposeProjectIdentity)
                        .Concat(sheet.Groups.Select(group => group.ComposeProjectIdentity)))
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .ToHashSet(StringComparer.Ordinal);

            // 이전 저장 파일은 프로젝트 고유키가 없을 수 있으므로 프로젝트명으로 호환 판정합니다.
            var usedLegacyProjectNames = _mainVm.Sheets
                .SelectMany(sheet => sheet.Nodes)
                .Where(node => string.IsNullOrWhiteSpace(node.ComposeProjectIdentity))
                .Select(node => node.ComposeProjectName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var availableProjects = _rawComposeProjects
                .Where(project => !usedProjectIdentities.Contains(project.IdentityKey))
                .Where(project => !usedLegacyProjectNames.Contains(project.Name))
                .ToList();

            ComposeProjects.Clear();
            foreach (DockerComposeProject project in availableProjects)
                ComposeProjects.Add(project);
        }

        private static bool IsSameProject(DockerResource resource, string projectName, string source)
        {
            return resource.ComposeProjectName.Equals(projectName, StringComparison.OrdinalIgnoreCase) &&
                   NormalizeProjectSource(resource.ProjectSource).Equals(source, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeProjectSource(string source)
        {
            return string.IsNullOrWhiteSpace(source) ? "Compose" : source;
        }


        public void UpdateDiagramConnectionStates()
        {
            if (_mainVm.Sheets == null) return;

            var containerIds = _rawContainers
                .Concat(_rawKubernetesDeployments)
                .Concat(_rawKubernetesReplicaSets)
                .Concat(_rawKubernetesServices)
                .Concat(_rawKubernetesConfigMaps)
                .Concat(_rawKubernetesSecrets)
                .Concat(_rawKubernetesIngresses)
                .Concat(_rawKubernetesPersistentVolumeClaims)
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
                    if (node.Type == NodeType.Container &&
                        !node.IsSwarmService &&
                        !node.IsKubernetesResource)
                    {
                        DockerContainer? containerMatch = _rawContainers.FirstOrDefault(container =>
                            !string.IsNullOrWhiteSpace(node.ContainerId) &&
                            container.Id.Equals(node.ContainerId, StringComparison.OrdinalIgnoreCase));

                        if (containerMatch == null &&
                            (string.IsNullOrWhiteSpace(node.ContainerId) ||
                             !containerIds.Contains(node.ContainerId)))
                        {
                            containerMatch = FindComposeContainer(node);
                        }

                        if (containerMatch != null)
                        {
                            node.ContainerId = containerMatch.Id;
                            node.ComposeProjectName = containerMatch.ComposeProjectName;
                            node.ComposeServiceName = containerMatch.ComposeServiceName;
                            node.ComposeContainerNumber = containerMatch.ComposeContainerNumber;
                            node.DetailStatus = containerMatch.State;
                            node.IsRunning = string.Equals(
                                containerMatch.State,
                                "running",
                                StringComparison.OrdinalIgnoreCase);
                            node.IsPaused = string.Equals(
                                containerMatch.State,
                                "paused",
                                StringComparison.OrdinalIgnoreCase);
                            node.StatusColor = containerMatch.StateColor;
                        }
                    }

                    node.IsDockerConnected = node.Type switch
                    {
                        NodeType.Container => !string.IsNullOrWhiteSpace(node.ContainerId) && containerIds.Contains(node.ContainerId),
                        NodeType.Volume => volumeNames.Contains(node.EffectiveVolumeName),
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

        private DockerContainer? FindComposeContainer(NodeViewModel node)
        {
            if (string.IsNullOrWhiteSpace(node.ComposeServiceName))
                return null;

            var candidates = _rawContainers
                .Where(container => container.IsComposeManaged)
                .Where(container => container.ComposeServiceName.Equals(
                    node.ComposeServiceName,
                    StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(node.ComposeProjectName))
            {
                candidates = candidates.Where(container => container.ComposeProjectName.Equals(
                    node.ComposeProjectName,
                    StringComparison.OrdinalIgnoreCase));
            }

            if (node.ComposeContainerNumber > 0)
            {
                candidates = candidates.Where(container =>
                    container.ComposeContainerNumber == node.ComposeContainerNumber);
            }

            var matches = candidates
                .OrderBy(container => container.ComposeContainerNumber)
                .Take(2)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        internal async Task DeleteContainerItemAsync(object? param)
        {
            if (param is DockerContainer c)
            {
                if (c.IsSwarmService)
                {
                    if (!_dialogService.ShowConfirm(
                            $"Swarm service '{c.Name}'을 클러스터에서 영구 삭제하시겠습니까?",
                            "Remove Swarm Service"))
                    {
                        return;
                    }

                    if ((_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService) is not ISwarmService swarmService)
                    {
                        _dialogService.ShowError("활성 Swarm Manager 연결이 없습니다.", "Remove Swarm Service");
                        return;
                    }

                    try
                    {
                        await swarmService.RemoveSwarmServiceAsync(c.Id);
                        await _mainVm.RefreshRuntimeResourcesAsync();
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowError(
                            $"Swarm service 삭제 실패:\n{ex.GetBaseException().Message}",
                            "Remove Swarm Service");
                    }
                    return;
                }

                if (_dialogService.ShowConfirm($"컨테이너 '{c.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        var service = (IContainerService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                        await service.RemoveContainerAsync(c.Id);
                        await _mainVm.RefreshRuntimeResourcesAsync();
                    }
                    catch (Exception ex) { _dialogService.ShowMessage($"삭제 실패: {ex.Message}"); }
                }
            }
        }

        private void RaiseRuntimeLabelsChanged()
        {
            OnPropertyChanged(nameof(IsSwarmRuntime));
            OnPropertyChanged(nameof(IsKubernetesRuntime));
            OnPropertyChanged(nameof(IsDockerRuntime));
            OnPropertyChanged(nameof(ResourceHeader));
            OnPropertyChanged(nameof(PrimaryResourceLabel));
            OnPropertyChanged(nameof(PrimaryResourceSearchPlaceholder));
            OnPropertyChanged(nameof(KubernetesResourceCount));
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
                        var decision = await _mainVm.ConfirmVolumeDockerDeleteAsync(service, v.Name, allowForceAttempt: true);
                        if (!decision.ShouldDelete) return;

                        await service.RemoveVolumeAsync(v.Name, decision.Force);
                        await _mainVm.RefreshRuntimeResourcesAsync();
                    }
                    catch (Exception ex) { _dialogService.ShowMessage($"볼륨 삭제 실패: {ex.Message}"); }
                }
            }
        }

        internal async Task DeleteNetworkItemAsync(object? param)
        {
            if (param is DockerNetworkGroup n)
            {
                bool isSwarmOverlay = _mainVm.ActiveSheet?.RuntimeKind == RuntimeKind.DockerSwarm;
                string resourceLabel = isSwarmOverlay ? "Swarm overlay network" : "네트워크";
                if (_dialogService.ShowConfirm($"{resourceLabel} '{n.Name}'을 영구 삭제하시겠습니까?", "확인"))
                {
                    try
                    {
                        var service = (INetworkService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                        await service.RemoveNetworkAsync(n.Id);
                        await _mainVm.RefreshRuntimeResourcesAsync();
                    }
                    catch (Exception ex) { _dialogService.ShowMessage($"네트워크 삭제 실패: {ex.Message}"); }
                }
            }
        }

        internal async Task DeleteSwarmDataResourceItemAsync(object? parameter)
        {
            if (parameter is not SwarmDataResourceSnapshot resource) return;
            if (!_dialogService.ShowConfirm(
                    $"Swarm {resource.Kind} '{resource.Name}'을 클러스터에서 영구 삭제하시겠습니까?",
                    $"Remove Swarm {resource.Kind}"))
            {
                return;
            }

            if ((_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService) is not ISwarmDataResourceMutationService mutationService)
            {
                _dialogService.ShowError("활성 Swarm Manager 연결이 없습니다.", $"Remove Swarm {resource.Kind}");
                return;
            }

            try
            {
                await mutationService.RemoveSwarmDataResourceAsync(resource.Kind, resource.Id);
                await _mainVm.RefreshRuntimeResourcesAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Swarm {resource.Kind} 삭제 실패:\n{ex.GetBaseException().Message}",
                    $"Remove Swarm {resource.Kind}");
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
                        await _mainVm.RefreshRuntimeResourcesAsync();
                    }
                    catch (Exception ex)
                    {
                        if (_dialogService.ShowConfirm($"삭제 실패: 이미지가 사용 중일 수 있습니다.\n강제로 삭제하시겠습니까?\n({ex.Message})", "강제 삭제 확인"))
                        {
                            try
                            {
                                await service.DeleteImageAsync(img.Id, force: true);
                                await _mainVm.RefreshRuntimeResourcesAsync();
                            }
                            catch (Exception forceEx) { _dialogService.ShowMessage($"강제 삭제 실패: {forceEx.Message}"); }
                        }
                    }
                }
            }
        }

        private async Task TagImageAsync(object? parameter)
        {
            if (parameter is not DockerImage img) return;

            var sourceImage = GetImageReference(img);
            if (!_dialogService.TryShowImageTagDialog(sourceImage, img.Repository, img.Tag, out var repository, out var imageTag, out var force)) return;

            try
            {
                var service = (IImageService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                await service.TagImageAsync(sourceImage, repository, imageTag, force);
                await _mainVm.RefreshRuntimeResourcesAsync();
                _dialogService.ShowInfo($"이미지 태그를 추가했습니다.\n{repository}:{imageTag}", "Tag Image");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"이미지 태그 실패: {ex.Message}", "Tag Image");
            }
        }

        private async Task PushImageAsync(object? parameter)
        {
            if (parameter is not DockerImage img) return;
            if (img.Repository == "<none>" || img.Tag == "<none>")
            {
                _dialogService.ShowInfo("Push 전에 먼저 이미지에 repository와 tag를 붙여 주세요.", "Push Image");
                return;
            }

            if (!_dialogService.TryShowImagePushDialog(
                    img.Repository,
                    img.Tag,
                    out var repository,
                    out var imageTag,
                    out var username,
                    out var password,
                    out var serverAddress))
            {
                return;
            }

            if (!_dialogService.ShowConfirm($"[{repository}:{imageTag}] 이미지를 push할까요?", "Push Image")) return;

            try
            {
                IsPulling = true;
                PullProgressValue = 0;
                PullProgressMessage = "Push 준비 중...";

                var service = (IImageService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                await service.PushImageAsync(
                    repository,
                    imageTag,
                    username,
                    password,
                    serverAddress);

                PullProgressValue = 100;
                PullProgressMessage = "Push 완료";
                _dialogService.ShowInfo("이미지 push가 완료되었습니다.", "Push Image");
            }
            catch (Exception ex)
            {
                PullProgressMessage = "Push 실패";
                _dialogService.ShowError($"이미지 push 실패: {ex.Message}", "Push Image");
            }
            finally
            {
                IsPulling = false;
            }
        }

        private async Task SaveImageAsync(object? parameter)
        {
            if (parameter is not DockerImage img) return;

            var imageRef = GetImageReference(img);
            var defaultFileName = FileService.MakeSafeFileName(imageRef.Replace(':', '_'), "image") + ".tar";
            var path = _dialogService.ShowSaveFileDialog(
                "Tar file (*.tar)|*.tar|All files (*.*)|*.*",
                ".tar",
                defaultFileName,
                "Save Docker Image");

            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var service = (IImageService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                await service.SaveImageAsync(imageRef, path);
                _dialogService.ShowInfo($"이미지를 저장했습니다.\n{path}", "Save Image");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"이미지 save 실패: {ex.Message}", "Save Image");
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

                await _mainVm.RefreshRuntimeResourcesAsync();
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

        private static string GetImageReference(DockerImage image)
        {
            if (!string.IsNullOrWhiteSpace(image.Repository) &&
                !string.IsNullOrWhiteSpace(image.Tag) &&
                image.Repository != "<none>" &&
                image.Tag != "<none>")
            {
                return $"{image.Repository}:{image.Tag}";
            }

            return image.Id;
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
        private static void SyncCollection<T>(ObservableCollection<T> uiCollection, List<T> newItems, Func<T, string> keySelector)
            where T : notnull =>
            DockerResourceCollectionSynchronizer.Sync(uiCollection, newItems, keySelector);
    }

    internal static class DockerResourceCollectionSynchronizer
    {
        internal static void Sync<T>(
            ObservableCollection<T> uiCollection,
            IReadOnlyList<T> newItems,
            Func<T, string> keySelector)
            where T : notnull
        {
            ArgumentNullException.ThrowIfNull(uiCollection);
            ArgumentNullException.ThrowIfNull(newItems);
            ArgumentNullException.ThrowIfNull(keySelector);

            var latestByKey = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (T newItem in newItems)
                latestByKey[keySelector(newItem)] = newItem;

            var toRemove = uiCollection
                .Where(item => !latestByKey.ContainsKey(keySelector(item)))
                .ToList();
            foreach (T item in toRemove)
                uiCollection.Remove(item);

            var currentKeys = new HashSet<string>(uiCollection.Select(keySelector), StringComparer.Ordinal);
            foreach (T newItem in newItems)
            {
                if (currentKeys.Add(keySelector(newItem)))
                    uiCollection.Add(newItem);
            }

            foreach (T currentItem in uiCollection)
            {
                if (latestByKey.TryGetValue(keySelector(currentItem), out T? latestItem) && latestItem is not null)
                    ApplySnapshot(currentItem, latestItem);
            }
        }

        internal static void ApplySnapshot(object currentItem, object latestItem)
        {
            if (ReferenceEquals(currentItem, latestItem)) return;

            if (currentItem is DockerResource currentResource && latestItem is DockerResource latestResource)
            {
                currentResource.Id = latestResource.Id;
                currentResource.Name = latestResource.Name;
                currentResource.StateColor = latestResource.StateColor;
                currentResource.ComposeProjectName = latestResource.ComposeProjectName;
                currentResource.ComposeResourceName = latestResource.ComposeResourceName;
                currentResource.ComposeWorkingDirectory = latestResource.ComposeWorkingDirectory;
                currentResource.ComposeConfigFiles = latestResource.ComposeConfigFiles;
                currentResource.ProjectSource = latestResource.ProjectSource;
                currentResource.Labels = new Dictionary<string, string>(
                    latestResource.Labels,
                    StringComparer.OrdinalIgnoreCase);
            }

            if (currentItem is DockerContainer currentContainer && latestItem is DockerContainer latestContainer)
            {
                currentContainer.Image = latestContainer.Image;
                currentContainer.State = latestContainer.State;
                currentContainer.Ports = latestContainer.Ports;
                currentContainer.ComposeServiceName = latestContainer.ComposeServiceName;
                currentContainer.ComposeContainerNumber = latestContainer.ComposeContainerNumber;
                currentContainer.IsComposeOneOff = latestContainer.IsComposeOneOff;
                currentContainer.IsSwarmService = latestContainer.IsSwarmService;
                currentContainer.SwarmMode = latestContainer.SwarmMode;
                currentContainer.SwarmDesiredReplicas = latestContainer.SwarmDesiredReplicas;
                currentContainer.SwarmRunningReplicas = latestContainer.SwarmRunningReplicas;
                currentContainer.IsKubernetesPod = latestContainer.IsKubernetesPod;
                currentContainer.KubernetesKind = latestContainer.KubernetesKind;
                currentContainer.KubernetesApiResource = latestContainer.KubernetesApiResource;
                currentContainer.KubernetesApiVersion = latestContainer.KubernetesApiVersion;
                currentContainer.KubernetesNamespace = latestContainer.KubernetesNamespace;
                currentContainer.KubernetesNodeName = latestContainer.KubernetesNodeName;
                currentContainer.KubernetesReady = latestContainer.KubernetesReady;
                currentContainer.KubernetesRestarts = latestContainer.KubernetesRestarts;
                currentContainer.KubernetesDesiredReplicas = latestContainer.KubernetesDesiredReplicas;
                currentContainer.KubernetesReadyReplicas = latestContainer.KubernetesReadyReplicas;
                currentContainer.KubernetesPodIp = latestContainer.KubernetesPodIp;
                currentContainer.KubernetesRawJson = latestContainer.KubernetesRawJson;
            }
            else if (currentItem is DockerNetworkGroup currentNetwork && latestItem is DockerNetworkGroup latestNetwork)
            {
                currentNetwork.Driver = latestNetwork.Driver;
                currentNetwork.Scope = latestNetwork.Scope;
            }
            else if (currentItem is DockerSwarmNode currentSwarmNode && latestItem is DockerSwarmNode latestSwarmNode)
            {
                currentSwarmNode.Hostname = latestSwarmNode.Hostname;
                currentSwarmNode.Role = latestSwarmNode.Role;
                currentSwarmNode.Availability = latestSwarmNode.Availability;
                currentSwarmNode.Status = latestSwarmNode.Status;
                currentSwarmNode.Address = latestSwarmNode.Address;
                currentSwarmNode.ManagerStatus = latestSwarmNode.ManagerStatus;
                currentSwarmNode.EngineVersion = latestSwarmNode.EngineVersion;
            }
            else if (currentItem is DockerKubernetesNode currentKubernetesNode && latestItem is DockerKubernetesNode latestKubernetesNode)
            {
                currentKubernetesNode.Role = latestKubernetesNode.Role;
                currentKubernetesNode.Status = latestKubernetesNode.Status;
                currentKubernetesNode.Version = latestKubernetesNode.Version;
                currentKubernetesNode.InternalIp = latestKubernetesNode.InternalIp;
                currentKubernetesNode.OsImage = latestKubernetesNode.OsImage;
            }
            else if (currentItem is DockerImage currentImage && latestItem is DockerImage latestImage)
            {
                currentImage.Id = latestImage.Id;
                currentImage.Repository = latestImage.Repository;
                currentImage.Tag = latestImage.Tag;
                currentImage.Size = latestImage.Size;
            }
        }
    }
}
