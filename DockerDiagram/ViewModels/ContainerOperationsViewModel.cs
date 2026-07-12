using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 컨테이너 제어, 로그, 터미널, 파일 전송 및 리소스 변경 명령을 제공합니다.
    /// </summary>
    public sealed class ContainerOperationsViewModel
    {
        private readonly NodeViewModel _node;
        private readonly IContainerService _containerService;
        private readonly IDialogService _dialogService;
        private CancellationTokenSource? _logStreamCts;
        private List<Docker.DotNet.Models.MountPoint> _cachedMounts = new();

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
            TerminalCommand = new RelayCommand(
                _ => OpenTerminal(),
                _ => IsConnectedContainer && _node.IsRunning);
            OpenDetailWindowCommand = new AsyncRelayCommand(
                _ => OpenDetailWindowAsync(),
                _ => IsConnectedContainer || IsConnectedSwarmService || IsKubernetesResource);
            RefreshLogsCommand = new AsyncRelayCommand(
                _ => LoadLogsAsync(),
                _ => IsConnectedContainer || IsConnectedKubernetesPod);
            ExtractDockerfileCommand = new AsyncRelayCommand(
                _ => ExtractDockerfileAsync(),
                _ => IsConnectedContainer);
            UpdateResourcesCommand = new AsyncRelayCommand(
                ExecuteUpdateResourcesAsync,
                _ => IsConnectedContainer);

            CopyLogsCommand = new RelayCommand(_ => CopyLogs());
            ExportLogsCommand = new RelayCommand(_ => ExportLogs());
            CopyToContainerCommand = new AsyncRelayCommand(_ => CopyToContainerAsync());
            CopyFromContainerCommand = new AsyncRelayCommand(_ => CopyFromContainerAsync());
            AddEnvAndRecreateCommand = new RelayCommand(_ => ShowRecreateNotice());
        }

        public AsyncRelayCommand StartCommand { get; }
        public AsyncRelayCommand StopCommand { get; }
        public AsyncRelayCommand PauseCommand { get; }
        public AsyncRelayCommand RestartCommand { get; }
        public RelayCommand TerminalCommand { get; }
        public ICommand OpenDetailWindowCommand { get; }
        public ICommand RefreshLogsCommand { get; }
        public ICommand CopyLogsCommand { get; }
        public ICommand ExportLogsCommand { get; }
        public ICommand CopyToContainerCommand { get; }
        public ICommand CopyFromContainerCommand { get; }
        public ICommand AddEnvAndRecreateCommand { get; }
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
            TerminalCommand.RaiseCanExecuteChanged();

            if (UpdateResourcesCommand is AsyncRelayCommand updateResources)
                updateResources.RaiseCanExecuteChanged();
            if (OpenDetailWindowCommand is AsyncRelayCommand openDetail)
                openDetail.RaiseCanExecuteChanged();
            if (RefreshLogsCommand is AsyncRelayCommand refreshLogs)
                refreshLogs.RaiseCanExecuteChanged();

            ExtractDockerfileCommand.RaiseCanExecuteChanged();
        }

        public async Task StartLogStreamAsync(Action<string> onLogReceived)
        {
            if (_node.IsSwarmService || _node.IsKubernetesResource) return;
            if (string.IsNullOrWhiteSpace(_node.ContainerId)) return;

            StopLogStream();
            _logStreamCts = new CancellationTokenSource();

            try
            {
                await _containerService.StreamContainerLogsAsync(
                    _node.ContainerId,
                    onLogReceived,
                    _logStreamCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Log stream error: {ex.Message}");
            }
        }

        public void StopLogStream()
        {
            _logStreamCts?.Cancel();
            _logStreamCts?.Dispose();
            _logStreamCts = null;
        }

        public async Task RefreshDetailsAsync()
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId))
            {
                _node.IsDockerConnected = false;
                return;
            }

            var info = await _containerService.InspectContainerAsync(_node.ContainerId);
            _node.IsDockerConnected = true;

            await RefreshResourceLimitsAsync(info);

            _node.DetailStatus = info.State.Status;
            _node.IsRunning = info.State.Running;
            _node.IsPaused = info.State.Paused;
            _node.StartedAt = DateTime.TryParse(info.State.StartedAt, out var started)
                ? started.ToString("yyyy-MM-dd HH:mm:ss")
                : info.State.StartedAt;
            _node.FinishedAt = DateTime.TryParse(info.State.FinishedAt, out var finished)
                ? finished.ToString("yyyy-MM-dd HH:mm:ss")
                : info.State.FinishedAt;
            _node.CreatedDate = info.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

            if (_node.IsRunning && DateTime.TryParse(info.State.StartedAt, out var startTime))
            {
                var duration = DateTime.UtcNow - startTime.ToUniversalTime();
                _node.Uptime = $"Up {duration.Days}d {duration.Hours}h {duration.Minutes}m";
            }
            else
            {
                _node.Uptime = $"Created {info.Created.ToLocalTime():yy-MM-dd}";
            }

            UpdateHealth(info);
            UpdateRestartPolicy(info);
            _node.EnvironmentVariables = info.Config?.Env?.ToList() ?? new List<string>();
            _node.PortBindings = ReadPortBindings(info);

            if (info.NetworkSettings?.Networks != null)
                _node.Network.UpdateDetails(info.NetworkSettings.Networks);
            else
                _node.Network.UpdateDetails(
                    Array.Empty<KeyValuePair<string, Docker.DotNet.Models.EndpointSettings>>());

            UpdateMounts(info.Mounts);
            _node.StatusColor = _node.IsRunning
                ? "#28a745"
                : _node.IsPaused
                    ? "#ffc107"
                    : "#dc3545";

            if (_node.IsRunning)
            {
                var stats = await _containerService.GetContainerStatsAsync(_node.ContainerId);
                _node.Monitoring.ApplyStats(stats);
            }
            else
            {
                _node.Monitoring.ApplyStoppedState();
            }
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

        public void RefreshMountedVolumes()
        {
            _node.MountedVolumeList.Clear();
            if (_node.ParentSheet == null) return;

            var validVolumeNames = _node.ParentSheet.Nodes
                .Where(node => node.Type == NodeType.Volume)
                .SelectMany(node => new[] { node.Name, node.EffectiveVolumeName })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var mount in _cachedMounts)
            {
                if (_node.VolumeDisplayMode == 0)
                {
                    if (mount.Type == "volume" && validVolumeNames.Contains(mount.Name))
                        _node.MountedVolumeList.Add($"{mount.Name} : {mount.Destination}");
                }
                else if (mount.Type == "bind")
                {
                    _node.MountedVolumeList.Add($"{mount.Source} -> {mount.Destination}");
                }
            }
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

        private async Task RefreshResourceLimitsAsync(
            Docker.DotNet.Models.ContainerInspectResponse info)
        {
            try
            {
                var systemInfo = await _containerService.GetSystemInfoAsync();
                _node.MaxCpuCount = systemInfo.NCPU > 0
                    ? systemInfo.NCPU
                    : Environment.ProcessorCount;
                _node.MaxMemoryMb = systemInfo.MemTotal > 0
                    ? systemInfo.MemTotal / 1048576
                    : 32768;
            }
            catch
            {
                _node.MaxCpuCount = Environment.ProcessorCount;
                _node.MaxMemoryMb = 32768;
            }

            if (info.HostConfig == null) return;

            _node.TargetMemoryMb = info.HostConfig.Memory > 0
                ? info.HostConfig.Memory / 1048576
                : _node.MaxMemoryMb;
            _node.TargetCpuCount = info.HostConfig.NanoCPUs > 0
                ? info.HostConfig.NanoCPUs / 1_000_000_000.0
                : _node.MaxCpuCount;
        }

        private void UpdateHealth(Docker.DotNet.Models.ContainerInspectResponse info)
        {
            string? health = info.State.Health?.Status;
            if (string.IsNullOrEmpty(health))
            {
                _node.HealthStatus = "No Check";
                _node.HealthColor = "#888888";
                return;
            }

            switch (health.ToLowerInvariant())
            {
                case "healthy":
                    _node.HealthStatus = "Healthy 💚";
                    _node.HealthColor = "#28a745";
                    break;
                case "starting":
                    _node.HealthStatus = "Starting 💛";
                    _node.HealthColor = "#ffc107";
                    break;
                case "unhealthy":
                    _node.HealthStatus = "Unhealthy 💔";
                    _node.HealthColor = "#dc3545";
                    break;
                default:
                    _node.HealthStatus = health;
                    _node.HealthColor = "#555555";
                    break;
            }
        }

        private void UpdateRestartPolicy(Docker.DotNet.Models.ContainerInspectResponse info)
        {
            if (info.HostConfig?.RestartPolicy == null) return;

            string policy = info.HostConfig.RestartPolicy.Name.ToString().ToLowerInvariant();
            _node.RestartPolicy = policy switch
            {
                "unlessstopped" => "unless-stopped",
                "onfailure" => "on-failure",
                _ => policy
            };
        }

        private static List<string> ReadPortBindings(
            Docker.DotNet.Models.ContainerInspectResponse info)
        {
            var ports = new List<string>();
            if (info.HostConfig?.PortBindings == null) return ports;

            foreach (var binding in info.HostConfig.PortBindings)
            {
                string containerPort = binding.Key.Replace("/tcp", "").Replace("/udp", "");
                foreach (var hostBinding in binding.Value)
                {
                    if (!string.IsNullOrEmpty(hostBinding.HostPort))
                        ports.Add($"{hostBinding.HostPort}:{containerPort}");
                }
            }

            return ports;
        }

        private void UpdateMounts(IList<Docker.DotNet.Models.MountPoint>? mounts)
        {
            _cachedMounts = mounts?.ToList() ?? new List<Docker.DotNet.Models.MountPoint>();
            _node.MountedVolumes = _cachedMounts.Count > 0
                ? string.Join(
                    "\n",
                    _cachedMounts.Select(mount => $"{mount.Source} -> {mount.Destination}"))
                : "None";
            RefreshMountedVolumes();
        }

        private void OpenTerminal()
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId)) return;

            try
            {
                _containerService.OpenTerminal(_node.ContainerId);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"터미널 오류 : {ex.Message}");
            }
        }

        private async Task OpenDetailWindowAsync()
        {
            if (!IsConnectedContainer && !IsConnectedSwarmService && !IsKubernetesResource) return;

            if (_node.IsSwarmService && !_node.IsRuntimeUnavailable && _node.IsDockerConnected)
                await _node.RefreshSwarmServiceAsync();

            if (_node.IsKubernetesResource && !_node.IsRuntimeUnavailable && _node.IsDockerConnected)
                await _node.RefreshKubernetesResourceAsync();

            _dialogService.ShowContainerDetail(_node);
            await LoadLogsAsync();
        }

        private async Task LoadLogsAsync()
        {
            if (_node.IsSwarmService)
            {
                _node.ContainerLogs = "Swarm service는 단일 컨테이너 로그 대상이 아닙니다. 실제 로그는 service task/container 단위에서 확인해야 합니다.";
                return;
            }

            if (_node.IsKubernetesPod)
            {
                if (_node.IsRuntimeUnavailable)
                {
                    _node.ContainerLogs = "현재 시트는 오프라인 스냅샷입니다. 런타임을 사용할 수 있을 때 로그를 다시 조회해 주세요.";
                    return;
                }

                if (_containerService is not IKubernetesService kubernetesService)
                {
                    _node.ContainerLogs = "Kubernetes service가 연결되어 있지 않습니다.";
                    return;
                }

                try
                {
                    _node.ContainerLogs = "Fetching logs from Kubernetes...";
                    string logs = await kubernetesService.GetKubernetesPodLogsAsync(_node.KubernetesNamespace, _node.KubernetesPodName, 500);
                    _node.ContainerLogs = string.IsNullOrWhiteSpace(logs) ? "(No logs found)" : logs;
                }
                catch (Exception ex)
                {
                    _node.ContainerLogs = $"Error fetching Kubernetes logs: {ex.Message}";
                }

                return;
            }

            if (_node.IsKubernetesResource)
            {
                _node.ContainerLogs = $"{_node.KubernetesKind} 리소스는 Pod 로그 대상이 아닙니다. Describe/YAML/JSON 탭에서 상태를 확인하세요.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_node.ContainerId))
            {
                _node.ContainerLogs = "Container ID is missing.";
                return;
            }

            try
            {
                _node.ContainerLogs = "Fetching logs from Docker engine...";
                string logs = await _containerService.GetContainerLogsAsync(_node.ContainerId, tailCount: 500);
                _node.ContainerLogs = string.IsNullOrEmpty(logs) ? "(No logs found)" : logs;
            }
            catch (Exception ex)
            {
                _node.ContainerLogs = $"Error fetching logs: {ex.Message}";
            }
        }

        private void CopyLogs()
        {
            if (string.IsNullOrEmpty(_node.ContainerLogs)) return;

            Clipboard.SetText(_node.ContainerLogs);
            _dialogService.ShowInfo("로그가 클립보드에 복사되었습니다.", "복사 완료");
        }

        private void ExportLogs()
        {
            if (string.IsNullOrEmpty(_node.ContainerLogs)) return;

            var fileName = _dialogService.ShowSaveFileDialog(
                "Text File|*.txt",
                ".txt",
                $"{_node.Name}_logs.txt",
                "Export Logs");
            if (string.IsNullOrWhiteSpace(fileName)) return;

            File.WriteAllText(fileName, _node.ContainerLogs);
            _dialogService.ShowInfo("로그가 파일로 저장되었습니다.", "저장 완료");
        }

        private async Task CopyToContainerAsync()
        {
            if (string.IsNullOrWhiteSpace(_node.HostFilePath) ||
                string.IsNullOrWhiteSpace(_node.ContainerFilePath))
            {
                return;
            }

            try
            {
                await _containerService.CopyToContainerAsync(
                    _node.ContainerId,
                    _node.HostFilePath,
                    _node.ContainerFilePath);
                _dialogService.ShowInfo("컨테이너로 파일 복사가 완료되었습니다.", "업로드 성공");
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"업로드 실패: {ex.Message}");
            }
        }

        private async Task CopyFromContainerAsync()
        {
            if (string.IsNullOrWhiteSpace(_node.HostFilePath) ||
                string.IsNullOrWhiteSpace(_node.ContainerFilePath))
            {
                return;
            }

            try
            {
                await _containerService.CopyFromContainerAsync(
                    _node.ContainerId,
                    _node.ContainerFilePath,
                    _node.HostFilePath);
                _dialogService.ShowInfo("컨테이너에서 파일 다운로드가 완료되었습니다.", "다운로드 성공");
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"다운로드 실패: {ex.Message}");
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
                Mouse.OverrideCursor = Cursors.Wait;
                var info = await _containerService.InspectContainerAsync(_node.ContainerId);
                var content = BuildDockerfile(info);

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
                    Clipboard.SetText(content);
                    _dialogService.ShowInfo("클립보드에 복사되었습니다. (Ctrl+V 로 붙여넣기 하세요)", "복사 완료");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"추출 실패: {ex.Message}");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private async Task ExecuteUpdateResourcesAsync(object? parameter)
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId)) return;

            bool confirm = _dialogService.ShowConfirm(
                $"컨테이너 리소스를 실시간으로 제한하시겠습니까? (재시작 없음)\n\n" +
                $"- 목표 CPU: {_node.TargetCpuCount:0.1} Core\n" +
                $"- 목표 Memory: {_node.TargetMemoryMb} MB",
                "실시간 리소스 변경");
            if (!confirm) return;

            try
            {
                await _containerService.UpdateContainerResourcesAsync(
                    _node.ContainerId,
                    _node.TargetCpuCount,
                    _node.TargetMemoryMb);
                _dialogService.ShowInfo("리소스 제한이 무중단으로 성공적으로 적용되었습니다.", "업데이트 완료");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"리소스 업데이트 실패: {ex.Message}\n(참고: CPU 제한이 호스트 코어 수를 넘을 수 없습니다.)",
                    "오류");
            }
        }

        private static string BuildDockerfile(Docker.DotNet.Models.ContainerInspectResponse info)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"FROM {info.Config.Image}");
            builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(info.Config.WorkingDir))
                builder.AppendLine($"WORKDIR {info.Config.WorkingDir}");

            if (info.Config.Env != null)
            {
                foreach (var env in info.Config.Env)
                    builder.AppendLine($"ENV {env}");
            }

            if (info.Config.ExposedPorts != null)
            {
                foreach (var port in info.Config.ExposedPorts.Keys)
                    builder.AppendLine($"EXPOSE {port.Split('/')[0]}");
            }

            if (info.Config.Volumes != null)
            {
                foreach (var volume in info.Config.Volumes.Keys)
                    builder.AppendLine($"VOLUME {volume}");
            }

            if (info.Config.Entrypoint != null && info.Config.Entrypoint.Count > 0)
                builder.AppendLine($"ENTRYPOINT [\"{string.Join("\", \"", info.Config.Entrypoint)}\"]");

            if (info.Config.Cmd != null && info.Config.Cmd.Count > 0)
                builder.AppendLine($"CMD [\"{string.Join("\", \"", info.Config.Cmd)}\"]");

            return builder.ToString();
        }
    }
}
