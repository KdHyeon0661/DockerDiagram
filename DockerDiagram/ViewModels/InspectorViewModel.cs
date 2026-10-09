using DockerDiagram.Diagram;
using DockerDiagram.Contracts;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DockerDiagram.Models;
using Docker.DotNet.Models;

namespace DockerDiagram.ViewModels
{
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
        public AsyncRelayCommand ReconnectCommand { get; }

        public InspectorViewModel(MainViewModel mainVm, IDialogService dialogService)
        {
            _mainVm = mainVm;
            _dialogService = dialogService;

            ClosePanelCommand = new RelayCommand(_ => ClearSelection());
            DeleteCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync());
            SetConnectorDirectionCommand = new RelayCommand(SetConnectorDirection);
            ReverseConnectorDirectionCommand = new RelayCommand(_ => ReverseConnectorDirection());
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

        // 캔버스 위에서 현재 선택된 요소(노드, 선, 그룹 등)
        private object? _selectedElement;
        public object? SelectedElement
        {
            get => _selectedElement;
            set
            {
                if (_selectedElement == value) return;

                if (_selectedElement is INotifyPropertyChanged oldNotify)
                    oldNotify.PropertyChanged -= SelectedElement_PropertyChanged;

                _selectedElement = value;
                if (_selectedElement is INotifyPropertyChanged newNotify)
                    newNotify.PropertyChanged += SelectedElement_PropertyChanged;

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDetailPanelOpen));
                RaiseSelectionStateChanged();

                // 1. 활성 시트 내의 시각적 선택 상태(IsSelected) 동기화
                if (_mainVm.ActiveSheet != null)
                {
                    foreach (var node in _mainVm.ActiveSheet.Nodes) node.IsSelected = (node == value);
                    foreach (var conn in _mainVm.ActiveSheet.Connectors) conn.IsSelected = (conn == value);
                    foreach (var group in _mainVm.ActiveSheet.Groups) group.IsSelected = (group == value);
                }

                // 2. 노드가 선택되었다면, 상세 정보(Inspect)를 비동기로 갱신.
                if (_selectedElement is NodeViewModel nodeVm)
                {
                    if (nodeVm.IsDockerConnected && !nodeVm.IsKubernetesResource)
                        _ = nodeVm.RefreshDetailsAsync();
                }
                else if (_selectedElement is GroupViewModel { Type: GroupType.Network, IsDockerConnected: true } networkVm)
                {
                    _ = networkVm.RefreshNetworkDetailsAsync();
                }
            }
        }

        // 상세 정보 사이드 패널의 열림/닫힘 상태
        public bool IsDetailPanelOpen => _selectedElement != null;
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
            SelectedElement is not GroupViewModel { Type: GroupType.Network, IsBuiltInDockerNetwork: true };

        public void ClearSelection() => SelectedElement = null;

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
                        "실제 Docker 컨테이너에서도 볼륨 연결을 해제하시겠습니까?\n" +
                        "[예(Yes)] : Docker에서 해제 (물리적 해제 - 재생성)\n" +
                        "[아니요(No)] : 시트에서만 제거 (논리적 삭제)\n" +
                        "[취소(Cancel)] : 작업 취소",
                        "볼륨 연결 해제");

                    if (result == DialogChoice.Cancel) return;

                    if (result == DialogChoice.Yes)
                    {
                        NodeViewModel? containerNode = new[] { conn.Source, conn.Target }
                            .OfType<NodeViewModel>()
                            .FirstOrDefault(node => node.Type == NodeType.Container);
                        NodeViewModel? volumeNode = new[] { conn.Source, conn.Target }
                            .OfType<NodeViewModel>()
                            .FirstOrDefault(node => node.Type == NodeType.Volume);

                        if (containerNode != null && volumeNode != null)
                        {
                            bool success = await UnmountVolumeFromContainerAsync(containerNode, volumeNode, containerService);
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

                var result = _dialogService.ShowYesNoCancel(
                    "선택한 항목을 삭제하시겠습니까?\n" +
                    "[예(Yes)] : Docker에서도 영구 삭제\n" +
                    "[아니요(No)] : 시트에서만 제거\n" +
                    "[취소(Cancel)] : 취소",
                    "삭제 옵션");

                if (result == DialogChoice.Cancel) return;

                bool deleteDocker = result == DialogChoice.Yes;
                bool forceVolumeDelete = false;

                if (deleteDocker && node.Type == NodeType.Volume && !node.VolumeExternal)
                {
                    var decision = await _mainVm.ConfirmVolumeDockerDeleteAsync(volumeService, node.EffectiveVolumeName, allowForceAttempt: true);
                    if (!decision.ShouldDelete) return;
                    forceVolumeDelete = decision.Force;
                }

                try
                {
                    await _mainVm.History.ExecuteAndRecordAsync(
                        _mainVm.CreateNodeDeleteCommand(sheet, node, deleteDocker, forceVolumeDelete));
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

        private async Task<bool> UnmountVolumeFromContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode, IContainerService containerService)
        {
            string containerId = containerNode.ContainerId;
            string volumeNameToRemove = volumeNode.EffectiveVolumeName;
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
                wasRunning = inspect.State?.Running == true;
                MountPoint? targetMount = inspect.Mounts?.FirstOrDefault(mount =>
                    string.Equals(mount.Name, volumeNameToRemove, StringComparison.OrdinalIgnoreCase));
                if (targetMount == null)
                {
                    _dialogService.ShowError(
                        $"컨테이너에서 볼륨 '{volumeNameToRemove}' 마운트를 찾을 수 없습니다.",
                        "볼륨 연결 해제");
                    return false;
                }

                if (wasRunning && inspect.HostConfig?.AutoRemove != true)
                    await containerService.StopContainerAsync(containerId);
                if (!Directory.Exists(tempHostPath)) Directory.CreateDirectory(tempHostPath);

                string mountPath = targetMount.Destination;

                await containerService.CopyFromContainerAsync(containerId, mountPath, tempHostPath);

                var newVolumes = inspect.HostConfig?.Binds?
                    .Where(bind => !bind.StartsWith(volumeNameToRemove + ":", StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? new System.Collections.Generic.List<string>();
                var newMounts = inspect.HostConfig?.Mounts?
                    .Where(mount => !(string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(mount.Source, volumeNameToRemove, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                await containerService.RemoveContainerAsync(containerId);
                originalRemoved = true;

                replacementId = await containerService.RecreateContainerFromInspectAsync(
                    inspect.Name,
                    inspect,
                    newVolumes,
                    startContainer: true,
                    mounts: newMounts);

                string folderName = Path.GetFileName(mountPath.TrimEnd('/'));
                string actualSourcePath = Path.Combine(tempHostPath, folderName);

                if (Directory.Exists(actualSourcePath))
                    await containerService.CopyToContainerAsync(replacementId, actualSourcePath, mountPath);
                else
                    await containerService.CopyToContainerAsync(replacementId, tempHostPath, mountPath);

                if (!wasRunning)
                    await containerService.StopContainerAsync(replacementId);

                containerNode.ContainerId = replacementId;
                await containerNode.RefreshDetailsAsync();

                _dialogService.ShowMessage("볼륨 연결 해제 및 컨테이너 재생성이 완료되었습니다.");
                return true;
            }
            catch (Exception ex)
            {
                keepBackup = true;
                string recoveryMessage = string.Empty;
                if (originalRemoved && inspect != null)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(replacementId))
                        {
                            try { await containerService.RemoveContainerAsync(replacementId); } catch { }
                        }

                        string restoredId = await containerService.RecreateContainerFromInspectAsync(
                            inspect.Name,
                            inspect,
                            inspect.HostConfig?.Binds?.ToList() ?? new System.Collections.Generic.List<string>(),
                            wasRunning);
                        containerNode.ContainerId = restoredId;
                        await containerNode.RefreshDetailsAsync();
                        keepBackup = false;
                        recoveryMessage = "\n원본 컨테이너 설정은 복구했습니다.";
                    }
                    catch (Exception recoveryEx)
                    {
                        recoveryMessage = $"\n원본 컨테이너 자동 복구도 실패했습니다: {recoveryEx.GetBaseException().Message}";
                    }
                }
                else if (wasRunning)
                {
                    try { await containerService.StartContainerAsync(containerId); } catch { }
                }

                string backupMessage = keepBackup ? $"\n\n백업: {tempHostPath}" : string.Empty;
                _dialogService.ShowMessage($"해제 중 오류 발생: {ex.GetBaseException().Message}{recoveryMessage}{backupMessage}");
                return false;
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
                if (!keepBackup && Directory.Exists(tempHostPath))
                {
                    try { Directory.Delete(tempHostPath, true); } catch { }
                }
            }
        }
    }
}
