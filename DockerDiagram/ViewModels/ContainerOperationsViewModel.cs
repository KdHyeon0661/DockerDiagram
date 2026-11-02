using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public sealed class ConfigurationDisplayItem
    {
        public required string Name { get; init; }
        public required string Value { get; init; }
    }

    public sealed class EnvironmentVariableDisplayItem : ViewModelBase
    {
        private string _key = string.Empty;
        private string _value = string.Empty;
        private bool _isValueVisible;
        public Action? Changed { get; set; }
        public string Key
        {
            get => _key;
            set
            {
                if (!SetProperty(ref _key, value)) return;
                OnPropertyChanged(nameof(IsSecret));
                OnPropertyChanged(nameof(IsValueVisible));
                OnPropertyChanged(nameof(DisplayValue));
                OnPropertyChanged(nameof(RevealButtonText));
                Changed?.Invoke();
            }
        }
        public string Value
        {
            get => _value;
            set
            {
                if (!SetProperty(ref _value, value)) return;
                OnPropertyChanged(nameof(DisplayValue));
                Changed?.Invoke();
            }
        }
        public bool IsSecret => Key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                Key.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
                                Key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                                Key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                                Key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                                Key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase);
        public bool IsValueVisible
        {
            get => !IsSecret || _isValueVisible;
            set
            {
                if (SetProperty(ref _isValueVisible, value))
                {
                    OnPropertyChanged(nameof(DisplayValue));
                    OnPropertyChanged(nameof(RevealButtonText));
                }
            }
        }
        public string DisplayValue => IsValueVisible ? Value : new string('•', Math.Clamp(Value.Length, 6, 20));
        public string RevealButtonText => IsValueVisible ? "숨기기" : "보기";
        public string CopyText => $"{Key}={Value}";
    }

    public sealed class PortBindingDisplayItem : ViewModelBase
    {
        private string _hostIp = "0.0.0.0";
        private string _hostPort = string.Empty;
        private string _containerPort = string.Empty;
        private string _protocol = "tcp";
        public Action? Changed { get; set; }
        public string HostIp { get => _hostIp; set { if (SetProperty(ref _hostIp, value)) Changed?.Invoke(); } }
        public string HostPort { get => _hostPort; set { if (SetProperty(ref _hostPort, value)) Changed?.Invoke(); } }
        public string ContainerPort { get => _containerPort; set { if (SetProperty(ref _containerPort, value)) Changed?.Invoke(); } }
        public string Protocol { get => _protocol; set { if (SetProperty(ref _protocol, value)) Changed?.Invoke(); } }
        public string CopyText => $"{HostIp}:{HostPort} -> {ContainerPort}/{Protocol}";
    }

    public sealed class MountDisplayItem : ViewModelBase
    {
        private MountKind _kind = MountKind.Volume;
        private string _source = string.Empty;
        private string _target = string.Empty;
        private string _mode = "rw";
        private string _bindPropagation = "rprivate";
        private bool _volumeNoCopy;
        private long _tmpfsSizeBytes;
        private string _tmpfsMode = "1777";
        public Action? Changed { get; set; }
        public MountKind Kind { get => _kind; set { if (SetProperty(ref _kind, value)) Changed?.Invoke(); } }
        public string Source { get => _source; set { if (SetProperty(ref _source, value)) Changed?.Invoke(); } }
        public string Target { get => _target; set { if (SetProperty(ref _target, value)) Changed?.Invoke(); } }
        public string Mode { get => _mode; set { if (SetProperty(ref _mode, value)) Changed?.Invoke(); } }
        public string BindPropagation { get => _bindPropagation; set { if (SetProperty(ref _bindPropagation, value)) Changed?.Invoke(); } }
        public bool VolumeNoCopy { get => _volumeNoCopy; set { if (SetProperty(ref _volumeNoCopy, value)) Changed?.Invoke(); } }
        public long TmpfsSizeBytes { get => _tmpfsSizeBytes; set { if (SetProperty(ref _tmpfsSizeBytes, value)) Changed?.Invoke(); } }
        public string TmpfsMode { get => _tmpfsMode; set { if (SetProperty(ref _tmpfsMode, value)) Changed?.Invoke(); } }
    }

    /// <summary>
    /// 컨테이너 제어, 로그, 터미널, 파일 전송 및 리소스 변경 명령을 제공합니다.
    /// </summary>
    public sealed partial class ContainerOperationsViewModel : ViewModelBase, IDisposable
    {
        private static readonly JsonSerializerOptions InspectJsonOptions = new()
        {
            WriteIndented = true,
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };

        private readonly NodeViewModel _node;
        private readonly IContainerService _containerService;
        private readonly IDialogService _dialogService;
        private CancellationTokenSource? _logStreamCts;
        private List<Docker.DotNet.Models.MountPoint> _cachedMounts = [];
        private bool _isTransferBusy;
        private string _transferStatus = "경로를 입력하거나 선택하세요.";
        private bool _hasPendingConfigurationChanges;
        private bool _hasPendingStorageChanges;
        private string _recreateStatus = "변경사항이 없습니다.";
        private string _uploadHostPath = string.Empty;
        private string _uploadContainerPath = string.Empty;
        private string _downloadContainerPath = string.Empty;
        private string _downloadHostFolder = string.Empty;
        private CancellationTokenSource? _transferCts;

        public void Dispose()
        {
            StopLogStream();
            _transferCts?.Cancel();
            _transferCts?.Dispose();
            _transferCts = null;
            GC.SuppressFinalize(this);
        }

        public ObservableCollection<EnvironmentVariableDisplayItem> EnvironmentItems { get; } = [];
        public ObservableCollection<PortBindingDisplayItem> PortItems { get; } = [];
        public ObservableCollection<MountDisplayItem> NamedVolumeItems { get; } = [];
        public ObservableCollection<MountDisplayItem> BindMountItems { get; } = [];
        public ObservableCollection<MountDisplayItem> TmpfsMountItems { get; } = [];
        public ObservableCollection<ConfigurationDisplayItem> GeneralConfigurationItems { get; } = [];
        public ObservableCollection<EnvironmentVariableDisplayItem> LabelItems { get; } = [];

        public bool HasPendingConfigurationChanges
        {
            get => _hasPendingConfigurationChanges;
            private set
            {
                if (SetProperty(ref _hasPendingConfigurationChanges, value))
                {
                    OnPropertyChanged(nameof(HasPendingRecreateChanges));
                    RaiseRecreateCommandStates();
                }
            }
        }

        public bool HasPendingStorageChanges
        {
            get => _hasPendingStorageChanges;
            private set
            {
                if (SetProperty(ref _hasPendingStorageChanges, value))
                {
                    OnPropertyChanged(nameof(HasPendingRecreateChanges));
                    RaiseRecreateCommandStates();
                }
            }
        }

        public bool HasPendingRecreateChanges =>
            HasPendingConfigurationChanges || HasPendingStorageChanges;

        public string RecreateStatus
        {
            get => _recreateStatus;
            private set => SetProperty(ref _recreateStatus, value);
        }

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

        public string UploadHostPath { get => _uploadHostPath; set { if (SetProperty(ref _uploadHostPath, value)) RaiseDetailTransferCommandStates(); } }
        public string UploadContainerPath { get => _uploadContainerPath; set { if (SetProperty(ref _uploadContainerPath, value)) RaiseDetailTransferCommandStates(); } }
        public string DownloadContainerPath { get => _downloadContainerPath; set { if (SetProperty(ref _downloadContainerPath, value)) RaiseDetailTransferCommandStates(); } }
        public string DownloadHostFolder { get => _downloadHostFolder; set { if (SetProperty(ref _downloadHostFolder, value)) RaiseDetailTransferCommandStates(); } }

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
            ResetResourcesCommand = new AsyncRelayCommand(
                ResetResourceLimitsAsync,
                _ => IsConnectedContainer && _node.Monitoring.HasPendingLimitChanges,
                _node.Monitoring,
                nameof(ContainerMonitoringViewModel.HasPendingLimitChanges));
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
            BrowseUploadFileCommand = new RelayCommand(_ => BrowseUploadFile());
            BrowseUploadFolderCommand = new RelayCommand(_ => BrowseUploadFolder());
            BrowseDownloadFolderCommand = new RelayCommand(_ => BrowseDownloadFolder());
            DetailUploadCommand = new AsyncRelayCommand(_ => DetailUploadAsync(), _ => CanDetailUpload());
            DetailDownloadCommand = new AsyncRelayCommand(_ => DetailDownloadAsync(), _ => CanDetailDownload());
            CancelTransferCommand = new RelayCommand(_ => CancelTransfer(), _ => IsTransferBusy);
            AddEnvAndRecreateCommand = new RelayCommand(_ => ShowRecreateNotice());
            AddEnvironmentCommand = new RelayCommand(_ => AddEnvironmentDraft());
            RemoveEnvironmentCommand = new RelayCommand(item => RemoveEnvironmentDraft(item as EnvironmentVariableDisplayItem));
            ToggleEnvironmentSecretCommand = new RelayCommand(item => ToggleEnvironmentSecret(item as EnvironmentVariableDisplayItem));
            AddPortCommand = new RelayCommand(_ => AddPortDraft());
            RemovePortCommand = new RelayCommand(item => RemovePortDraft(item as PortBindingDisplayItem));
            AddNamedVolumeCommand = new RelayCommand(_ => AddNamedVolumeDraft());
            AddBindMountCommand = new RelayCommand(_ => AddBindMountDraft());
            AddTmpfsMountCommand = new RelayCommand(_ => AddTmpfsMountDraft());
            RemoveNamedVolumeCommand = new RelayCommand(item => RemoveNamedVolumeDraft(item as MountDisplayItem));
            RemoveBindMountCommand = new RelayCommand(item => RemoveBindMountDraft(item as MountDisplayItem));
            RemoveTmpfsMountCommand = new RelayCommand(item => RemoveTmpfsMountDraft(item as MountDisplayItem));
            BrowseBindMountCommand = new RelayCommand(item => BrowseBindMount(item as MountDisplayItem));
            ApplyRecreateChangesCommand = new AsyncRelayCommand(
                ApplyRecreateChangesAsync,
                _ => IsConnectedContainer && HasPendingRecreateChanges);
            ResetRecreateChangesCommand = new AsyncRelayCommand(
                ResetRecreateChangesAsync,
                _ => IsConnectedContainer && HasPendingRecreateChanges);
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
        public ICommand BrowseUploadFileCommand { get; }
        public ICommand BrowseUploadFolderCommand { get; }
        public ICommand BrowseDownloadFolderCommand { get; }
        public AsyncRelayCommand DetailUploadCommand { get; }
        public AsyncRelayCommand DetailDownloadCommand { get; }
        public RelayCommand CancelTransferCommand { get; }
        public ICommand AddEnvAndRecreateCommand { get; }
        public ICommand AddEnvironmentCommand { get; }
        public ICommand RemoveEnvironmentCommand { get; }
        public ICommand ToggleEnvironmentSecretCommand { get; }
        public ICommand AddPortCommand { get; }
        public ICommand RemovePortCommand { get; }
        public ICommand AddNamedVolumeCommand { get; }
        public ICommand AddBindMountCommand { get; }
        public ICommand AddTmpfsMountCommand { get; }
        public ICommand RemoveNamedVolumeCommand { get; }
        public ICommand RemoveBindMountCommand { get; }
        public ICommand RemoveTmpfsMountCommand { get; }
        public ICommand BrowseBindMountCommand { get; }
        public AsyncRelayCommand ApplyRecreateChangesCommand { get; }
        public AsyncRelayCommand ResetRecreateChangesCommand { get; }
        public AsyncRelayCommand RenameContainerCommand { get; }
        public AsyncRelayCommand ExecContainerCommand { get; }
        public AsyncRelayCommand CommitContainerCommand { get; }
        public AsyncRelayCommand KillContainerCommand { get; }
        public AsyncRelayCommand ViewRawInspectCommand { get; }
        public AsyncRelayCommand ExtractDockerfileCommand { get; }
        public ICommand UpdateResourcesCommand { get; }
        public AsyncRelayCommand ResetResourcesCommand { get; }

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
            ResetResourcesCommand.RaiseCanExecuteChanged();
            if (OpenDetailWindowCommand is AsyncRelayCommand openDetail)
                openDetail.RaiseCanExecuteChanged();
            if (ExportLogsCommand is AsyncRelayCommand exportLogs)
                exportLogs.RaiseCanExecuteChanged();

            ExtractDockerfileCommand.RaiseCanExecuteChanged();
            RaiseTransferCommandStates();
            RaiseRecreateCommandStates();
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
                _node.ApplyObservedContainerState(connected: false, dockerState: null);
                return false;
            }

            _node.ContainerId = match.Id;
            _node.Name = match.Name;
            _node.ImageName = match.Image;
            _node.PortInfo = match.Ports;
            _node.ApplyObservedContainerState(connected: true, dockerState: match.State);
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
                var json = JsonSerializer.Serialize(payload, InspectJsonOptions);

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
                    catch (Exception ex)
                    {
                        // The container can still be exported when its base image metadata
                        // is unavailable. In that case the generator keeps all visible values.
                        AppLog.Warning("Dockerfile 추출 중 베이스 이미지 메타데이터 조회 실패", ex);
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
