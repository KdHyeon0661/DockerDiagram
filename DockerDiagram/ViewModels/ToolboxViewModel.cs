using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 상단 툴바(메뉴)에 배치되는 전역 기능들(화면 비우기, 도커 시스템 청소, Compose 연동 등)을 전담하는 Sub-ViewModel입니다.
    /// </summary>
    public class ToolboxViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVm;
        private readonly IDockerService _defaultDockerService;
        private readonly IDialogService _dialogService;
        private readonly IComposeService _composeService;

        public ICommand FlowClearCommand { get; }
        public ICommand SystemPruneCommand { get; }
        public ICommand ImportComposeCommand { get; }
        public ICommand ExportComposeCommand { get; }
        public ICommand RearrangeComposeCommand { get; }
        private ComposeLayoutOptions _composeLayoutOptions = new();

        public ToolboxViewModel(MainViewModel mainVm, IDockerService defaultDockerService, IDialogService dialogService, IComposeService composeService)
        {
            _mainVm = mainVm;
            _defaultDockerService = defaultDockerService;
            _dialogService = dialogService;
            _composeService = composeService;

            FlowClearCommand = new RelayCommand(ExecuteFlowClear);
            SystemPruneCommand = new AsyncRelayCommand(ExecuteSystemPruneAsync);

            ExportComposeCommand = new RelayCommand(_ =>
            {
                if (_mainVm.ActiveSheet != null)
                {
                    ComposeExportService.ExportToCompose(_mainVm.ActiveSheet, _dialogService);
                }
            });

            ImportComposeCommand = new AsyncRelayCommand(async _ =>
            {
                // 현재 탭의 접속 상태에 맞는 도커 서비스를 유동적으로 주입
                var activeService = _mainVm.ActiveSheet?.DockerService ?? _defaultDockerService;

                await ComposeImportService.ImportFromCompose(
                    _mainVm,
                    (IContainerService)activeService,
                    (IVolumeService)activeService,
                    (INetworkService)activeService,
                    _dialogService,
                    _composeService,
                    _mainVm.LifetimeToken
                );
            });

            RearrangeComposeCommand = new AsyncRelayCommand(async _ =>
            {
                if (!_mainVm.HasSelectedComposeLayout())
                {
                    _dialogService.ShowInfo("Compose로 배치된 노드나 네트워크를 먼저 선택해 주세요.", "Compose 재정렬");
                    return;
                }

                if (!_dialogService.TryShowComposeLayoutDialog(_composeLayoutOptions, out ComposeLayoutOptions options)) return;
                _composeLayoutOptions = options.Clone();
                await _mainVm.RearrangeSelectedComposeAsync(options);
            });
        }

        /// <summary>
        /// 현재 활성화된 시트(도화지)의 모든 요소(노드, 선, 그룹)를 깨끗하게 지웁니다.
        /// </summary>
        private void ExecuteFlowClear(object? obj)
        {
            _mainVm.SheetManager.ClearSheetContents(_mainVm.ActiveSheet);
        }

        /// <summary>
        /// 도커 리소스 대청소(Prune) 명령을 실행하고 결과를 사용자에게 안내합니다.
        /// </summary>
        private async Task ExecuteSystemPruneAsync(object? obj)
        {
            if (!_dialogService.TryShowPruneOptionsDialog(out var pruneOptions)) return;

            try
            {
                _dialogService.SetBusyCursor(true);

                if (pruneOptions.Resources == DockerPruneResource.None &&
                    pruneOptions.Target == DockerPruneTarget.Volume)
                {
                    await ExecuteSafeVolumePruneAsync();
                    return;
                }

                IDockerService dockerService = _mainVm.ActiveSheet?.DockerService ?? _defaultDockerService;
                if (pruneOptions.IncludeRunningContainers &&
                    !await ConfirmRunningContainerPruneAsync(dockerService, pruneOptions))
                {
                    return;
                }

                var pruneResult = await ((ISystemService)dockerService).PruneAsync(pruneOptions);
                await RemovePrunedResourcesFromDiagramAsync(dockerService, pruneResult);

                // 청소가 끝났으니 사이드바(Explorer) 담당자에게 새로고침을 지시
                if (_mainVm.Explorer != null)
                {
                    await _mainVm.RefreshRuntimeResourcesAsync();
                }

                string operationName = pruneOptions.Resources != DockerPruneResource.None
                    ? "Docker 리소스 정리"
                    : pruneOptions.Target switch
                {
                    DockerPruneTarget.AllResources => "Docker 전체 삭제",
                    DockerPruneTarget.Container => "컨테이너 정리",
                    DockerPruneTarget.Image => "이미지 정리",
                    DockerPruneTarget.Network => "네트워크 정리",
                    _ => "미사용 리소스 정리"
                };
                _dialogService.SetBusyCursor(false);
                _dialogService.ShowPruneResult(pruneResult, operationName, pruneResult.Failures);
            }
            catch (Exception ex)
            {
                // System prune는 여러 API를 순서대로 실행하므로 중간 단계에서 실패해도
                // 앞 단계에서 삭제된 리소스가 화면에 남지 않도록 목록을 다시 읽는다.
                if (_mainVm.Explorer != null)
                {
                    try
                    {
                        await _mainVm.RefreshRuntimeResourcesAsync();
                    }
                    catch (Exception refreshException)
                    {
                        // 원래 prune 예외를 사용자에게 전달한다.
                        AppLog.Warning("Prune 실패 후 Docker 리소스 목록 갱신 실패", refreshException);
                    }
                }

                _dialogService.ShowError($"청소 중 오류 발생: {ex.Message}", "오류");
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
            }
        }

        private async Task<bool> ConfirmRunningContainerPruneAsync(
            IDockerService service,
            DockerPruneOptions options)
        {
            System.Collections.Generic.List<DockerContainer> containers = await service.GetContainersAsync();
            System.Collections.Generic.List<DockerContainer> managedContainers = containers
                .FindAll(container => DockerPruneSafety.IsOrchestratorManaged(container.Labels));
            System.Collections.Generic.List<DockerContainer> eligibleContainers = containers
                .FindAll(container => !DockerPruneSafety.IsOrchestratorManaged(container.Labels));
            int activeCount = eligibleContainers.Count(container =>
                container.State.Equals("running", StringComparison.OrdinalIgnoreCase) ||
                container.State.Equals("paused", StringComparison.OrdinalIgnoreCase) ||
                container.State.Equals("restarting", StringComparison.OrdinalIgnoreCase));
            string preview = string.Join("\n", eligibleContainers
                .Take(10)
                .Select(container => $"- {DisplayContainerName(container)} ({container.State})"));
            string remainderText = eligibleContainers.Count > 10
                ? $"\n- 외 {eligibleContainers.Count - 10}개"
                : string.Empty;
            string containerSection = eligibleContainers.Count == 0
                ? "삭제할 일반 컨테이너는 없습니다."
                : $"삭제할 일반 컨테이너 {eligibleContainers.Count}개 (실행 중 {activeCount}개)\n{preview}{remainderText}";
            string managedSection = managedContainers.Count == 0
                ? string.Empty
                : $"\n\nSwarm·Kubernetes 관리 컨테이너 {managedContainers.Count}개는 삭제하지 않습니다.";
            string additionalScopes = BuildAdditionalPruneScopeDescription(options.EffectiveResources);

            _dialogService.SetBusyCursor(false);
            bool confirmed = _dialogService.ShowConfirm(
                "이 작업은 되돌릴 수 없습니다.\n\n" +
                containerSection + managedSection + "\n\n" +
                "실행 중인 일반 컨테이너는 먼저 정상 종료를 시도한 뒤 삭제합니다." + additionalScopes + "\n" +
                "Docker 기본 네트워크와 bind mount로 연결한 호스트 파일은 삭제하지 않습니다.\n\n" +
                "선택한 위험 작업을 실행하시겠습니까?",
                "실행 중 컨테이너 삭제 확인");
            if (confirmed)
                _dialogService.SetBusyCursor(true);

            return confirmed;
        }

        private static string BuildAdditionalPruneScopeDescription(DockerPruneResource resources)
        {
            var scopes = new System.Collections.Generic.List<string>();
            if (resources.HasFlag(DockerPruneResource.Images))
                scopes.Add("이미지");
            if (resources.HasFlag(DockerPruneResource.Volumes))
                scopes.Add("볼륨");
            if (resources.HasFlag(DockerPruneResource.Networks))
                scopes.Add("네트워크");
            if (resources.HasFlag(DockerPruneResource.BuildCache))
                scopes.Add("빌드 캐시");

            return scopes.Count == 0
                ? string.Empty
                : $" 함께 선택한 미사용 {string.Join(", ", scopes)}도 정리합니다.";
        }

        private static string DisplayContainerName(DockerContainer container) =>
            !string.IsNullOrWhiteSpace(container.Name)
                ? container.Name
                : DockerIdFormatter.Shorten(container.Id);

        private async Task ExecuteSafeVolumePruneAsync()
        {
            IDockerService dockerService = _mainVm.ActiveSheet?.DockerService ?? _defaultDockerService;
            var service = (IVolumeService)dockerService;
            var volumes = await service.GetVolumesAsync();
            var unusedVolumes = new System.Collections.Generic.List<DockerVolume>();

            foreach (var volume in volumes)
            {
                if (string.IsNullOrWhiteSpace(volume.Name)) continue;
                var users = await service.GetContainersUsingVolumeAsync(volume.Name);
                if (users.Count == 0)
                    unusedVolumes.Add(volume);
            }

            if (unusedVolumes.Count == 0)
            {
                _dialogService.SetBusyCursor(false);
                _dialogService.ShowInfo("삭제할 미사용 볼륨이 없습니다.", "Volume Prune");
                return;
            }

            int previewCount = Math.Min(10, unusedVolumes.Count);
            string volumeList = string.Join("\n", unusedVolumes
                .GetRange(0, previewCount)
                .ConvertAll(volume => $"- {volume.Name}"));
            string remainderText = unusedVolumes.Count > previewCount
                ? $"\n- 외 {unusedVolumes.Count - previewCount}개"
                : string.Empty;
            _dialogService.SetBusyCursor(false);
            if (!_dialogService.ShowConfirm(
                    $"다음 미사용 볼륨 {unusedVolumes.Count}개를 삭제하시겠습니까?\n\n{volumeList}{remainderText}",
                    "Volume Prune"))
            {
                return;
            }
            _dialogService.SetBusyCursor(true);

            var deleted = new System.Collections.Generic.List<string>();
            var failed = new System.Collections.Generic.List<string>();

            foreach (var volume in unusedVolumes)
            {
                try
                {
                    await service.RemoveVolumeAsync(volume.Name, force: false);
                    deleted.Add(volume.Name);
                }
                catch (Exception ex)
                {
                    failed.Add($"{volume.Name}: {ex.Message}");
                }
            }

            if (_mainVm.Explorer != null)
                await _mainVm.RefreshRuntimeResourcesAsync();

            var pruneResult = new DockerPruneResult
            {
                VolumesDeleted = deleted
            };
            await RemovePrunedResourcesFromDiagramAsync(dockerService, pruneResult);
            _dialogService.SetBusyCursor(false);
            _dialogService.ShowPruneResult(pruneResult, "볼륨 정리", failed);
        }

        private async Task RemovePrunedResourcesFromDiagramAsync(
            IDockerService dockerService,
            DockerPruneResult result)
        {
            var deletedContainerIds = result.ContainersDeleted
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deletedVolumeNames = result.VolumesDeleted
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deletedNetworkIdentifiers = result.NetworksDeleted
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (deletedContainerIds.Count == 0 &&
                deletedVolumeNames.Count == 0 &&
                deletedNetworkIdentifiers.Count == 0)
            {
                return;
            }

            bool diagramChanged = false;
            bool clearInspectorSelection = false;

            foreach (SheetViewModel sheet in _mainVm.Sheets
                         .Where(candidate => ReferenceEquals(candidate.DockerService, dockerService)))
            {
                List<NodeViewModel> nodesToRemove = sheet.Nodes
                    .Where(node =>
                        (node.Type == NodeType.Container &&
                         MatchesDockerIdentifier(deletedContainerIds, node.ContainerId)) ||
                        (node.Type == NodeType.Volume &&
                         (deletedVolumeNames.Contains(node.EffectiveVolumeName) ||
                          deletedVolumeNames.Contains(node.Name))))
                    .ToList();

                foreach (NodeViewModel node in nodesToRemove)
                {
                    if (ReferenceEquals(sheet.SelectedNode, node))
                        sheet.SelectedNode = null;
                    if (ReferenceEquals(_mainVm.Inspector.SelectedElement, node) ||
                        _mainVm.Inspector.SelectedCanvasItems.Any(selected =>
                            ReferenceEquals(selected, node)))
                        clearInspectorSelection = true;

                    foreach (ConnectorViewModel connector in sheet.Connectors
                                 .Where(candidate =>
                                     ReferenceEquals(candidate.Source, node) ||
                                     ReferenceEquals(candidate.Target, node))
                                 .ToList())
                    {
                        if (ReferenceEquals(sheet.SelectedConnector, connector))
                            sheet.SelectedConnector = null;
                        if (ReferenceEquals(_mainVm.Inspector.SelectedElement, connector))
                            clearInspectorSelection = true;
                        sheet.Connectors.Remove(connector);
                    }

                    foreach (GroupViewModel group in sheet.Groups
                                 .Where(candidate => candidate.ContainedNodes.Contains(node))
                                 .ToList())
                    {
                        // Docker 리소스는 이미 Prune으로 삭제되었으므로 네트워크 API를 다시 호출하지 않는다.
                        await group.RemoveNodeAsync(node, isRestoring: true);
                    }

                    if (sheet.Nodes.Remove(node))
                    {
                        node.Dispose();
                        diagramChanged = true;
                    }
                }

                List<GroupViewModel> groupsToRemove = sheet.Groups
                    .Where(group =>
                        group.Type == GroupType.Network &&
                        (MatchesDockerIdentifier(deletedNetworkIdentifiers, group.Id) ||
                         deletedNetworkIdentifiers.Contains(group.Title) ||
                         deletedNetworkIdentifiers.Contains(group.DockerNetworkName)))
                    .ToList();

                foreach (GroupViewModel group in groupsToRemove)
                {
                    if (ReferenceEquals(sheet.SelectedGroup, group))
                        sheet.SelectedGroup = null;
                    if (ReferenceEquals(_mainVm.Inspector.SelectedElement, group) ||
                        _mainVm.Inspector.SelectedCanvasItems.Any(selected =>
                            ReferenceEquals(selected, group)))
                        clearInspectorSelection = true;

                    List<ConnectorViewModel> relatedConnectors = sheet.Connectors
                        .Where(connector =>
                            ReferenceEquals(connector.Source, group) ||
                            ReferenceEquals(connector.Target, group))
                        .ToList();
                    foreach (ConnectorViewModel connector in relatedConnectors)
                    {
                        if (ReferenceEquals(sheet.SelectedConnector, connector))
                            sheet.SelectedConnector = null;
                        if (ReferenceEquals(_mainVm.Inspector.SelectedElement, connector))
                            clearInspectorSelection = true;
                    }

                    DiagramDeletionSupport.RemoveGroupFromDiagram(
                        sheet,
                        group,
                        relatedConnectors);
                    diagramChanged = true;
                }

                if (nodesToRemove.Count > 0 || groupsToRemove.Count > 0)
                    sheet.UpdateGroupLayering();
            }

            if (clearInspectorSelection)
                _mainVm.Inspector.ClearSelection();
            if (diagramChanged)
                _mainVm.IsModified = true;
        }

        private static bool MatchesDockerIdentifier(
            IReadOnlySet<string> deletedIdentifiers,
            string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            return deletedIdentifiers.Any(deleted =>
                candidate.Equals(deleted, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(deleted, StringComparison.OrdinalIgnoreCase) ||
                deleted.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        }
    }
}
