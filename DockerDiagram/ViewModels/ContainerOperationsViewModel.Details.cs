using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDiagram.ViewModels
{
    public sealed partial class ContainerOperationsViewModel
    {
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
            _node.FinishedAt = _node.IsRunning
                ? "-"
                : DateTime.TryParse(info.State.FinishedAt, out var finished)
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
            UpdateEnvironmentVariables(info.Config?.Env);
            UpdatePortBindings(info);

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
                await _node.Monitoring.RefreshAsync(appendChartPoint: false);
            }
            else
            {
                _node.Monitoring.ApplyStoppedState();
            }
        }

        private async Task RefreshResourceLimitsAsync(
            Docker.DotNet.Models.ContainerInspectResponse info)
        {
            var monitoring = _node.Monitoring;

            try
            {
                var systemInfo = await _containerService.GetSystemInfoAsync();
                monitoring.MaxCpuCount = systemInfo.NCPU > 0
                    ? systemInfo.NCPU
                    : Environment.ProcessorCount;
                monitoring.MaxMemoryMb = systemInfo.MemTotal > 0
                    ? systemInfo.MemTotal / 1048576
                    : 32768;
            }
            catch
            {
                monitoring.MaxCpuCount = Environment.ProcessorCount;
                monitoring.MaxMemoryMb = 32768;
            }

            if (info.HostConfig == null) return;

            monitoring.TargetMemoryMb = info.HostConfig.Memory > 0
                ? info.HostConfig.Memory / 1048576
                : monitoring.MaxMemoryMb;
            monitoring.TargetCpuCount = info.HostConfig.NanoCPUs > 0
                ? info.HostConfig.NanoCPUs / 1_000_000_000.0
                : monitoring.MaxCpuCount;
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

        private void UpdateEnvironmentVariables(IList<string>? values)
        {
            var environmentVariables = values?.ToList() ?? new List<string>();
            _node.EnvironmentVariables = environmentVariables;
            EnvironmentItems.Clear();

            foreach (var entry in environmentVariables)
            {
                int separator = entry.IndexOf('=');
                EnvironmentItems.Add(new EnvironmentVariableDisplayItem
                {
                    Key = separator >= 0 ? entry[..separator] : entry,
                    Value = separator >= 0 ? entry[(separator + 1)..] : string.Empty
                });
            }
        }

        private void UpdatePortBindings(Docker.DotNet.Models.ContainerInspectResponse info)
        {
            _node.PortBindings = new List<string>();
            PortItems.Clear();
            if (info.HostConfig?.PortBindings == null) return;

            foreach (var binding in info.HostConfig.PortBindings)
            {
                var portParts = binding.Key.Split('/', 2);
                string containerPort = portParts[0];
                string protocol = portParts.Length > 1 ? portParts[1] : "tcp";

                foreach (var hostBinding in binding.Value ?? Array.Empty<Docker.DotNet.Models.PortBinding>())
                {
                    if (!string.IsNullOrEmpty(hostBinding.HostPort))
                    {
                        _node.PortBindings.Add($"{hostBinding.HostPort}:{containerPort}");
                    }

                    PortItems.Add(new PortBindingDisplayItem
                    {
                        HostIp = string.IsNullOrWhiteSpace(hostBinding.HostIP) ? "0.0.0.0" : hostBinding.HostIP,
                        HostPort = string.IsNullOrWhiteSpace(hostBinding.HostPort) ? "-" : hostBinding.HostPort,
                        ContainerPort = containerPort,
                        Protocol = protocol
                    });
                }
            }
        }

        private void UpdateMounts(IList<Docker.DotNet.Models.MountPoint>? mounts)
        {
            _cachedMounts = mounts?.ToList() ?? new List<Docker.DotNet.Models.MountPoint>();
            _node.MountCount = _cachedMounts.Count;

            NamedVolumeItems.Clear();
            BindMountItems.Clear();

            foreach (var mount in _cachedMounts)
            {
                var item = new MountDisplayItem
                {
                    Source = mount.Type == "volume" && !string.IsNullOrWhiteSpace(mount.Name)
                        ? mount.Name
                        : mount.Source,
                    Target = mount.Destination,
                    Mode = mount.RW ? "rw" : "ro"
                };

                if (mount.Type == "volume")
                    NamedVolumeItems.Add(item);
                else if (mount.Type == "bind")
                    BindMountItems.Add(item);
            }
        }

        private async Task OpenDetailWindowAsync()
        {
            if (!IsConnectedContainer && !IsConnectedSwarmService && !IsKubernetesResource) return;

            if (_node.IsSwarmService && !_node.IsRuntimeUnavailable && _node.IsDockerConnected)
                await _node.RefreshSwarmServiceAsync();

            if (_node.IsKubernetesResource && !_node.IsRuntimeUnavailable && _node.IsDockerConnected)
                await _node.RefreshKubernetesResourceAsync();

            await LoadLogsAsync();
            _dialogService.ShowContainerDetail(_node);
        }

        private void CopyContainerId()
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId)) return;

            _dialogService.SetClipboardText(_node.ContainerId);
            _dialogService.ShowInfo("컨테이너 ID가 클립보드에 복사되었습니다.", "복사 완료");
        }


        private async Task ExecuteUpdateResourcesAsync(object? parameter)
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId)) return;

            var monitoring = _node.Monitoring;
            bool confirm = _dialogService.ShowConfirm(
                $"컨테이너 리소스를 실시간으로 제한하시겠습니까? (재시작 없음)\n\n" +
                $"- 목표 CPU: {monitoring.TargetCpuCount:0.##} Core\n" +
                $"- 목표 Memory: {monitoring.TargetMemoryMb} MB",
                "실시간 리소스 변경");
            if (!confirm) return;

            try
            {
                await _containerService.UpdateContainerResourcesAsync(
                    _node.ContainerId,
                    monitoring.TargetCpuCount,
                    monitoring.TargetMemoryMb);
                _dialogService.ShowInfo("리소스 제한이 무중단으로 성공적으로 적용되었습니다.", "업데이트 완료");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"리소스 업데이트 실패: {ex.Message}\n(참고: CPU 제한이 호스트 코어 수를 넘을 수 없습니다.)",
                    "오류");
            }
        }

        public async Task StartLogStreamAsync(Action<string> onLogReceived, int initialTailCount = 100)
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
                    _logStreamCts.Token,
                    initialTailCount);
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

        private async Task ExportLogsAsync()
        {
            var fileName = _dialogService.ShowSaveFileDialog(
                "Text File|*.txt",
                ".txt",
                $"{_node.Name}_logs.txt",
                "Export Logs");
            if (string.IsNullOrWhiteSpace(fileName)) return;

            try
            {
                string logs;
                if (IsConnectedKubernetesPod)
                {
                    if (_containerService is not IKubernetesService kubernetesService)
                    {
                        _dialogService.ShowError("Kubernetes service가 연결되어 있지 않습니다.", "Export Logs");
                        return;
                    }

                    logs = await kubernetesService.GetKubernetesPodLogsAsync(
                        _node.KubernetesNamespace,
                        _node.KubernetesPodName,
                        tailCount: 0);
                }
                else if (IsConnectedContainer)
                {
                    logs = await _containerService.GetContainerLogsAsync(_node.ContainerId, tailCount: 0);
                }
                else
                {
                    return;
                }

                await File.WriteAllTextAsync(fileName, logs ?? string.Empty);
                _dialogService.ShowInfo("전체 로그가 파일로 저장되었습니다.", "저장 완료");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"전체 로그 내보내기 실패:\n{ex.Message}", "Export Logs");
            }
        }
    }
}
