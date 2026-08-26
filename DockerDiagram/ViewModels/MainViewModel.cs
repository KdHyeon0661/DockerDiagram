using DockerDiagram.Infrastructure;
using DockerDiagram.Diagram;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Common;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 애플리케이션의 하위 ViewModel을 조정하고 Docker 리소스와 다이어그램 사이의 작업을 처리합니다.
    /// </summary>
    public partial class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly IDockerService _defaultDockerService;
        private readonly IDialogService _dialogService;
        internal IDockerServiceFactory DockerServiceFactory { get; }
        private readonly DockerSyncCoordinator _dockerSync;
        private readonly DiagramHistoryService _diagramHistory;
        private readonly ResourceCreationNameService _resourceNames;

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
        public MainViewModel(IDockerService dockerService, IDialogService dialogService, IDockerServiceFactory dockerServiceFactory)
        {
            _defaultDockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            DockerServiceFactory = dockerServiceFactory ?? throw new ArgumentNullException(nameof(dockerServiceFactory));
            _resourceNames = new ResourceCreationNameService(_dialogService);
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
                SheetManager,
                _dialogService);
        }

        public Task OnDockerStartedAsync() => _dockerSync.OnDockerStartedAsync();

        public async Task PlaceComposeProjectAsync(
            DockerComposeProject project,
            double x,
            double y,
            bool centerOnPoint = false)
        {
            if (ActiveSheet == null) return;
            if (project.ResourceCount == 0)
            {
                _dialogService.ShowInfo("배치할 수 있는 Compose 리소스가 없습니다.", "Compose 프로젝트 배치");
                return;
            }

            SheetViewModel sheet = ActiveSheet;
            string projectIdentity = project.IdentityKey;
            NodeViewModel? existingProjectNode = sheet.Nodes.FirstOrDefault(node =>
                string.Equals(node.ComposeProjectIdentity, projectIdentity, StringComparison.Ordinal));
            GroupViewModel? existingProjectGroup = sheet.Groups.FirstOrDefault(group =>
                string.Equals(group.ComposeProjectIdentity, projectIdentity, StringComparison.Ordinal));

            // 이전 저장 파일에는 고유키가 없으므로 프로젝트명 기준으로 한 번만 호환 판정합니다.
            existingProjectNode ??= sheet.Nodes.FirstOrDefault(node =>
                string.IsNullOrWhiteSpace(node.ComposeProjectIdentity) &&
                !string.IsNullOrWhiteSpace(node.ComposeProjectName) &&
                string.Equals(node.ComposeProjectName, project.Name, StringComparison.OrdinalIgnoreCase));

            if (existingProjectNode != null || existingProjectGroup != null)
            {
                if (centerOnPoint)
                {
                    string layoutInstanceId =
                        existingProjectNode?.ComposeLayoutInstanceId ??
                        existingProjectGroup?.ComposeLayoutInstanceId ??
                        string.Empty;
                    IReadOnlyCollection<NodeViewModel> existingNodes = !string.IsNullOrWhiteSpace(layoutInstanceId)
                        ? sheet.Nodes.Where(node => string.Equals(
                            node.ComposeLayoutInstanceId,
                            layoutInstanceId,
                            StringComparison.Ordinal)).ToList()
                        : sheet.Nodes.Where(node =>
                            string.Equals(node.ComposeProjectIdentity, projectIdentity, StringComparison.Ordinal) ||
                            (string.IsNullOrWhiteSpace(node.ComposeProjectIdentity) &&
                             string.Equals(node.ComposeProjectName, project.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                    IReadOnlyCollection<GroupViewModel> existingGroups = !string.IsNullOrWhiteSpace(layoutInstanceId)
                        ? sheet.Groups.Where(group => string.Equals(
                            group.ComposeLayoutInstanceId,
                            layoutInstanceId,
                            StringComparison.Ordinal)).ToList()
                        : sheet.Groups.Where(group =>
                            string.Equals(group.ComposeProjectIdentity, projectIdentity, StringComparison.Ordinal)).ToList();

                    ComposeDiagramLayoutService.CenterOn(
                        sheet,
                        existingNodes,
                        existingGroups,
                        x,
                        y);
                    IsModified = true;
                }

                Inspector.SelectedElement = (object?)existingProjectNode ?? existingProjectGroup;
                return;
            }

            var historyBefore = CaptureDiagramState(sheet);

            try
            {
                _dialogService.SetBusyCursor(true);
                var placementService = new ComposeProjectPlacementService(sheet.DockerService, _dialogService);
                ComposeProjectPlacementResult result = await placementService.PlaceAsync(sheet, project, x, y);
                if (centerOnPoint)
                {
                    ComposeDiagramLayoutService.CenterOn(
                        sheet,
                        result.Nodes,
                        result.Groups,
                        x,
                        y);
                }

                IsModified = true;
                RecordAdditionsFromSnapshot(
                    sheet,
                    historyBefore,
                    $"Place Compose project {project.Name}",
                    affectsDocker: false);
                Explorer.UpdateAvailableItems();

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
                _dialogService.SetBusyCursor(false);
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
                _dialogService.SetBusyCursor(true);
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
                _dialogService.SetBusyCursor(false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _dockerSync.Dispose();
            SheetManager.Dispose();
        }

        // =========================================================
        // 🎨 연결선(Connector) 긋기 로직

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
    }
}
