using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DockerDiagram.Models;
using DockerDiagram.Helpers;

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
        public AsyncRelayCommand ReconnectCommand { get; }

        public InspectorViewModel(MainViewModel mainVm, IDialogService dialogService)
        {
            _mainVm = mainVm;
            _dialogService = dialogService;

            ClosePanelCommand = new RelayCommand(_ => ClearSelection());
            DeleteCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync());
            ReconnectCommand = new AsyncRelayCommand(_ => ReconnectSelectedAsync(), _ => IsSelectedDockerDisconnected);
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
                    if (nodeVm.IsDockerConnected)
                        _ = nodeVm.RefreshDetailsAsync();
                }
            }
        }

        // 상세 정보 사이드 패널의 열림/닫힘 상태
        public bool IsDetailPanelOpen => _selectedElement != null;
        public bool IsSelectedDockerDisconnected => SelectedElement switch
        {
            NodeViewModel node => node.IsDockerDisconnected,
            GroupViewModel group => group.IsDockerDisconnected,
            _ => false
        };
        public bool IsSelectedDockerConnected => !IsSelectedDockerDisconnected;

        public void ClearSelection() => SelectedElement = null;

        private void SelectedElement_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NodeViewModel.IsDockerConnected) ||
                e.PropertyName == nameof(NodeViewModel.IsDockerDisconnected) ||
                e.PropertyName == nameof(GroupViewModel.IsDockerConnected) ||
                e.PropertyName == nameof(GroupViewModel.IsDockerDisconnected))
            {
                RaiseSelectionStateChanged();
            }
        }

        private void RaiseSelectionStateChanged()
        {
            OnPropertyChanged(nameof(IsSelectedDockerDisconnected));
            OnPropertyChanged(nameof(IsSelectedDockerConnected));
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
                if (conn.RelationType == RelationType.Dependency)
                {
                    await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateConnectorDeleteCommand(sheet, conn));
                }
                else if (conn.RelationType == RelationType.VolumeMount)
                {
                    var result = _dialogService.ShowYesNoCancel(
                        "실제 Docker 컨테이너에서도 볼륨 연결을 해제하시겠습니까?\n" +
                        "[예(Yes)] : Docker에서 해제 (물리적 해제 - 재생성)\n" +
                        "[아니요(No)] : 시트에서만 제거 (논리적 삭제)\n" +
                        "[취소(Cancel)] : 작업 취소",
                        "볼륨 연결 해제");

                    if (result == System.Windows.MessageBoxResult.Cancel) return;

                    if (result == System.Windows.MessageBoxResult.Yes)
                    {
                        if (conn.Source is NodeViewModel srcNode && conn.Target is NodeViewModel tgtNode)
                        {
                            bool success = await UnmountVolumeFromContainerAsync(srcNode, tgtNode, containerService);
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
                _mainVm.IsModified = true;
            }

            // =========================================================
            // [CASE 2] 노드(Node) 삭제 시
            // =========================================================
            else if (SelectedElement is NodeViewModel node)
            {
                if (node.Type == NodeType.Internet)
                {
                    await _mainVm.History.ExecuteAndRecordAsync(_mainVm.CreateNodeDeleteCommand(sheet, node, deleteDocker: false));
                    SelectedElement = null;
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

                if (result == System.Windows.MessageBoxResult.Cancel) return;

                await _mainVm.History.ExecuteAndRecordAsync(
                    _mainVm.CreateNodeDeleteCommand(sheet, node, deleteDocker: result == System.Windows.MessageBoxResult.Yes));
            }

            // =========================================================
            // [CASE 3] 그룹(Group) 삭제 시
            // =========================================================
            else if (SelectedElement is GroupViewModel group)
            {
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

                await _mainVm.History.ExecuteAndRecordAsync(
                    _mainVm.CreateGroupDeleteCommand(sheet, group, deleteDocker: group.Type == GroupType.Network));
            }

            SelectedElement = null;
        }

        private static void RemoveGroupFromSheetOnly(SheetViewModel sheet, GroupViewModel group)
        {
            if (group.ContainedNodes != null)
            {
                foreach (var childNode in group.ContainedNodes.ToList())
                {
                    childNode.X += group.X;
                    childNode.Y += group.Y;
                    if (!sheet.Nodes.Contains(childNode))
                        sheet.Nodes.Add(childNode);
                }
            }

            var relatedConnectors = sheet.Connectors
                .Where(c => c.Source == (IConnectableItem)group || c.Target == (IConnectableItem)group).ToList();
            foreach (var c in relatedConnectors) sheet.Connectors.Remove(c);

            sheet.Groups.Remove(group);
        }

        private async Task<bool> UnmountVolumeFromContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode, IContainerService containerService)
        {
            string containerId = containerNode.ContainerId;
            string volumeNameToRemove = volumeNode.Name;
            bool keepBackup = false;
            string tempHostPath = Path.Combine(Path.GetTempPath(), "docker_backup_" + Guid.NewGuid());

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                if (containerNode.IsRunning) await containerService.StopContainerAsync(containerId);
                if (!Directory.Exists(tempHostPath)) Directory.CreateDirectory(tempHostPath);

                var inspect = await containerService.InspectContainerAsync(containerId);
                string mountPath = "/data";
                foreach (var m in inspect.Mounts)
                {
                    if (m.Name == volumeNameToRemove) { mountPath = m.Destination; break; }
                }

                await containerService.CopyFromContainerAsync(containerId, mountPath, tempHostPath);

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

                var envs = oldConfig.Env != null ? oldConfig.Env.ToList() : new System.Collections.Generic.List<string>();

                var ports = new System.Collections.Generic.List<string>();
                if (oldHostConfig.PortBindings != null)
                {
                    foreach (var pb in oldHostConfig.PortBindings)
                    {
                        string containerPort = pb.Key.Split('/')[0];
                        if (pb.Value != null && pb.Value.Count > 0)
                            ports.Add($"{pb.Value[0].HostPort}:{containerPort}");
                    }
                }

                var newVolumes = new System.Collections.Generic.List<string>();
                if (oldHostConfig.Binds != null)
                {
                    foreach (var bind in oldHostConfig.Binds)
                    {
                        if (!bind.StartsWith(volumeNameToRemove + ":")) newVolumes.Add(bind);
                    }
                }

                string command = oldConfig.Cmd != null ? string.Join(" ", oldConfig.Cmd) : "";
                bool tty = oldConfig.Tty;

                await containerService.RemoveContainerAsync(containerId);

                string newId = await containerService.CreateAndStartContainerAsync(
                    containerNode.Name, imgRepo, imgTag, ports, envs, newVolumes,
                    oldHostConfig.RestartPolicy.Name.ToString(), 0, 0, command, tty);

                containerNode.ContainerId = newId;

                string folderName = Path.GetFileName(mountPath.TrimEnd('/'));
                string actualSourcePath = Path.Combine(tempHostPath, folderName);

                if (Directory.Exists(actualSourcePath))
                    await containerService.CopyToContainerAsync(newId, actualSourcePath, mountPath);
                else
                    await containerService.CopyToContainerAsync(newId, tempHostPath, mountPath);

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
                    try { Directory.Delete(tempHostPath, true); } catch { }
                }
            }
        }
    }
}
