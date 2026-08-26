using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Common;
using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DockerDiagram.ViewModels
{
    public sealed class EnvironmentVariableDisplayItem
    {
        public required string Key { get; init; }
        public required string Value { get; init; }
        public string CopyText => $"{Key}={Value}";
    }

    public sealed class PortBindingDisplayItem
    {
        public required string HostIp { get; init; }
        public required string HostPort { get; init; }
        public required string ContainerPort { get; init; }
        public required string Protocol { get; init; }
        public string CopyText => $"{HostIp}:{HostPort} -> {ContainerPort}/{Protocol}";
    }

    public sealed class MountDisplayItem
    {
        public required string Source { get; init; }
        public required string Target { get; init; }
        public required string Mode { get; init; }
    }

    /// <summary>
    /// 컨테이너 제어, 로그, 터미널, 파일 전송 및 리소스 변경 명령을 제공합니다.
    /// </summary>
    public sealed partial class ContainerOperationsViewModel : ViewModelBase
    {
        private readonly NodeViewModel _node;
        private readonly IContainerService _containerService;
        private readonly IDialogService _dialogService;
        private CancellationTokenSource? _logStreamCts;
        private List<Docker.DotNet.Models.MountPoint> _cachedMounts = new();
        private bool _isTransferBusy;
        private string _transferStatus = "경로를 입력하거나 선택하세요.";

        public ObservableCollection<EnvironmentVariableDisplayItem> EnvironmentItems { get; } = new();
        public ObservableCollection<PortBindingDisplayItem> PortItems { get; } = new();
        public ObservableCollection<MountDisplayItem> NamedVolumeItems { get; } = new();
        public ObservableCollection<MountDisplayItem> BindMountItems { get; } = new();

        public bool IsTransferBusy
        {
            get => _isTransferBusy;
            private set => SetProperty(ref _isTransferBusy, value);
        }

        public string TransferStatus
        {
            get => _transferStatus;
            private set => SetProperty(ref _transferStatus, value);
        }

        public ContainerOperationsViewModel(
            NodeViewModel node,
            IContainerService containerService,
            IDialogService dialogService)
        {
            _node = node;
            _containerService = containerService;
            _dialogService = dialogService;

            StartCommand = new AsyncRelayCommand(
                _ => ControlActionAsync("start"),
                _ => IsConnectedContainer && !_node.IsRunning);
            StopCommand = new AsyncRelayCommand(
                _ => ControlActionAsync("stop"),
                _ => IsConnectedContainer && _node.IsRunning);
            PauseCommand = new AsyncRelayCommand(
                _ => ControlActionAsync("pause"),
                _ => IsConnectedContainer && (_node.IsRunning || _node.IsPaused));
            RestartCommand = new AsyncRelayCommand(
                _ => ControlActionAsync("restart"),
                _ => IsConnectedContainer);
            TerminalCommand = new AsyncRelayCommand(
                _ => OpenTerminalAsync(),
                _ => IsConnectedContainer && _node.IsRunning);
            OpenDetailWindowCommand = new AsyncRelayCommand(
                _ => OpenDetailWindowAsync(),
                _ => IsConnectedContainer || IsConnectedSwarmService || IsKubernetesResource);
            ExtractDockerfileCommand = new AsyncRelayCommand(
                _ => ExtractDockerfileAsync(),
                _ => IsConnectedContainer);
            UpdateResourcesCommand = new AsyncRelayCommand(
                ExecuteUpdateResourcesAsync,
                _ => IsConnectedContainer);
            RenameContainerCommand = new AsyncRelayCommand(
                _ => RenameContainerAsync(),
                _ => IsConnectedContainer);
            ExecContainerCommand = new AsyncRelayCommand(
                _ => ExecContainerAsync(),
                _ => IsConnectedContainer && _node.IsRunning);
            CommitContainerCommand = new AsyncRelayCommand(
                _ => CommitContainerAsync(),
                _ => IsConnectedContainer);
            KillContainerCommand = new AsyncRelayCommand(
                _ => KillContainerAsync(),
                _ => IsConnectedContainer && _node.IsRunning);
            ViewRawInspectCommand = new AsyncRelayCommand(
                _ => ViewRawInspectAsync(),
                _ => IsConnectedContainer);

            CopyContainerIdCommand = new RelayCommand(_ => CopyContainerId());
            ExportLogsCommand = new AsyncRelayCommand(
                _ => ExportLogsAsync(),
                _ => IsConnectedContainer || IsConnectedKubernetesPod);
            CopyToContainerCommand = new AsyncRelayCommand(
                _ => CopyToContainerAsync(),
                _ => CanCopyToContainer());
            CopyFromContainerCommand = new AsyncRelayCommand(
                _ => CopyFromContainerAsync(),
                _ => CanCopyFromContainer());
            BrowseHostFileCommand = new RelayCommand(_ => BrowseHostFile());
            BrowseHostFolderCommand = new RelayCommand(_ => BrowseHostFolder());
            AddEnvAndRecreateCommand = new RelayCommand(_ => ShowRecreateNotice());
            OnTransferPathChanged();
        }

        public AsyncRelayCommand StartCommand { get; }
        public AsyncRelayCommand StopCommand { get; }
        public AsyncRelayCommand PauseCommand { get; }
        public AsyncRelayCommand RestartCommand { get; }
        public AsyncRelayCommand TerminalCommand { get; }
        public ICommand OpenDetailWindowCommand { get; }
        public ICommand CopyContainerIdCommand { get; }
        public ICommand ExportLogsCommand { get; }
        public ICommand CopyToContainerCommand { get; }
        public ICommand CopyFromContainerCommand { get; }
        public ICommand BrowseHostFileCommand { get; }
        public ICommand BrowseHostFolderCommand { get; }
        public ICommand AddEnvAndRecreateCommand { get; }
        public AsyncRelayCommand RenameContainerCommand { get; }
        public AsyncRelayCommand ExecContainerCommand { get; }
        public AsyncRelayCommand CommitContainerCommand { get; }
        public AsyncRelayCommand KillContainerCommand { get; }
        public AsyncRelayCommand ViewRawInspectCommand { get; }
        public AsyncRelayCommand ExtractDockerfileCommand { get; }
        public ICommand UpdateResourcesCommand { get; }

        private bool IsConnectedContainer =>
            _node.Type == NodeType.Container &&
            !_node.IsSwarmService &&
            !_node.IsKubernetesResource &&
            _node.IsDockerConnected &&
            !string.IsNullOrWhiteSpace(_node.ContainerId);

        private bool IsConnectedSwarmService =>
            _node.Type == NodeType.Container &&
            _node.IsSwarmService;

        private bool IsKubernetesPod =>
            _node.Type == NodeType.Container &&
            _node.IsKubernetesPod;

        private bool IsKubernetesResource =>
            _node.Type == NodeType.Container &&
            _node.IsKubernetesResource;

        private bool IsConnectedKubernetesPod =>
            IsKubernetesPod &&
            !_node.IsRuntimeUnavailable &&
            _node.IsDockerConnected;

        public void RaiseCommandStates()
        {
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            RestartCommand.RaiseCanExecuteChanged();
            RenameContainerCommand.RaiseCanExecuteChanged();
            ExecContainerCommand.RaiseCanExecuteChanged();
            CommitContainerCommand.RaiseCanExecuteChanged();
            KillContainerCommand.RaiseCanExecuteChanged();
            ViewRawInspectCommand.RaiseCanExecuteChanged();
            TerminalCommand.RaiseCanExecuteChanged();

            if (UpdateResourcesCommand is AsyncRelayCommand updateResources)
                updateResources.RaiseCanExecuteChanged();
            if (OpenDetailWindowCommand is AsyncRelayCommand openDetail)
                openDetail.RaiseCanExecuteChanged();
            if (ExportLogsCommand is AsyncRelayCommand exportLogs)
                exportLogs.RaiseCanExecuteChanged();

            ExtractDockerfileCommand.RaiseCanExecuteChanged();
            RaiseTransferCommandStates();
        }

        public async Task<bool> ReconnectAsync()
        {
            var containers = await _containerService.GetContainersAsync();
            var match = containers.FirstOrDefault(container =>
                            !string.IsNullOrWhiteSpace(_node.ContainerId) &&
                            container.Id == _node.ContainerId)
                        ?? containers.FirstOrDefault(container =>
                            string.Equals(container.Name, _node.Name, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                _dialogService.ShowInfo(
                    $"Docker에서 '{_node.Name}' 컨테이너를 찾지 못했습니다.",
                    "Reconnect");
                _node.IsDockerConnected = false;
                return false;
            }

            _node.ContainerId = match.Id;
            _node.Name = match.Name;
            _node.ImageName = match.Image;
            _node.PortInfo = match.Ports;
            _node.StatusColor = match.StateColor;
            _node.IsDockerConnected = true;
            await _node.RefreshDetailsAsync();
            return true;
        }
        private async Task RenameContainerAsync()
        {
            const string title = "Rename Container";
            if (!await ValidateContainerActionAsync(title, requireRunning: false)) return;

            if (!_dialogService.TryShowContainerRenameDialog(_node, _node.Name, out var newName) ||
                string.Equals(_node.Name, newName, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                await _containerService.RenameContainerAsync(_node.ContainerId, newName);
                _node.Name = newName;
                _node.NotifyModified();
                await _node.RefreshDetailsAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"컨테이너 이름 변경 실패: {ex.Message}", title);
            }
        }

        private async Task ExecContainerAsync()
        {
            const string title = "Exec Command";
            if (!await ValidateContainerActionAsync(title, requireRunning: true)) return;

            string containerId = _node.ContainerId;
            _dialogService.ShowContainerExecDialog(
                _node,
                _node.Name,
                containerId,
                command => _containerService.ExecuteCommandWithOutputAsync(containerId, command));
        }

        private async Task CommitContainerAsync()
        {
            const string title = "Commit Container";
            if (!await ValidateContainerActionAsync(title, requireRunning: false)) return;

            if (!_dialogService.TryShowContainerCommitDialog(
                    _node,
                    _node.Name,
                    out var repository,
                    out var imageTag,
                    out var message,
                    out var author,
                    out var pause))
            {
                return;
            }

            try
            {
                _dialogService.SetBusyCursor(true);
                var imageId = await _containerService.CommitContainerAsync(
                    _node.ContainerId,
                    repository,
                    imageTag,
                    message,
                    author,
                    pause);

                _dialogService.ShowInfo(
                    $"이미지를 생성했습니다.\n{repository}:{imageTag}\n{imageId}",
                    title);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"컨테이너 커밋 실패: {ex.Message}", title);
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
            }
        }

        private async Task KillContainerAsync()
        {
            const string title = "Kill Container";
            if (!await ValidateContainerActionAsync(title, requireRunning: true)) return;

            if (!_dialogService.ShowConfirm(
                    $"'{_node.Name}' 컨테이너에 SIGKILL을 보내 강제 종료할까요?",
                    title))
            {
                return;
            }

            try
            {
                await _containerService.KillContainerAsync(_node.ContainerId);
                await _node.RefreshDetailsAsync();
                _dialogService.ShowInfo("컨테이너를 강제 종료했습니다.", title);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"컨테이너 강제 종료 실패: {ex.Message}", title);
            }
        }

        private async Task ViewRawInspectAsync()
        {
            const string title = "Raw Inspect";
            if (!await ValidateContainerActionAsync(title, requireRunning: false)) return;

            try
            {
                var payload = await _containerService.InspectContainerAsync(_node.ContainerId);
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    ReferenceHandler = ReferenceHandler.IgnoreCycles
                });

                _dialogService.ShowRawInspectDialog(
                    _node,
                    $"Container inspect - {_node.Name}",
                    json);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Inspect 실패: {ex.Message}", title);
            }
        }

        private async Task<bool> ValidateContainerActionAsync(string title, bool requireRunning)
        {
            if (!IsConnectedContainer)
            {
                _dialogService.ShowInfo(
                    "Docker에서 끊긴 컨테이너입니다. 먼저 Reconnect를 실행해 주세요.",
                    title);
                return false;
            }

            await _node.RefreshDetailsAsync();

            if (!IsConnectedContainer)
            {
                _dialogService.ShowInfo(
                    "Docker에서 컨테이너를 찾지 못했습니다. 먼저 Reconnect를 실행해 주세요.",
                    title);
                return false;
            }

            if (requireRunning && !_node.IsRunning)
            {
                _dialogService.ShowInfo("실행 중인 컨테이너에서만 사용할 수 있습니다.", title);
                return false;
            }

            return true;
        }

        private async Task ControlActionAsync(string action)
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId)) return;

            try
            {
                switch (action)
                {
                    case "start":
                        await _containerService.StartContainerAsync(_node.ContainerId);
                        break;
                    case "stop":
                        await _containerService.StopContainerAsync(_node.ContainerId);
                        break;
                    case "pause":
                        if (_node.DetailStatus == "paused")
                            await _containerService.UnpauseContainerAsync(_node.ContainerId);
                        else
                            await _containerService.PauseContainerAsync(_node.ContainerId);
                        break;
                    case "restart":
                        await _containerService.RestartContainerAsync(_node.ContainerId);
                        break;
                }

                await _node.RefreshDetailsAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"동작 실패 : {ex.Message}");
            }
        }

        private async Task OpenTerminalAsync()
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId)) return;

            try
            {
                bool opened = await _containerService.OpenTerminalAsync(_node.ContainerId);
                if (!opened)
                    _dialogService.ShowMessage("이 컨테이너 이미지에는 실행 가능한 셸이 없습니다.\nDistroless 또는 scratch 이미지에서는 터미널을 열 수 없습니다.");
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"터미널 오류: {ex.Message}");
            }
        }

        private void ShowRecreateNotice()
        {
            if (string.IsNullOrWhiteSpace(_node.NewEnvInput)) return;

            _dialogService.ShowInfo(
                $"이 기능은 기존 설정을 바탕으로 컨테이너를 삭제하고 [{_node.NewEnvInput}] 환경변수를 추가하여 다시 생성합니다.\n(기능 연결 필요)",
                "Recreate");
        }

        private async Task ExtractDockerfileAsync()
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId)) return;

            try
            {
                _dialogService.SetBusyCursor(true);
                var info = await _containerService.InspectContainerAsync(_node.ContainerId);
                Docker.DotNet.Models.ImageInspectResponse? imageInfo = null;
                if (_containerService is IImageService imageService)
                {
                    try
                    {
                        string imageReference = !string.IsNullOrWhiteSpace(info.Image)
                            ? info.Image
                            : info.Config.Image;
                        imageInfo = await imageService.InspectImageAsync(imageReference);
                    }
                    catch
                    {
                        // The container can still be exported when its base image metadata
                        // is unavailable. In that case the generator keeps all visible values.
                    }
                }

                var content = DockerfileGenerator.Build(info, imageInfo?.Config);

                var dockerfilePath = _dialogService.ShowSaveFileDialog(
                    "Dockerfile|*.*|Text Files (*.txt)|*.txt",
                    "",
                    "Dockerfile",
                    "추출한 Dockerfile 저장");
                if (!string.IsNullOrWhiteSpace(dockerfilePath))
                {
                    File.WriteAllText(dockerfilePath, content);
                    _dialogService.ShowInfo($"[{dockerfilePath}] 경로에 성공적으로 저장되었습니다.", "저장 완료");
                }
                else if (_dialogService.ShowConfirm(
                    "파일 저장을 취소하셨습니다.\n대신 내용을 클립보드(Ctrl+C)에 복사하시겠습니까?",
                    "클립보드 복사"))
                {
                    _dialogService.SetClipboardText(content);
                    _dialogService.ShowInfo("클립보드에 복사되었습니다. (Ctrl+V 로 붙여넣기 하세요)", "복사 완료");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"추출 실패: {ex.Message}");
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
            }
        }
    }
}
