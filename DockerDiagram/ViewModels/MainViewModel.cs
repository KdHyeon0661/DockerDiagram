using Docker.DotNet.Models;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 애플리케이션의 하위 ViewModel을 조정하고 Docker 리소스와 다이어그램 사이의 작업을 처리합니다.
    /// </summary>
    public class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly IDockerService _defaultDockerService;
        private readonly IDialogService _dialogService;
        private readonly DockerSyncCoordinator _dockerSync;
        private readonly DiagramHistoryService _diagramHistory;

        // =========================================================
        // 하위 ViewModel
        // =========================================================
        public SheetManagerViewModel SheetManager { get; }
        public ToolboxViewModel Toolbox { get; }
        public ResourceExplorerViewModel Explorer { get; }
        public InspectorViewModel Inspector { get; }
        public UndoRedoManagerViewModel History { get; }

        // =========================================================
        // 기존 UI 바인딩을 위한 위임 속성
        // =========================================================
        public ObservableCollection<ConnectionWorkspaceViewModel> Workspaces => SheetManager.Workspaces;
        public ObservableCollection<SheetViewModel> Sheets => SheetManager.Sheets;
        public IEnumerable<SheetViewModel> AllSheets => SheetManager.AllSheets;
        public SheetViewModel? ActiveSheet
        {
            get => SheetManager.ActiveSheet;
            set => SheetManager.ActiveSheet = value;
        }
        public bool IsModified
        {
            get => SheetManager.IsModified;
            set => SheetManager.IsModified = value;
        }
        public string? CurrentFilePath
        {
            get => SheetManager.CurrentFilePath;
            set => SheetManager.CurrentFilePath = value;
        }

        // =========================================================
        // 🐳 도커 서비스 동적 할당 프로퍼티 (현재 시트에 맞춰 통신)
        // =========================================================
        private IContainerService _containerService => ActiveSheet?.DockerService ?? _defaultDockerService;
        private IVolumeService _volumeService => ActiveSheet?.DockerService ?? _defaultDockerService;
        private INetworkService _networkService => ActiveSheet?.DockerService ?? _defaultDockerService;
        private IImageService _imageService => ActiveSheet?.DockerService ?? _defaultDockerService;

        private bool _disposed;

        // =========================================================
        // 🚀 생성자 (앱 시작 시 초기화)
        // =========================================================
        public MainViewModel(IDockerService dockerService, IDialogService dialogService)
        {
            _defaultDockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            History = new UndoRedoManagerViewModel(_dialogService);

            // 하위 ViewModel 구성
            SheetManager = new SheetManagerViewModel(this, _defaultDockerService, _dialogService);
            Toolbox = new ToolboxViewModel(this, _defaultDockerService, _dialogService, new DockerComposeCliService());
            Explorer = new ResourceExplorerViewModel(this, _defaultDockerService, _dialogService);
            Inspector = new InspectorViewModel(this, _dialogService);
            _diagramHistory = new DiagramHistoryService(
                () => ActiveSheet?.DockerService ?? _defaultDockerService,
                () => ActiveSheet,
                () => IsModified = true,
                History,
                Explorer,
                _dialogService);

            // 2. 초기 탭 생성 및 자동 로드 지시
            SheetManager.AddSheet();
            _ = SheetManager.LoadLastFileIfExistsAsync();

            _dockerSync = new DockerSyncCoordinator(
                () => ActiveSheet?.DockerService ?? _defaultDockerService,
                Explorer,
                SheetManager);
        }

        public Task OnDockerStartedAsync() => _dockerSync.OnDockerStartedAsync();

        public async Task PlaceComposeProjectAsync(DockerComposeProject project, double x, double y)
        {
            if (ActiveSheet == null || project.Containers.Count == 0) return;

            SheetViewModel sheet = ActiveSheet;
            var historyBefore = CaptureDiagramState(sheet);

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var placementService = new ComposeProjectPlacementService(sheet.DockerService, _dialogService);
                ComposeProjectPlacementResult result = await placementService.PlaceAsync(sheet, project, x, y);

                IsModified = true;
                RecordAdditionsFromSnapshot(
                    sheet,
                    historyBefore,
                    $"Place Compose project {project.Name}",
                    affectsDocker: false);

                if (result.Warnings.Count > 0)
                {
                    string details = string.Join("\n", result.Warnings.Take(5));
                    if (result.Warnings.Count > 5) details += $"\n... 외 {result.Warnings.Count - 5}개";
                    _dialogService.ShowInfo(
                        $"프로젝트 배치는 완료했지만 일부 컨테이너의 상세 정보를 읽지 못했습니다.\n\n{details}",
                        "Compose 프로젝트 배치");
                }
            }
            catch (Exception ex)
            {
                foreach (ConnectorViewModel connector in sheet.Connectors.Where(connector => !historyBefore.Connectors.Contains(connector)).ToList())
                    sheet.Connectors.Remove(connector);
                foreach (GroupViewModel group in sheet.Groups.Where(group => !historyBefore.Groups.Contains(group)).ToList())
                    sheet.Groups.Remove(group);
                foreach (NodeViewModel node in sheet.Nodes.Where(node => !historyBefore.Nodes.Contains(node)).ToList())
                    sheet.Nodes.Remove(node);

                _dialogService.ShowError($"Compose 프로젝트를 배치하지 못했습니다:\n{ex.Message}", "Compose 프로젝트 배치");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        public bool HasSelectedComposeLayout()
        {
            if (ActiveSheet == null) return false;
            return !string.IsNullOrWhiteSpace(ActiveSheet.SelectedNode?.ComposeLayoutInstanceId) ||
                   !string.IsNullOrWhiteSpace(ActiveSheet.SelectedGroup?.ComposeLayoutInstanceId);
        }

        public Task RearrangeSelectedComposeAsync(ComposeLayoutOptions options)
        {
            if (ActiveSheet == null) return Task.CompletedTask;

            SheetViewModel sheet = ActiveSheet;
            string layoutInstanceId = sheet.SelectedNode?.ComposeLayoutInstanceId ??
                                      sheet.SelectedGroup?.ComposeLayoutInstanceId ??
                                      string.Empty;
            if (string.IsNullOrWhiteSpace(layoutInstanceId))
            {
                _dialogService.ShowInfo("Compose로 배치된 노드나 네트워크를 먼저 선택해 주세요.", "Compose 재정렬");
                return Task.CompletedTask;
            }

            var nodes = sheet.Nodes
                .Where(node => string.Equals(node.ComposeLayoutInstanceId, layoutInstanceId, StringComparison.Ordinal))
                .ToList();
            var groups = sheet.Groups
                .Where(group => string.Equals(group.ComposeLayoutInstanceId, layoutInstanceId, StringComparison.Ordinal))
                .ToList();
            if (nodes.Count == 0) return Task.CompletedTask;

            var serviceNodes = nodes
                .Where(node => node.Type == NodeType.Container)
                .ToDictionary(node => node.Id, node => node, StringComparer.OrdinalIgnoreCase);
            var dependencyMap = serviceNodes.Keys.ToDictionary(
                key => key,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var serviceNodeSet = new HashSet<NodeViewModel>(serviceNodes.Values, ReferenceEqualityComparer.Instance);

            foreach (ConnectorViewModel connector in sheet.Connectors.Where(connector => connector.RelationType == RelationType.Dependency))
            {
                if (connector.Source is not NodeViewModel source || connector.Target is not NodeViewModel target) continue;
                if (!serviceNodeSet.Contains(source) || !serviceNodeSet.Contains(target)) continue;
                dependencyMap[target.Id].Add(source.Id);
            }

            static Dictionary<NodeViewModel, Rect> CaptureNodeRects(IEnumerable<NodeViewModel> source)
            {
                var result = new Dictionary<NodeViewModel, Rect>(ReferenceEqualityComparer.Instance);
                foreach (NodeViewModel node in source)
                    result[node] = new Rect(node.X, node.Y, node.Width, node.Height);
                return result;
            }

            static Dictionary<GroupViewModel, Rect> CaptureGroupRects(IEnumerable<GroupViewModel> source)
            {
                var result = new Dictionary<GroupViewModel, Rect>(ReferenceEqualityComparer.Instance);
                foreach (GroupViewModel group in source)
                    result[group] = new Rect(group.X, group.Y, group.Width, group.Height);
                return result;
            }

            var beforeNodes = CaptureNodeRects(nodes);
            var beforeGroups = CaptureGroupRects(groups);
            double originX = nodes.Min(node => node.X);
            double originY = nodes.Min(node => node.Y);

            ComposeDiagramLayoutService.Arrange(
                sheet,
                serviceNodes,
                dependencyMap,
                originX,
                originY,
                nodes.Where(node => node.Type == NodeType.Volume).ToList(),
                groups,
                options);

            var afterNodes = CaptureNodeRects(nodes);
            var afterGroups = CaptureGroupRects(groups);

            RecordComposeLayoutChange(
                sheet,
                beforeNodes,
                afterNodes,
                beforeGroups,
                afterGroups,
                "Rearrange Compose project");
            IsModified = true;
            return Task.CompletedTask;
        }

        public async Task ApplyStackTemplateAsync(
            StackTemplateDefinition template,
            StackTemplateDeploymentOptions options,
            double x,
            double y)
        {
            if (ActiveSheet == null) return;

            var sheet = ActiveSheet;
            var deploymentService = new StackTemplateDeploymentService(sheet.DockerService, _dialogService);
            StackTemplateApplication? application = null;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                application = await deploymentService.ApplyAsync(template, options, sheet, x, y);
                IsModified = true;

                var historyApplication = application;
                History.RecordExecuted(new DelegateHistoryCommand(
                    $"Create stack template: {template.Name}",
                    affectsDocker: options.DeployToDocker,
                    undo: async () =>
                    {
                        await deploymentService.RemoveAsync(historyApplication, sheet, options.DeployToDocker);
                        IsModified = true;
                        if (ReferenceEquals(ActiveSheet, sheet))
                            await Explorer.SyncWithDockerEngineAsync();
                    },
                    redo: async () =>
                    {
                        historyApplication = await deploymentService.ApplyAsync(template, options, sheet, x, y);
                        IsModified = true;
                        if (ReferenceEquals(ActiveSheet, sheet))
                            await Explorer.SyncWithDockerEngineAsync();
                    }));

                await Explorer.SyncWithDockerEngineAsync();
                _dialogService.ShowInfo(
                    options.DeployToDocker
                        ? $"'{template.Name}' 스택을 생성하고 실행했습니다."
                        : $"'{template.Name}' 스택을 다이어그램에 추가했습니다.",
                    "Stack Template");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"'{template.Name}' 템플릿 적용에 실패했습니다.\n\n{ex.Message}",
                    "Stack Template");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _dockerSync.Dispose();
        }

        // =========================================================
        // 🎨 연결선(Connector) 긋기 로직
        // =========================================================
        public async Task AddConnectionAsync(IConnectableItem source, IConnectableItem target, PortDirection sourceDir, PortDirection targetDir)
        {
            if (ActiveSheet == null || source == target) return;

            if (!IsValidConnection(source, target))
            {
                _dialogService.ShowMessage("연결할 수 없는 조합입니다.\n(볼륨끼리 연결하거나, 인터넷과 볼륨은 연결할 수 없습니다.)");
                return;
            }

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

            // 볼륨 마운트 물리적 연결
            if (finalSource is NodeViewModel fsNode && fsNode.Type == NodeType.Container &&
                finalTarget is NodeViewModel ftNode && ftNode.Type == NodeType.Volume)
            {
                bool isSuccess = await ConnectVolumeToContainerAsync(fsNode, ftNode);
                if (!isSuccess) return;
            }

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
                    newConnector.RelationType = RelationType.Dependency;
                }

                ActiveSheet.Connectors.Add(newConnector);
                RecordConnectorAdd(ActiveSheet, newConnector);
            }

            IsModified = true;
        }

        private bool IsValidConnection(IConnectableItem t1, IConnectableItem t2)
        {
            bool isT1Volume = t1 is NodeViewModel n1 && n1.Type == NodeType.Volume;
            bool isT2Volume = t2 is NodeViewModel n2 && n2.Type == NodeType.Volume;
            bool isT1Internet = t1 is NodeViewModel i1 && i1.Type == NodeType.Internet;
            bool isT2Internet = t2 is NodeViewModel i2 && i2.Type == NodeType.Internet;

            if (isT1Volume && isT2Volume) return false;
            if ((isT1Internet && isT2Volume) || (isT1Volume && isT2Internet)) return false;
            return true;
        }

        // =========================================================
        // 🧱 노드(Node) 및 도커 리소스 생성 로직 모음
        // =========================================================

        public async Task CreateNodeAtAsync(object item, double x, double y)
        {
            if (ActiveSheet == null) return;
            var historyBefore = CaptureDiagramState(ActiveSheet);

            // [CASE 1] 컨테이너 (DockerContainer)
            if (item is DockerContainer container)
            {
                ActiveSheet.CreateNodeAt(container, x, y);
                IsModified = true;
                Explorer.RegisterTemplateUsage(container.Image);

                if (!string.IsNullOrEmpty(container.Id))
                {
                    try
                    {
                        var info = await _containerService.InspectContainerAsync(container.Id);

                        // 네트워크 복구
                        if (info.NetworkSettings != null && info.NetworkSettings.Networks != null)
                        {
                            foreach (var netKvp in info.NetworkSettings.Networks)
                            {
                                string netName = netKvp.Key;
                                if (netName == "bridge") continue;

                                var existingGroup = ActiveSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == netName);

                                if (existingGroup == null)
                                {
                                    existingGroup = new GroupViewModel(x - 30, y - 40, 220, 150, _networkService, _dialogService, netName, GroupType.Network)
                                    {
                                        IsDockerConnected = true
                                    };
                                    ActiveSheet.AddGroup(existingGroup);
                                }

                                var newNode = ActiveSheet.Nodes.LastOrDefault(n => n.ContainerId == container.Id);
                                if (newNode != null)
                                {
                                    await existingGroup.AddNodeAsync(newNode, isRestoring: true);
                                }
                            }
                        }

                        // 볼륨 복구
                        if (info.Mounts != null)
                        {
                            int volIndex = 0;
                            foreach (var mount in info.Mounts)
                            {
                                if (mount.Type == "volume")
                                {
                                    string volName = mount.Name;
                                    string destination = mount.Destination;

                                    var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n =>
                                        n.Type == NodeType.Volume &&
                                        (string.Equals(n.Name, volName, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(n.EffectiveVolumeName, volName, StringComparison.OrdinalIgnoreCase)));
                                    NodeViewModel targetVolNode;

                                    if (existingVolNode != null)
                                    {
                                        targetVolNode = existingVolNode;
                                    }
                                    else
                                    {
                                        var volModel = new DockerVolume { Name = volName };
                                        ActiveSheet.CreateNodeAt(volModel, x + 250, y + (volIndex * 120));
                                        targetVolNode = ActiveSheet.Nodes.Last();
                                    }

                                    var newNode = ActiveSheet.Nodes.LastOrDefault(n => n.ContainerId == container.Id);
                                    if (newNode != null)
                                    {
                                        bool connExists = ActiveSheet.Connectors.Any(c =>
                                            (c.Source == newNode && c.Target == targetVolNode) ||
                                            (c.Source == targetVolNode && c.Target == newNode));

                                        if (!connExists)
                                        {
                                            var conn = new ConnectorViewModel(newNode, targetVolNode, PortDirection.Right, PortDirection.Left, _dialogService)
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
                        _dialogService.ShowError($"연관 정보 로드 실패:\n{ex.Message}", "Docker API Error");
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
                var groupVm = new GroupViewModel(x, y, 220, 150, _networkService, _dialogService, network.Name, GroupType.Network)
                {
                    Id = network.Id,
                    Driver = network.Driver,
                    IsDockerConnected = true
                };
                ActiveSheet.AddGroup(groupVm);
                IsModified = true;
            }

            RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, "Add diagram item", affectsDocker: false);
        }

        public async Task CreateNewContainerNodeAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes, string restartPolicy, long memoryMb, double cpuCount, double x, double y, string networkName = "bridge", string command = "", bool tty = false, string? regUser = null, string? regPass = null, string? regServer = null)
        {
            if (ActiveSheet == null) return;
            var historyBefore = CaptureDiagramState(ActiveSheet);
            var safePorts = ports ?? new List<string>();
            var safeEnvs = envs ?? new List<string>();
            var safeVolumes = volumes ?? new List<string>();

            bool isNameUsed = ActiveSheet.Nodes.Any(n => n.Type == NodeType.Container && n.Name == name);
            if (isNameUsed)
            {
                _dialogService.ShowError($"'{name}'(은)는 이미 다이어그램에 존재하는 컨테이너 이름입니다.\n다른 이름을 사용해 주세요.", "이름 중복 오류");
                return;
            }

            if (safePorts.Count > 0)
            {
                var newHostPorts = safePorts.Select(p => p.Split(':')[0]).ToList();
                var existingContainers = ActiveSheet.Nodes.Where(n => n.Type == NodeType.Container && n.PortBindings != null);

                foreach (var existingNode in existingContainers)
                {
                    var existingHostPorts = existingNode.PortBindings.Select(p => p.Split(':')[0]);
                    var conflictedPort = newHostPorts.FirstOrDefault(p => existingHostPorts.Contains(p));
                    if (conflictedPort != null)
                    {
                        _dialogService.ShowError($"호스트 포트 '{conflictedPort}'는 이미 '{existingNode.Name}' 컨테이너가 사용 중입니다.\n충돌을 방지하기 위해 작업을 취소합니다.", "포트 충돌 경고");
                        return;
                    }
                }
            }

            var namedVolumesToDraw = new List<string>();
            foreach (var vol in safeVolumes)
            {
                bool isBindMount = System.Text.RegularExpressions.Regex.IsMatch(vol, @"^([a-zA-Z]:[\\/]|/|\.|~)");
                if (!isBindMount) namedVolumesToDraw.Add(vol);
            }

            (image, tag) = DockerImageReferenceParser.Split(image, tag);

            GroupViewModel? targetGroup = null;

            if (!string.IsNullOrWhiteSpace(networkName) && networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                targetGroup = ActiveSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == networkName);

                if (targetGroup == null)
                {
                    targetGroup = new GroupViewModel(x, y, 220, 150, _networkService, _dialogService, networkName, GroupType.Network);
                    ActiveSheet.AddGroup(targetGroup);

                    try
                    {
                        targetGroup.Id = await _networkService.CreateNetworkAsync(networkName, "bridge");
                        targetGroup.IsDockerConnected = true;
                    }
                    catch (Exception ex)
                    {
                        if (!ex.Message.Contains("already exists") && !ex.Message.Contains("409"))
                            Debug.WriteLine($"[DockerDiscovery] 네트워크 '{networkName}' 자동 생성 실패: {ex.Message}");
                        else
                            targetGroup.IsDockerConnected = true;
                    }
                }

                x = targetGroup.X + 20;
                y = targetGroup.Y + 40 + (targetGroup.ContainedNodes.Count * 100);

                if (y + 80 > targetGroup.Y + targetGroup.Height)
                {
                    targetGroup.Height = (y - targetGroup.Y) + 100;
                }
            }

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
                try { await _imageService.PullImageAsync(image, tag, regUser, regPass, regServer); }
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

                string containerId = await _containerService.CreateAndStartContainerAsync(
                    name, image, tag, safePorts, safeEnvs, safeVolumes, restartPolicy, memoryMb, cpuCount, command, tty);

                node.Name = name;
                node.ContainerId = containerId;
                node.PortInfo = string.Join(", ", safePorts);
                node.PortBindings = safePorts;
                node.EnvironmentVariables = safeEnvs;
                node.RestartPolicy = restartPolicy;
                node.IsCreating = false;
                node.StatusColor = "#28a745";
                node.IsDockerConnected = true;

                if (targetGroup != null)
                {
                    await targetGroup.AddNodeAsync(node);
                    ActiveSheet.UpdateGroupLayering();
                }

                Explorer.RegisterTemplateUsage($"{image}:{tag}");

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

                    var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n =>
                        n.Type == NodeType.Volume &&
                        (string.Equals(n.Name, volName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(n.EffectiveVolumeName, volName, StringComparison.OrdinalIgnoreCase)));
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
                            StatusColor = "#E67E22",
                            IsDockerConnected = true
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
                Explorer.UpdateAvailableItems();
                RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, $"Create container {name}", History.IncludeDockerResourceHistory);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"컨테이너 생성 중 오류가 발생했습니다:\n{ex.Message}", "생성 실패");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        public async Task CreateNewVolumeNodeAsync(string name, string driver, double x, double y)
        {
            await CreateNewVolumeNodeAsync(VolumeCreateOptions.Basic(name, driver), x, y);
        }

        public async Task CreateNewVolumeNodeAsync(VolumeCreateOptions options, double x, double y)
        {
            if (ActiveSheet == null) return;
            var historyBefore = CaptureDiagramState(ActiveSheet);
            string displayName = options.Name.Trim();
            string dockerVolumeName = options.EffectiveDockerVolumeName.Trim();
            string driver = string.IsNullOrWhiteSpace(options.Driver) ? "local" : options.Driver.Trim();
            options.Name = displayName;
            options.DockerVolumeName = dockerVolumeName;
            options.Driver = driver;

            var node = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = $"{displayName} (Creating...)",
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
                if (options.External)
                {
                    var existing = await _volumeService.InspectVolumeAsync(dockerVolumeName);
                    driver = string.IsNullOrWhiteSpace(existing.Driver) ? driver : existing.Driver;
                }
                else
                {
                    await _volumeService.CreateVolumeAsync(options);
                }

                node.Name = displayName;
                node.DockerVolumeName = dockerVolumeName;
                node.VolumeExternal = options.External;
                node.VolumeLabels = new Dictionary<string, string>(options.Labels);
                node.VolumeDriverOptions = new Dictionary<string, string>(options.DriverOptions);
                node.ContainerId = "";
                node.Driver = driver;
                node.ImageName = driver;
                node.IsCreating = false;
                node.StatusColor = "#E67E22";
                node.IsDockerConnected = true;
                RecordAdditionsFromSnapshot(
                    ActiveSheet,
                    historyBefore,
                    options.External ? $"Add external volume {dockerVolumeName}" : $"Create volume {dockerVolumeName}",
                    !options.External && History.IncludeDockerResourceHistory);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"볼륨 생성 실패: {ex.Message}");
                ActiveSheet.Nodes.Remove(node);
            }
        }

        public async Task CreateNewNetworkGroupAsync(string name, string driver, double x, double y, double w, double h)
        {
            await CreateNewNetworkGroupAsync(NetworkCreateOptions.Basic(name, driver), x, y, w, h);
        }

        public async Task CreateNewNetworkGroupAsync(NetworkCreateOptions options, double x, double y, double w, double h)
        {
            if (string.IsNullOrWhiteSpace(options.Name) || ActiveSheet == null) return;
            var historyBefore = CaptureDiagramState(ActiveSheet);

            try
            {
                string networkId;
                if (options.External)
                {
                    var dockerNetworkName = string.IsNullOrWhiteSpace(options.ComposeNetworkName) ? options.Name : options.ComposeNetworkName;
                    var networks = await _networkService.GetNetworksAsync();
                    var existingNetwork = networks.FirstOrDefault(n => string.Equals(n.Name, dockerNetworkName, StringComparison.OrdinalIgnoreCase));
                    if (existingNetwork == null)
                    {
                        _dialogService.ShowError($"외부 네트워크 '{dockerNetworkName}'을(를) Docker에서 찾을 수 없습니다.\n먼저 Docker에 해당 네트워크를 만든 뒤 다시 시도하세요.", "External Network");
                        return;
                    }

                    networkId = existingNetwork.Id;
                    options.Driver = existingNetwork.Driver;
                }
                else
                {
                    networkId = await _networkService.CreateNetworkAsync(options);
                }

                var newNetworkGroup = new GroupViewModel(x, y, w, h, _networkService, _dialogService, options.Name, GroupType.Network)
                {
                    Id = networkId,
                    Driver = options.Driver,
                    Subnet = options.Subnet,
                    Gateway = options.Gateway,
                    IpRange = options.IpRange,
                    Internal = options.Internal,
                    Attachable = options.Attachable,
                    EnableIPv6 = options.EnableIPv6,
                    External = options.External,
                    ComposeNetworkName = options.ComposeNetworkName,
                    ComposeRawNetworkYaml = options.ComposeRawNetworkYaml,
                    Labels = new Dictionary<string, string>(options.Labels),
                    DriverOptions = new Dictionary<string, string>(options.DriverOptions),
                    AuxAddresses = new Dictionary<string, string>(options.AuxAddresses),
                    IsDockerConnected = true,
                    ParentSheet = this.ActiveSheet
                };

                ActiveSheet.Groups.Add(newNetworkGroup);
                ActiveSheet.UpdateGroupLayering();

                await ActiveSheet.RefreshGroupContainmentAsync(newNetworkGroup);

                IsModified = true;
                RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, $"Create network {options.Name}", !options.External && History.IncludeDockerResourceHistory);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"'{options.Name}' 네트워크 생성에 실패했습니다:\n{ex.Message}", "Network Create Error");
            }
        }

        public async Task ProcessCliCommandAsync(string cliCommand, double x, double y)
        {
            if (ActiveSheet == null) return;
            var historyBefore = CaptureDiagramState(ActiveSheet);

            var regex = new System.Text.RegularExpressions.Regex(@"[\""].+?[\""]|['].+?[']|[^ ]+");
            var tokens = regex.Matches(cliCommand).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value.Trim('\"', '\'')).ToList();

            string name = $"cli-{Guid.NewGuid().ToString().Substring(0, 4)}";
            string image = "unknown";
            string networkName = "bridge";

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == "--name" && i + 1 < tokens.Count) name = tokens[i + 1];
                if ((tokens[i] == "--network" || tokens[i] == "--net") && i + 1 < tokens.Count) networkName = tokens[i + 1];
                if (!tokens[i].StartsWith("-") && tokens[i] != "docker" && tokens[i] != "run" && image == "unknown") image = tokens[i];
            }

            if (networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                var existingNetworks = await _networkService.GetNetworksAsync();
                if (!existingNetworks.Any(n => n.Name == networkName))
                {
                    _dialogService.ShowError($"명령어 실행 실패!\n\n도커 엔진에 '{networkName}' 네트워크가 존재하지 않습니다.\n먼저 해당 네트워크를 생성한 후 다시 시도해 주세요.", "네트워크 없음");
                    return;
                }
            }

            GroupViewModel? targetGroup = null;
            if (!string.IsNullOrWhiteSpace(networkName) && networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                targetGroup = ActiveSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == networkName);
                if (targetGroup == null)
                {
                    targetGroup = new GroupViewModel(x, y, 220, 150, _networkService, _dialogService, networkName, GroupType.Network)
                    {
                        IsDockerConnected = true
                    };
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
                StatusColor = "#FFC107"
            };
            ActiveSheet.Nodes.Add(dummyNode);

            if (targetGroup != null)
            {
                await targetGroup.AddNodeAsync(dummyNode, isRestoring: true);
            }

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
                    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("cmd.exe 프로세스를 시작할 수 없습니다.");
                    process.WaitForExit();
                });

                var allContainers = await _containerService.GetContainersAsync();
                var realContainer = allContainers.FirstOrDefault(c => c.Name == name || c.Name == $"/{name}");

                if (realContainer != null)
                {
                    dummyNode.ContainerId = realContainer.Id;
                    dummyNode.Name = name;
                    dummyNode.IsCreating = false;
                    dummyNode.StatusColor = "#28a745";
                    dummyNode.IsDockerConnected = true;

                    await dummyNode.RefreshDetailsAsync();

                    try
                    {
                        var inspectData = await _containerService.InspectContainerAsync(realContainer.Id);
                        if (inspectData?.Mounts != null)
                        {
                            int volIndex = 0;
                            foreach (var mount in inspectData.Mounts)
                            {
                                if (mount.Type == "volume")
                                {
                                    string volName = mount.Name;
                                    string mountPath = mount.Destination;

                                    var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n =>
                                        n.Type == NodeType.Volume &&
                                        (string.Equals(n.Name, volName, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(n.EffectiveVolumeName, volName, StringComparison.OrdinalIgnoreCase)));
                                    NodeViewModel targetVolNode;

                                    if (existingVolNode != null) targetVolNode = existingVolNode;
                                    else
                                    {
                                        targetVolNode = new NodeViewModel(_containerService, _volumeService, _dialogService)
                                        {
                                            Name = volName,
                                            Type = NodeType.Volume,
                                            ImageName = "local",
                                            X = dummyNode.X + 250,
                                            Y = dummyNode.Y + (volIndex * 100),
                                            StatusColor = "#E67E22",
                                            IsDockerConnected = true
                                        };
                                        ActiveSheet.Nodes.Add(targetVolNode);
                                    }

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
                        Debug.WriteLine($"[DockerDiscovery] Inspect 실패: {ex.Message}");

                        _dialogService.ShowInfo(
                            $"컨테이너 '{name}'(은)는 성공적으로 생성되었으나, 볼륨 마운트 등의 상세 정보를 불러오는데 실패했습니다.\n" +
                            $"컨테이너가 실행 직후 즉시 종료(Exit)되었거나 API 응답이 지연되었을 수 있습니다.\n\n" +
                            $"[상세 오류]\n{ex.Message}",
                            "⚠️ 상세 정보 동기화 경고"
                        );
                    }

                    Explorer.UpdateAvailableItems();
                    RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, $"Create container {name}", History.IncludeDockerResourceHistory);
                }
                else
                {
                    _dialogService.ShowError($"명령어 실행 실패.\n도커가 컨테이너를 생성하지 못했습니다. 명령어를 다시 확인해 주세요.", "실패");
                    ActiveSheet.Nodes.Remove(dummyNode);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"CMD 실행 중 오류가 발생했습니다:\n{ex.Message}", "명령어 실행 오류");
                ActiveSheet.Nodes.Remove(dummyNode);
            }
        }

        public async Task BuildImageAndCreateNodeAsync(string targetImageName, string dockerfileContent, string uploadedFilePath, double x, double y)
        {
            if (ActiveSheet == null) return;
            if (string.IsNullOrWhiteSpace(targetImageName)) targetImageName = $"custom-app:{Guid.NewGuid().ToString().Substring(0, 4)}";

            string buildContextPath = "";
            string dockerfilePath = "";

            if (!string.IsNullOrEmpty(uploadedFilePath) && System.IO.File.Exists(uploadedFilePath))
            {
                dockerfilePath = uploadedFilePath;
                buildContextPath = Path.GetDirectoryName(uploadedFilePath)
                    ?? throw new InvalidOperationException("Dockerfile 경로의 상위 폴더를 확인할 수 없습니다.");
            }
            else
            {
                buildContextPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DockerDiagramBuild_" + Guid.NewGuid().ToString().Substring(0, 8));
                System.IO.Directory.CreateDirectory(buildContextPath);
                dockerfilePath = System.IO.Path.Combine(buildContextPath, "Dockerfile");
                await System.IO.File.WriteAllTextAsync(dockerfilePath, dockerfileContent);
            }

            var dummyNode = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = $"Building ({targetImageName})...",
                ImageName = "Building...",
                Type = NodeType.Container,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#17a2b8"
            };
            ActiveSheet.Nodes.Add(dummyNode);

            try
            {
                await _imageService.BuildImageAsync(targetImageName, buildContextPath, dockerfilePath);

                ActiveSheet.Nodes.Remove(dummyNode);

                string containerName = targetImageName.Split(':')[0] + "-" + Guid.NewGuid().ToString().Substring(0, 4);

                await CreateNewContainerNodeAsync(
                    containerName, targetImageName.Split(':')[0],
                    targetImageName.Contains(":") ? targetImageName.Split(':')[1] : "latest",
                    new List<string>(), new List<string>(), new List<string>(), "no", 0, 0, x, y);
            }
            catch (Exception ex)
            {
                ActiveSheet.Nodes.Remove(dummyNode);
                _dialogService.ShowMessage($"빌드 중 오류 발생: {ex.Message}");
            }
        }

        public async Task BuildImageOnlyAsync(string targetImageName, string dockerfileContent, string uploadedFilePath)
        {
            if (string.IsNullOrWhiteSpace(targetImageName))
            {
                targetImageName = $"custom-image:{Guid.NewGuid().ToString().Substring(0, 4)}";
            }

            string buildContextPath = "";
            string dockerfilePath = "";
            bool isTempContext = false;

            try
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                _dialogService.ShowConfirm($"[{targetImageName}] 이미지 빌드를 시작합니다...\n(백그라운드에서 진행됩니다.)", "빌드 시작");

                if (!string.IsNullOrEmpty(uploadedFilePath) && File.Exists(uploadedFilePath))
                {
                    dockerfilePath = uploadedFilePath;
                    buildContextPath = Path.GetDirectoryName(uploadedFilePath)
                        ?? throw new InvalidOperationException("Dockerfile 경로의 상위 폴더를 확인할 수 없습니다.");
                }
                else
                {
                    isTempContext = true;
                    buildContextPath = Path.Combine(Path.GetTempPath(), "DockerDiagramBuild_" + Guid.NewGuid().ToString().Substring(0, 8));
                    Directory.CreateDirectory(buildContextPath);

                    dockerfilePath = Path.Combine(buildContextPath, "Dockerfile");
                    await File.WriteAllTextAsync(dockerfilePath, dockerfileContent);
                }

                await _imageService.BuildImageAsync(targetImageName, buildContextPath, dockerfilePath);

                _dialogService.ShowConfirm($"[{targetImageName}] 이미지가 성공적으로 생성되었습니다!", "빌드 완료");
                await Explorer.SyncWithDockerEngineAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"빌드 중 시스템 오류 발생: {ex.Message}", "오류");
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
                if (isTempContext && Directory.Exists(buildContextPath))
                {
                    try { Directory.Delete(buildContextPath, true); } catch { }
                }
            }
        }

        // =========================================================
        // ↩️ Undo / Redo 히스토리 헬퍼
        // =========================================================
        private DiagramHistoryService.DiagramState CaptureDiagramState(SheetViewModel sheet) =>
            _diagramHistory.CaptureState(sheet);

        private void RecordAdditionsFromSnapshot(
            SheetViewModel sheet,
            DiagramHistoryService.DiagramState before,
            string description,
            bool affectsDocker) =>
            _diagramHistory.RecordAdditions(sheet, before, description, affectsDocker);

        private void RecordConnectorAdd(SheetViewModel sheet, ConnectorViewModel connector) =>
            _diagramHistory.RecordConnectorAdd(sheet, connector);

        public void RecordNodeRectChange(NodeViewModel node, Rect before, Rect after, string description) =>
            _diagramHistory.RecordNodeRectChange(node, before, after, description);

        public void RecordGroupRectChange(GroupViewModel group, Rect before, Rect after, string description) =>
            _diagramHistory.RecordGroupRectChange(group, before, after, description);

        public void RecordComposeLayoutChange(
            SheetViewModel sheet,
            IReadOnlyDictionary<NodeViewModel, Rect> beforeNodes,
            IReadOnlyDictionary<NodeViewModel, Rect> afterNodes,
            IReadOnlyDictionary<GroupViewModel, Rect> beforeGroups,
            IReadOnlyDictionary<GroupViewModel, Rect> afterGroups,
            string description) =>
            _diagramHistory.RecordLayoutChange(sheet, beforeNodes, afterNodes, beforeGroups, afterGroups, description);

        public IHistoryCommand CreateConnectorDeleteCommand(SheetViewModel sheet, ConnectorViewModel connector) =>
            _diagramHistory.CreateConnectorDeleteCommand(sheet, connector);

        public IHistoryCommand CreateNodeDeleteCommand(
            SheetViewModel sheet,
            NodeViewModel node,
            bool deleteDocker,
            bool forceVolumeDelete = false) =>
            _diagramHistory.CreateNodeDeleteCommand(sheet, node, deleteDocker, forceVolumeDelete);

        public IHistoryCommand CreateGroupDeleteCommand(
            SheetViewModel sheet,
            GroupViewModel group,
            bool deleteDocker) =>
            _diagramHistory.CreateGroupDeleteCommand(sheet, group, deleteDocker);

        public Task<(bool ShouldDelete, bool Force)> ConfirmVolumeDockerDeleteAsync(
            IVolumeService volumeService,
            string volumeName,
            bool allowForceAttempt) =>
            _diagramHistory.ConfirmVolumeDockerDeleteAsync(volumeService, volumeName, allowForceAttempt);

        public async Task<bool> ConnectVolumeToContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode)
        {
            if (!_dialogService.TryShowMountDialog(out string mountPath, out string owner)) return false;

            string containerId = containerNode.ContainerId;
            string volumeName = volumeNode.Name;

            bool keepBackup = false;
            string tempHostPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docker_backup_" + Guid.NewGuid());

            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
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

                var inspect = await _containerService.InspectContainerAsync(containerId);
                var oldConfig = inspect.Config;
                var oldHostConfig = inspect.HostConfig;

                string imageName = oldConfig.Image;
                string imgRepo = imageName;
                string imgTag = "latest";
                int lastColonIndex = imageName.LastIndexOf(':');
                if (lastColonIndex > 0)
                {
                    imgRepo = imageName[..lastColonIndex];
                    imgTag = imageName[(lastColonIndex + 1)..];
                }

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

                var envs = oldConfig.Env != null ? oldConfig.Env.ToList() : new List<string>();

                var volumes = new List<string>();
                if (oldHostConfig.Binds != null) volumes.AddRange(oldHostConfig.Binds);
                volumes.Add($"{volumeName}:{mountPath}");

                string command = oldConfig.Cmd != null ? string.Join(" ", oldConfig.Cmd) : "";
                bool tty = oldConfig.Tty;

                await _containerService.RemoveContainerAsync(containerId);

                string newId = await _containerService.CreateAndStartContainerAsync(
                    containerNode.Name, imgRepo, imgTag, ports, envs, volumes,
                    oldHostConfig.RestartPolicy.Name.ToString(), 0, 0,
                    command,
                    tty
                );

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

                if (!string.IsNullOrWhiteSpace(owner))
                {
                    string cmd = $"chown -R {owner} {mountPath}";
                    await _containerService.ExecuteCommandAsync(newId, cmd);
                }

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
    }
}
