using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Docker.DotNet.Models;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public sealed record InspectorSelectionSummary(
        ConnectableItemViewModel Item,
        string KindLabel,
        string Detail);

    /// <summary>
    /// 화면 우측(또는 하단)에 표시되는 상세 속성창(Inspector)과 선택된 객체에 대한 액션(삭제, 해제 등)을 전담하는 Sub-ViewModel입니다.
    /// </summary>
    public class InspectorViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVm;
        private readonly IDialogService _dialogService;

        // 커맨드
        public ICommand ClosePanelCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand SetConnectorDirectionCommand { get; }
        public ICommand ReverseConnectorDirectionCommand { get; }
        public ICommand FocusSelectionCommand { get; }
        public AsyncRelayCommand ReconnectCommand { get; }

        public InspectorViewModel(MainViewModel mainVm, IDialogService dialogService)
        {
            _mainVm = mainVm;
            _dialogService = dialogService;

            ClosePanelCommand = new RelayCommand(_ => ClearSelection());
            DeleteCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync());
            SetConnectorDirectionCommand = new RelayCommand(SetConnectorDirection);
            ReverseConnectorDirectionCommand = new RelayCommand(_ => ReverseConnectorDirection());
            FocusSelectionCommand = new RelayCommand(FocusSelection);
            ReconnectCommand = new AsyncRelayCommand(_ => ReconnectSelectedAsync(), _ => IsSelectedRuntimeDisconnected);
        }

        private readonly record struct ConnectorDirectionState(
            IConnectableItem Source,
            PortDirection SourceDirection,
            IConnectableItem Target,
            PortDirection TargetDirection,
            bool IsBidirectional,
            string SourceDataLabel,
            string TargetDataLabel);

        private static ConnectorDirectionState CaptureDirection(ConnectorViewModel connector) =>
            new(
                connector.Source,
                connector.SourceDir,
                connector.Target,
                connector.TargetDir,
                connector.IsBidirectional,
                connector.SourceDataLabel,
                connector.TargetDataLabel);

        private static void ApplyDirection(ConnectorViewModel connector, ConnectorDirectionState state)
        {
            connector.UpdateConnection(state.Source, state.SourceDirection, state.Target, state.TargetDirection);
            connector.IsBidirectional = state.IsBidirectional;
            connector.SourceDataLabel = state.SourceDataLabel;
            connector.TargetDataLabel = state.TargetDataLabel;
        }

        private void SetConnectorDirection(object? parameter)
        {
            if (SelectedElement is not ConnectorViewModel connector) return;
            if (!connector.CanChangeDirectionMode) return;

            bool isBidirectional = string.Equals(parameter?.ToString(), "Bidirectional", StringComparison.Ordinal);
            if (connector.IsBidirectional == isBidirectional) return;

            ConnectorDirectionState before = CaptureDirection(connector);
            connector.IsBidirectional = isBidirectional;
            ConnectorDirectionState after = CaptureDirection(connector);
            RecordConnectorDirectionChange(connector, before, after, "Change connector direction mode");
        }

        private void ReverseConnectorDirection()
        {
            if (SelectedElement is not ConnectorViewModel connector) return;
            if (!connector.CanReverseDirection) return;

            ConnectorDirectionState before = CaptureDirection(connector);
            connector.ReverseDirection();
            ConnectorDirectionState after = CaptureDirection(connector);
            RecordConnectorDirectionChange(connector, before, after, "Reverse connector direction");
        }

        private void RecordConnectorDirectionChange(
            ConnectorViewModel connector,
            ConnectorDirectionState before,
            ConnectorDirectionState after,
            string description)
        {
            _mainVm.History.RecordExecuted(new DelegateHistoryCommand(
                description,
                affectsDocker: false,
                undo: () =>
                {
                    ApplyDirection(connector, before);
                    return Task.CompletedTask;
                },
                redo: () =>
                {
                    ApplyDirection(connector, after);
                    return Task.CompletedTask;
                }));
        }

        // 상세정보를 표시하는 단일 선택과 캔버스의 다중 선택을 분리합니다.
        private object? _selectedElement;
        private readonly List<ConnectableItemViewModel> _selectedCanvasItems = [];

        public object? SelectedElement
        {
            get => _selectedElement;
            set
            {
                if (value is ConnectableItemViewModel canvasItem)
                    ApplySelection([canvasItem], canvasItem);
                else
                    ApplySelection([], value);
            }
        }

        public IReadOnlyList<ConnectableItemViewModel> SelectedCanvasItems => _selectedCanvasItems;
        public IReadOnlyList<InspectorSelectionSummary> SelectedItemSummaries { get; private set; } = [];
        public int SelectionCount => _selectedCanvasItems.Count > 0
            ? _selectedCanvasItems.Count
            : _selectedElement == null ? 0 : 1;
        public bool IsSingleSelection => _selectedElement != null;
        public bool IsMultipleSelection => _selectedCanvasItems.Count > 1;
        public bool IsDetailPanelOpen => IsSingleSelection || IsMultipleSelection;
        public NodeViewModel? SelectedOfflineSnapshotNode =>
            SelectedElement is NodeViewModel { IsOfflineSnapshot: true } node ? node : null;
        public ICommand? SelectedOfflineSnapshotDetailCommand =>
            SelectedOfflineSnapshotNode?.OpenDetailWindowCommand;
        public NodeViewModel? SelectedFailedCreationNode =>
            SelectedElement is NodeViewModel { IsCreationFailed: true } node ? node : null;
        public bool IsSelectedCreationFailed => SelectedFailedCreationNode != null;
        public bool IsSelectedDockerDisconnected => SelectedElement switch
        {
            NodeViewModel node => node.IsDockerDisconnected,
            GroupViewModel group => group.IsDockerDisconnected,
            _ => false
        };
        public bool IsSelectedOfflineSnapshot => SelectedElement is NodeViewModel { IsOfflineSnapshot: true };
        public bool IsSelectedRuntimeDisconnected => IsSelectedDockerDisconnected && !IsSelectedOfflineSnapshot;
        public bool IsSelectedDockerConnected => !IsSelectedDockerDisconnected;
        public bool CanDeleteSelected =>
            !IsMultipleSelection &&
            SelectedElement is not GroupViewModel { Type: GroupType.Network, IsBuiltInDockerNetwork: true };

        public bool IsCanvasItemSelected(ConnectableItemViewModel item) =>
            _selectedCanvasItems.Any(selected => ReferenceEquals(selected, item));

        public void SelectCanvasItem(ConnectableItemViewModel item, bool additive)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (additive)
            {
                ToggleCanvasItem(item);
                return;
            }

            ApplySelection([item], item);
        }

        public void AddCanvasItems(
            IEnumerable<ConnectableItemViewModel> items,
            bool preserveCurrentSelection)
        {
            ArgumentNullException.ThrowIfNull(items);
            var next = preserveCurrentSelection
                ? new List<ConnectableItemViewModel>(_selectedCanvasItems)
                : [];

            foreach (ConnectableItemViewModel item in items)
            {
                if (!next.Any(selected => ReferenceEquals(selected, item)))
                    next.Add(item);
            }

            ApplySelection(next, next.Count == 1 ? next[0] : null);
        }

        public void ToggleCanvasItem(ConnectableItemViewModel item)
        {
            ArgumentNullException.ThrowIfNull(item);
            var next = new List<ConnectableItemViewModel>(_selectedCanvasItems);
            int index = next.FindIndex(selected => ReferenceEquals(selected, item));
            if (index >= 0)
                next.RemoveAt(index);
            else
                next.Add(item);

            ApplySelection(next, next.Count == 1 ? next[0] : null);
        }

        public void ClearSelection() => ApplySelection([], null);

        private void FocusSelection(object? parameter)
        {
            if (parameter is ConnectableItemViewModel item)
                ApplySelection([item], item);
        }

        private void ApplySelection(
            IEnumerable<ConnectableItemViewModel> canvasItems,
            object? primaryElement)
        {
            var normalized = new List<ConnectableItemViewModel>();
            foreach (ConnectableItemViewModel item in canvasItems)
            {
                if (!normalized.Any(selected => ReferenceEquals(selected, item)))
                    normalized.Add(item);
            }

            if (normalized.Count > 1)
                primaryElement = null;
            else if (normalized.Count == 1)
                primaryElement = normalized[0];

            if (!ReferenceEquals(_selectedElement, primaryElement))
            {
                if (_selectedElement is INotifyPropertyChanged oldNotify)
                    oldNotify.PropertyChanged -= SelectedElement_PropertyChanged;

                _selectedElement = primaryElement;

                if (_selectedElement is INotifyPropertyChanged newNotify)
                    newNotify.PropertyChanged += SelectedElement_PropertyChanged;
            }

            UnsubscribeSelectionSummaryChanges();
            _selectedCanvasItems.Clear();
            _selectedCanvasItems.AddRange(normalized);
            SubscribeSelectionSummaryChanges();
            RebuildSelectionSummaries();

            SynchronizeVisualSelection();
            OnPropertyChanged(nameof(SelectedElement));
            OnPropertyChanged(nameof(SelectedCanvasItems));
            OnPropertyChanged(nameof(SelectionCount));
            OnPropertyChanged(nameof(IsSingleSelection));
            OnPropertyChanged(nameof(IsMultipleSelection));
            OnPropertyChanged(nameof(IsDetailPanelOpen));
            RaiseSelectionStateChanged();
            RefreshSelectedDetails();
        }

        private void SynchronizeVisualSelection()
        {
            if (_mainVm.ActiveSheet == null)
                return;

            foreach (NodeViewModel node in _mainVm.ActiveSheet.Nodes)
            {
                bool selected = _selectedCanvasItems.Any(item => ReferenceEquals(item, node));
                node.IsSelected = selected;
                node.IsPrimarySelection = ReferenceEquals(node, _selectedElement);
            }

            foreach (GroupViewModel group in _mainVm.ActiveSheet.Groups)
            {
                bool selected = _selectedCanvasItems.Any(item => ReferenceEquals(item, group));
                group.IsSelected = selected;
                group.IsPrimarySelection = ReferenceEquals(group, _selectedElement);
            }

            foreach (ConnectorViewModel connector in _mainVm.ActiveSheet.Connectors)
                connector.IsSelected = ReferenceEquals(connector, _selectedElement);
        }

        private void RefreshSelectedDetails()
        {
            // NodeViewModel은 IsPrimarySelection 변경 시 자신에게 맞는 상세 조회를 한 번만 실행합니다.
            // 여기서 다시 호출하면 컨테이너/볼륨 inspect가 중복되므로 네트워크 그룹만 직접 갱신합니다.
            if (_selectedElement is GroupViewModel { Type: GroupType.Network, IsDockerConnected: true } networkVm)
            {
                _ = networkVm.RefreshNetworkDetailsAsync();
            }
        }

        private void SubscribeSelectionSummaryChanges()
        {
            foreach (ConnectableItemViewModel item in _selectedCanvasItems)
                item.PropertyChanged += SelectedCanvasItem_PropertyChanged;

            foreach (GroupViewModel group in _selectedCanvasItems.OfType<GroupViewModel>())
                group.ContainedNodes.CollectionChanged += SelectedGroupContainedNodes_CollectionChanged;
        }

        private void UnsubscribeSelectionSummaryChanges()
        {
            foreach (ConnectableItemViewModel item in _selectedCanvasItems)
                item.PropertyChanged -= SelectedCanvasItem_PropertyChanged;

            foreach (GroupViewModel group in _selectedCanvasItems.OfType<GroupViewModel>())
                group.ContainedNodes.CollectionChanged -= SelectedGroupContainedNodes_CollectionChanged;
        }

        private void SelectedCanvasItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ConnectableItemViewModel.Name)
                or nameof(ConnectableItemViewModel.ResourceKind)
                or nameof(NodeViewModel.ImageName)
                or nameof(NodeViewModel.Driver))
            {
                RebuildSelectionSummaries();
            }
        }

        private void SelectedGroupContainedNodes_CollectionChanged(
            object? sender,
            NotifyCollectionChangedEventArgs e) => RebuildSelectionSummaries();

        private void RebuildSelectionSummaries()
        {
            SelectedItemSummaries = _selectedCanvasItems
                .Select(CreateSelectionSummary)
                .ToList();
            OnPropertyChanged(nameof(SelectedItemSummaries));
        }

        private static InspectorSelectionSummary CreateSelectionSummary(
            ConnectableItemViewModel item)
        {
            string kindLabel = item switch
            {
                NodeViewModel { ResourceKind: RuntimeResourceKind.SwarmService } => "Swarm 서비스",
                NodeViewModel { ResourceKind: RuntimeResourceKind.SwarmSecret } => "Swarm 시크릿",
                NodeViewModel { ResourceKind: RuntimeResourceKind.SwarmConfig } => "Swarm 구성",
                NodeViewModel { ResourceKind: RuntimeResourceKind.SwarmVolume } => "Swarm 볼륨",
                NodeViewModel { Type: NodeType.Container } => "컨테이너",
                NodeViewModel { Type: NodeType.Volume } => "볼륨",
                NodeViewModel { Type: NodeType.HostPath } => "호스트 경로",
                NodeViewModel { Type: NodeType.Internet } => "인터넷",
                GroupViewModel { Type: GroupType.Network } => "네트워크",
                GroupViewModel => "그룹",
                _ => "항목"
            };

            string detail = item switch
            {
                NodeViewModel { Type: NodeType.Container } node => node.ImageName,
                NodeViewModel { Type: NodeType.Volume } node => $"driver · {node.Driver}",
                NodeViewModel { Type: NodeType.HostPath } node => node.HostPath,
                GroupViewModel { Type: GroupType.Network } group => $"driver · {group.Driver}",
                GroupViewModel group => $"포함된 노드 {group.ContainedNodes.Count}개",
                _ => string.Empty
            };

            return new InspectorSelectionSummary(item, kindLabel, detail);
        }

        private void SelectedElement_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NodeViewModel.IsDockerConnected) ||
                e.PropertyName == nameof(NodeViewModel.IsDockerDisconnected) ||
                e.PropertyName == nameof(NodeViewModel.IsOfflineSnapshot) ||
                e.PropertyName == nameof(NodeViewModel.IsRuntimeUnavailable) ||
                e.PropertyName == nameof(NodeViewModel.IsCreationFailed) ||
                e.PropertyName == nameof(NodeViewModel.LastCreationError) ||
                e.PropertyName == nameof(GroupViewModel.IsDockerConnected) ||
                e.PropertyName == nameof(GroupViewModel.IsDockerDisconnected) ||
                e.PropertyName == nameof(GroupViewModel.IsBuiltInDockerNetwork))
            {
                RaiseSelectionStateChanged();
            }
        }

        private void RaiseSelectionStateChanged()
        {
            OnPropertyChanged(nameof(IsSelectedDockerDisconnected));
            OnPropertyChanged(nameof(IsSelectedDockerConnected));
            OnPropertyChanged(nameof(CanDeleteSelected));
            OnPropertyChanged(nameof(IsSelectedOfflineSnapshot));
            OnPropertyChanged(nameof(IsSelectedRuntimeDisconnected));
            OnPropertyChanged(nameof(SelectedOfflineSnapshotNode));
            OnPropertyChanged(nameof(SelectedOfflineSnapshotDetailCommand));
            OnPropertyChanged(nameof(SelectedFailedCreationNode));
            OnPropertyChanged(nameof(IsSelectedCreationFailed));
            ReconnectCommand?.RaiseCanExecuteChanged();
        }

        private async Task ReconnectSelectedAsync()
        {
            bool reconnected = SelectedElement switch
            {
                NodeViewModel node => await node.ReconnectDockerResourceAsync(),
                GroupViewModel group => await group.ReconnectDockerResourceAsync(),
                _ => false
            };

            if (reconnected)
            {
                if (SelectedElement is GroupViewModel { Type: GroupType.Network } network)
                    await network.RefreshNetworkDetailsAsync();

                _mainVm.Explorer.UpdateAvailableItems();
                RaiseSelectionStateChanged();
            }
        }

        /// <summary>
        /// 선택된 요소(선, 컨테이너, 볼륨, 네트워크 그룹 등)를 삭제합니다.
        /// MainViewModel에 있던 거대한 삭제 로직을 이곳으로 완전히 캡슐화했습니다.
        /// </summary>
        public async Task DeleteSelectedAsync()
        {
            var sheet = _mainVm.ActiveSheet;
            if (SelectedElement == null || sheet == null) return;

            // 현재 시트의 접속 상태에 맞는 도커 서비스 획득
            var containerService = (IContainerService)sheet.DockerService;
            var volumeService = (IVolumeService)sheet.DockerService;
            var networkService = (INetworkService)sheet.DockerService;

            // =========================================================
            // [CASE 1] 연결선(Connector) 삭제 시
            // =========================================================
            if (SelectedElement is ConnectorViewModel conn)
            {
                bool isDraftConnector = new[] { conn.Source, conn.Target }
                    .OfType<ConnectableItemViewModel>()
                    .Any(item => item.IsDraft);

                if (isDraftConnector || conn.RelationType == RelationType.Dependency)
                {
                    await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateConnectorDeleteCommand(sheet, conn));
                }
                else if (DiagramDeletionSupport.RequiresStandaloneVolumeUnmount(
                             sheet.RuntimeKind,
                             conn.RelationType))
                {
                    var result = _dialogService.ShowYesNoCancel(
                        "실제 Docker 컨테이너에서도 마운트 연결을 해제하시겠습니까?\n" +
                        "[예(Yes)] : Docker에서 해제 (물리적 해제 - 재생성)\n" +
                        "[아니요(No)] : 시트에서만 제거 (논리적 삭제)\n" +
                        "[취소(Cancel)] : 작업 취소",
                        "마운트 연결 해제");

                    if (result == DialogChoice.Cancel) return;

                    if (result == DialogChoice.Yes)
                    {
                        NodeViewModel? containerNode = new[] { conn.Source, conn.Target }
                            .OfType<NodeViewModel>()
                            .FirstOrDefault(node => node.Type == NodeType.Container);
                        NodeViewModel? storageNode = new[] { conn.Source, conn.Target }
                            .OfType<NodeViewModel>()
                            .FirstOrDefault(node => node.Type is NodeType.Volume or NodeType.HostPath);

                        if (containerNode != null && storageNode != null)
                        {
                            bool success = await UnmountStorageFromContainerAsync(containerNode, storageNode, conn, containerService);
                            if (success) sheet.Connectors.Remove(conn);
                        }
                        else
                        {
                            await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateConnectorDeleteCommand(sheet, conn));
                        }
                    }
                    else
                    {
                        await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateConnectorDeleteCommand(sheet, conn));
                    }
                }
                else
                {
                    await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateConnectorDeleteCommand(sheet, conn));
                }
                _mainVm.IsModified = true;
            }

            // =========================================================
            // [CASE 2] 노드(Node) 삭제 시
            // =========================================================
            else if (SelectedElement is NodeViewModel node)
            {
                if (node.IsDraft || node.Type == NodeType.Internet)
                {
                    await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateNodeDeleteCommand(sheet, node, deleteDocker: false));
                    SelectedElement = null;
                    return;
                }

                if (node.ResourceKind is RuntimeResourceKind.SwarmVolume or RuntimeResourceKind.SwarmExternalTraffic)
                {
                    if (!_dialogService.ShowConfirm(
                            $"'{node.Name}'은(는) Swarm service 설정을 표현하는 다이어그램 항목입니다.\n다이어그램에서 제거하시겠습니까?",
                            "Swarm Diagram Resource"))
                    {
                        return;
                    }

                    await _mainVm.History.ExecuteAndRecordAsync(
                        _mainVm.CreateNodeDeleteCommand(sheet, node, deleteDocker: false));
                    SelectedElement = null;
                    return;
                }

                if (node.ResourceKind is RuntimeResourceKind.SwarmService or RuntimeResourceKind.SwarmSecret or RuntimeResourceKind.SwarmConfig)
                {
                    await DeleteSwarmRuntimeNodeAsync(sheet, node);
                    return;
                }

                if (node.IsDockerDisconnected)
                {
                    if (!_dialogService.ShowConfirm(
                            $"'{node.Name}'은(는) 현재 Docker와 연결되어 있지 않습니다.\n다이어그램에서만 삭제하시겠습니까?",
                            "끊긴 항목 삭제"))
                    {
                        return;
                    }

                    await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateNodeDeleteCommand(sheet, node, deleteDocker: false));
                    SelectedElement = null;
                    return;
                }

                if (node.Type == NodeType.HostPath)
                {
                    if (!_dialogService.ShowConfirm(
                            $"호스트 경로 참조 '{node.HostPath}'을(를) 시트에서 제거하시겠습니까?\n실제 호스트 파일과 폴더는 삭제되지 않습니다.",
                            "Host Path 참조 삭제"))
                    {
                        return;
                    }

                    await _mainVm.History.ExecuteAndRecordAsync(
                        _mainVm.CreateNodeDeleteCommand(sheet, node, deleteDocker: false));
                    SelectedElement = null;
                    return;
                }

                var result = _dialogService.ShowYesNoCancel(
                    "선택한 항목을 삭제하시겠습니까?\n" +
                    "[예(Yes)] : Docker에서도 영구 삭제\n" +
                    "[아니요(No)] : 시트에서만 제거\n" +
                    "[취소(Cancel)] : 취소",
                    "삭제 옵션");

                if (result == DialogChoice.Cancel) return;

                bool deleteDocker = result == DialogChoice.Yes;
                bool forceVolumeDelete = false;
                bool deleteExternalVolume = false;

                if (deleteDocker && node.Type == NodeType.Volume)
                {
                    if (node.VolumeExternal && !_dialogService.ShowConfirm(
                            $"'{node.EffectiveVolumeName}'은(는) 기존 또는 외부 볼륨으로 표시되어 있습니다.\n" +
                            "계속하면 다른 컨테이너나 Compose 프로젝트가 사용하는 데이터도 영구 삭제될 수 있습니다.\n\n" +
                            "Docker에서 이 볼륨을 정말 삭제하시겠습니까?",
                            "외부 볼륨 영구 삭제"))
                    {
                        return;
                    }

                    var decision = await _mainVm.ConfirmVolumeDockerDeleteAsync(volumeService, node.EffectiveVolumeName, allowForceAttempt: true);
                    if (!decision.ShouldDelete) return;
                    forceVolumeDelete = decision.Force;
                    deleteExternalVolume = node.VolumeExternal;
                }

                try
                {
                    await _mainVm.History.ExecuteAndRecordAsync(
                        _mainVm.CreateNodeDeleteCommand(
                            sheet,
                            node,
                            deleteDocker,
                            forceVolumeDelete,
                            deleteExternalVolume));
                }
                catch (Exception ex)
                {
                    _dialogService.ShowError(
                        $"'{node.Name}' 삭제 실패:\n{ex.GetBaseException().Message}\n\n다이어그램 항목은 유지했습니다.",
                        "Delete Resource");
                    return;
                }
            }

            // =========================================================
            // [CASE 3] 그룹(Group) 삭제 시
            // =========================================================
            else if (SelectedElement is GroupViewModel group)
            {
                if (group.IsDraft)
                {
                    await _mainVm.History.ExecuteAndRecordAsync(
                        _mainVm.CreateGroupDeleteCommand(sheet, group, deleteDocker: false));
                    SelectedElement = null;
                    return;
                }

                if (group.Type == GroupType.Network && group.IsBuiltInDockerNetwork)
                {
                    _dialogService.ShowInfo(
                        $"'{group.DockerNetworkName}' is a built-in Docker network and cannot be deleted.",
                        "Delete Network");
                    return;
                }

                if (group.ResourceKind == RuntimeResourceKind.SwarmOverlayNetwork)
                {
                    await DeleteSwarmOverlayNetworkAsync(sheet, group, networkService);
                    return;
                }

                if (group.IsDockerDisconnected)
                {
                    if (!_dialogService.ShowConfirm(
                            $"'{group.Title}' 네트워크는 현재 Docker와 연결되어 있지 않습니다.\n다이어그램에서만 삭제하시겠습니까?",
                            "끊긴 항목 삭제"))
                    {
                        return;
                    }

                    await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateGroupDeleteCommand(sheet, group, deleteDocker: false));
                    SelectedElement = null;
                    return;
                }

                try
                {
                    await _mainVm.History.ExecuteAndRecordAsync(
                        _mainVm.CreateGroupDeleteCommand(sheet, group, deleteDocker: group.Type == GroupType.Network));
                }
                catch (Exception ex)
                {
                    _dialogService.ShowError(
                        $"'{group.Title}' 삭제 실패:\n{ex.GetBaseException().Message}\n\n다이어그램 항목은 유지했습니다.",
                        "Delete Network");
                    return;
                }
            }

            SelectedElement = null;
        }

        private async Task DeleteSwarmRuntimeNodeAsync(SheetViewModel sheet, NodeViewModel node)
        {
            string kind = node.ResourceKind switch
            {
                RuntimeResourceKind.SwarmService => "Service",
                RuntimeResourceKind.SwarmSecret => "Secret",
                _ => "Config"
            };
            DialogChoice choice = _dialogService.ShowYesNoCancel(
                $"Swarm {kind} '{node.Name}'을 삭제하시겠습니까?\n" +
                "[예(Yes)] : Swarm에서도 영구 삭제\n" +
                "[아니요(No)] : 다이어그램에서만 제거\n" +
                "[취소(Cancel)] : 취소",
                $"Delete Swarm {kind}");
            if (choice == DialogChoice.Cancel) return;

            if (choice == DialogChoice.No)
            {
                await _mainVm.History.ExecuteAndRecordAsync(
                    _mainVm.CreateNodeDeleteCommand(sheet, node, deleteDocker: false));
                SelectedElement = null;
                return;
            }

            try
            {
                if (node.ResourceKind == RuntimeResourceKind.SwarmService)
                {
                    if (sheet.DockerService is not ISwarmService swarmService)
                        throw new InvalidOperationException("활성 Swarm Manager 연결이 없습니다.");
                    await swarmService.RemoveSwarmServiceAsync(node.ContainerId);
                }
                else
                {
                    if (sheet.DockerService is not ISwarmDataResourceMutationService mutationService)
                        throw new InvalidOperationException("활성 Swarm Manager 연결이 없습니다.");
                    SwarmDataResourceKind dataKind = node.ResourceKind == RuntimeResourceKind.SwarmSecret
                        ? SwarmDataResourceKind.Secret
                        : SwarmDataResourceKind.Config;
                    await mutationService.RemoveSwarmDataResourceAsync(dataKind, node.ContainerId);
                }

                await sheet.RemoveNodeAsync(node);
                await sheet.NotifyRuntimeResourcesChangedAsync();
                SelectedElement = null;
                _mainVm.IsModified = true;
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Swarm {kind} 삭제 실패:\n{ex.GetBaseException().Message}",
                    $"Delete Swarm {kind}");
            }
        }

        private async Task DeleteSwarmOverlayNetworkAsync(
            SheetViewModel sheet,
            GroupViewModel group,
            INetworkService networkService)
        {
            DialogChoice choice = _dialogService.ShowYesNoCancel(
                $"Swarm overlay network '{group.Title}'을 삭제하시겠습니까?\n" +
                "[예(Yes)] : Swarm에서도 영구 삭제\n" +
                "[아니요(No)] : 다이어그램에서만 제거\n" +
                "[취소(Cancel)] : 취소",
                "Delete Swarm Overlay Network");
            if (choice == DialogChoice.Cancel) return;

            if (choice == DialogChoice.No)
            {
                await _mainVm.History.ExecuteAndRecordAsync(
                    _mainVm.CreateGroupDeleteCommand(sheet, group, deleteDocker: false));
                SelectedElement = null;
                return;
            }

            try
            {
                await networkService.RemoveNetworkAsync(
                    string.IsNullOrWhiteSpace(group.Id) ? group.DockerNetworkName : group.Id);
                RemoveGroupFromSheetOnly(sheet, group);
                await sheet.NotifyRuntimeResourcesChangedAsync();
                SelectedElement = null;
                _mainVm.IsModified = true;
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Swarm overlay network 삭제 실패:\n{ex.GetBaseException().Message}",
                    "Delete Swarm Overlay Network");
            }
        }

        private static void RemoveGroupFromSheetOnly(SheetViewModel sheet, GroupViewModel group)
        {
            var relatedConnectors = sheet.Connectors
                .Where(c => c.Source == (IConnectableItem)group || c.Target == (IConnectableItem)group).ToList();
            DiagramDeletionSupport.RemoveGroupFromDiagram(sheet, group, relatedConnectors);
        }

        private async Task<bool> UnmountStorageFromContainerAsync(
            NodeViewModel containerNode,
            NodeViewModel storageNode,
            ConnectorViewModel connector,
            IContainerService containerService)
        {
            string containerId = containerNode.ContainerId;
            MountKind mountKind = storageNode.Type == NodeType.HostPath ? MountKind.Bind : MountKind.Volume;
            string sourceToRemove = storageNode.Type == NodeType.HostPath
                ? storageNode.HostPath
                : storageNode.EffectiveVolumeName;
            string targetToRemove = connector.MountPath ?? string.Empty;
            bool keepBackup = false;
            bool originalRemoved = false;
            bool wasRunning = false;
            string? replacementId = null;
            ContainerInspectResponse? inspect = null;
            string tempHostPath = Path.Combine(Path.GetTempPath(), "docker_backup_" + Guid.NewGuid());

            _dialogService.SetBusyCursor(true);
            try
            {
                inspect = await containerService.InspectContainerAsync(containerId);
                if (inspect.HostConfig?.AutoRemove == true)
                    throw new InvalidOperationException(
                        "--rm(AutoRemove) 컨테이너는 중지 시 즉시 삭제되므로 안전한 마운트 해제를 지원하지 않습니다.");
                wasRunning = inspect.State?.Running == true;
                MountPoint? targetMount = inspect.Mounts?.FirstOrDefault(mount =>
                    ContainerMountMapper.ParseKind(mount.Type) == mountKind &&
                    string.Equals(mount.Destination, targetToRemove, StringComparison.Ordinal));
                if (targetMount == null)
                {
                    _dialogService.ShowError(
                        $"컨테이너에서 '{sourceToRemove} → {targetToRemove}' 마운트를 찾을 수 없습니다.",
                        "마운트 연결 해제");
                    return false;
                }

                if (wasRunning && inspect.HostConfig?.AutoRemove != true)
                    await containerService.StopContainerAsync(containerId);
                if (!Directory.Exists(tempHostPath)) Directory.CreateDirectory(tempHostPath);

                string mountPath = targetMount.Destination;

                await containerService.CopyFromContainerAsync(
                    containerId,
                    mountPath,
                    tempHostPath,
                    _mainVm.LifetimeToken);

                var newVolumes = inspect.HostConfig?.Binds?
                    .Where(bind => !IsMatchingLegacyMount(bind, mountKind, sourceToRemove, targetToRemove))
                    .ToList() ?? [];
                var newMounts = inspect.HostConfig?.Mounts?
                    .Where(mount => !(ContainerMountMapper.ParseKind(mount.Type) == mountKind &&
                                      string.Equals(mount.Target, targetToRemove, StringComparison.Ordinal)))
                    .ToList();

                await containerService.RemoveContainerAsync(containerId);
                originalRemoved = true;

                replacementId = await containerService.RecreateContainerFromInspectAsync(
                    inspect.Name,
                    inspect,
                    newVolumes,
                    startContainer: wasRunning,
                    mounts: newMounts);

                string folderName = Path.GetFileName(mountPath.TrimEnd('/'));
                string actualSourcePath = Path.Combine(tempHostPath, folderName);

                if (Directory.Exists(actualSourcePath))
                    await containerService.CopyToContainerAsync(
                        replacementId,
                        actualSourcePath,
                        mountPath,
                        _mainVm.LifetimeToken);
                else
                    await containerService.CopyToContainerAsync(
                        replacementId,
                        tempHostPath,
                        mountPath,
                        _mainVm.LifetimeToken);

                containerNode.ContainerId = replacementId;
                await containerNode.RefreshDetailsAsync();

                _dialogService.ShowMessage("마운트 연결 해제 및 컨테이너 재생성이 완료되었습니다.");
                return true;
            }
            catch (Exception ex)
            {
                ContainerRecoveryResult recovery = await ContainerReplacementRecovery.RecoverAsync(
                    containerNode,
                    containerService,
                    containerId,
                    inspect,
                    originalRemoved,
                    wasRunning,
                    replacementId);
                keepBackup = recovery.KeepBackup;

                string backupMessage = keepBackup ? $"\n\n백업: {tempHostPath}" : string.Empty;
                _dialogService.ShowMessage(
                    $"해제 중 오류 발생: {ex.GetBaseException().Message}{recovery.UserMessage}{backupMessage}");
                return false;
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
                if (!keepBackup)
                    ContainerReplacementRecovery.DeleteTemporaryDirectory(tempHostPath);
            }
        }

        private static bool IsMatchingLegacyMount(
            string value,
            MountKind kind,
            string source,
            string target)
        {
            try
            {
                ContainerMountSpec parsed = ContainerMountParser.ParseLegacy(value);
                StringComparison sourceComparison = source.Length >= 2 && source[1] == ':'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                return parsed.Kind == kind &&
                       string.Equals(parsed.Source, source, sourceComparison) &&
                       string.Equals(parsed.Target, target, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
