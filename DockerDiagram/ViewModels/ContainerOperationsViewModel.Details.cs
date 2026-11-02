using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public sealed partial class ContainerOperationsViewModel
    {
        public async Task RefreshDetailsAsync(bool refreshMonitoring = true)
        {
            if (string.IsNullOrWhiteSpace(_node.ContainerId))
            {
                _node.ApplyRuntimeState(
                    RuntimeConnectionState.Disconnected,
                    RuntimeLifecycleState.Unknown);
                return;
            }

            var info = await _containerService.InspectContainerAsync(_node.ContainerId);
            _node.SetDockerConnection(true);

            string inspectedName = info.Name?.TrimStart('/') ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(inspectedName))
                _node.Name = inspectedName;
            if (!string.IsNullOrWhiteSpace(info.Config?.Image))
                _node.ImageName = info.Config.Image;

            await RefreshResourceLimitsAsync(info);

            RuntimeLifecycleState lifecycleState = info.State.Paused
                ? RuntimeLifecycleState.Paused
                : info.State.Running
                    ? RuntimeLifecycleState.Running
                    : RuntimeLifecycleState.Stopped;
            _node.SetLifecycleState(lifecycleState, info.State.Status);
            _node.StartedAt = DateTime.TryParse(info.State.StartedAt, out var started)
                ? started.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : info.State.StartedAt;
            _node.FinishedAt = _node.IsRunning
                ? "-"
                : DateTime.TryParse(info.State.FinishedAt, out var finished)
                    ? finished.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                    : info.State.FinishedAt;
            _node.StoppedRuntimeSummary = _node.IsRunning
                ? string.Empty
                : FormatStoppedRuntimeSummary(info.State.ExitCode, info.State.FinishedAt);
            _node.CreatedDate = info.Created.ToLocalTime().ToString(
                "yyyy-MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture);

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
            UpdateGeneralConfiguration(info);
            UpdateEnvironmentVariables(info.Config?.Env);
            UpdatePortBindings(info);

            if (info.NetworkSettings?.Networks != null)
                _node.Network.UpdateDetails(info.NetworkSettings.Networks);
            else
                _node.Network.UpdateDetails(
                    Array.Empty<KeyValuePair<string, Docker.DotNet.Models.EndpointSettings>>());
            try
            {
                await _node.Network.RefreshAvailableNetworksAsync();
            }
            catch (Exception ex)
            {
                AppLog.WarningThrottled(
                    $"container-network-candidates-{_node.ContainerId}",
                    "연결 가능한 Docker 네트워크 목록 갱신 실패",
                    ex);
            }

            UpdateMounts(info.Mounts, info.HostConfig?.Mounts);
            if (_node.IsRunning && refreshMonitoring)
            {
                await _node.Monitoring.RefreshAsync(appendChartPoint: false);
            }
            else if (!_node.IsRunning)
            {
                _node.Monitoring.ApplyStoppedState();
            }

            _node.LastDetailsRefresh = $"마지막 갱신 {DateTime.Now:HH:mm:ss}";
        }

        private static string FormatStoppedRuntimeSummary(long exitCode, string? finishedAt)
        {
            string finishedText = FormatRelativeFinishedTime(finishedAt);
            return $"Exit code {exitCode} · {finishedText}";
        }

        private static string FormatRelativeFinishedTime(string? finishedAt)
        {
            if (!DateTimeOffset.TryParse(
                    finishedAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset finished) ||
                finished.Year <= 1)
            {
                return "종료 시각 -";
            }

            TimeSpan elapsed = DateTimeOffset.UtcNow - finished.ToUniversalTime();
            if (elapsed < TimeSpan.Zero || elapsed.TotalMinutes < 1)
                return "방금 종료";
            if (elapsed.TotalHours < 1)
                return $"{Math.Max(1, (int)elapsed.TotalMinutes)}분 전 종료";
            if (elapsed.TotalDays < 1)
                return $"{Math.Max(1, (int)elapsed.TotalHours)}시간 전 종료";
            if (elapsed.TotalDays < 30)
                return $"{Math.Max(1, (int)elapsed.TotalDays)}일 전 종료";

            return $"{finished.ToLocalTime():yyyy-MM-dd HH:mm} 종료";
        }

        private async Task RefreshResourceLimitsAsync(
            Docker.DotNet.Models.ContainerInspectResponse info)
        {
            var monitoring = _node.Monitoring;
            double maxCpuCount;
            long maxMemoryMb;

            try
            {
                var systemInfo = await _containerService.GetSystemInfoAsync();
                maxCpuCount = systemInfo.NCPU > 0
                    ? systemInfo.NCPU
                    : Environment.ProcessorCount;
                maxMemoryMb = systemInfo.MemTotal > 0
                    ? systemInfo.MemTotal / 1048576
                    : 32768;
            }
            catch (Exception ex)
            {
                AppLog.WarningThrottled(
                    "container-system-info",
                    "컨테이너 리소스 한도 계산을 위한 Docker 시스템 정보 조회 실패",
                    ex);
                maxCpuCount = Environment.ProcessorCount;
                maxMemoryMb = 32768;
            }

            if (info.HostConfig == null)
            {
                monitoring.LoadResourceLimits(
                    maxCpuCount, maxMemoryMb, false, maxCpuCount, false, maxMemoryMb);
                return;
            }

            monitoring.LoadResourceLimits(
                maxCpuCount,
                maxMemoryMb,
                info.HostConfig.NanoCPUs > 0,
                info.HostConfig.NanoCPUs > 0
                    ? info.HostConfig.NanoCPUs / 1_000_000_000.0
                    : maxCpuCount,
                info.HostConfig.Memory > 0,
                info.HostConfig.Memory > 0
                    ? info.HostConfig.Memory / 1048576
                    : maxMemoryMb);
        }

        private void UpdateHealth(Docker.DotNet.Models.ContainerInspectResponse info)
        {
            string? health = info.State.Health?.Status;
            if (string.IsNullOrEmpty(health))
            {
                _node.HealthStatus = "검사 없음";
                _node.HealthColor = "#888888";
                return;
            }

            switch (health.ToLowerInvariant())
            {
                case "healthy":
                    _node.HealthStatus = "Healthy 💚";
                    _node.HealthColor = StatusColors.Running;
                    break;
                case "starting":
                    _node.HealthStatus = "Starting 💛";
                    _node.HealthColor = StatusColors.InProgress;
                    break;
                case "unhealthy":
                    _node.HealthStatus = "Unhealthy 💔";
                    _node.HealthColor = StatusColors.Failed;
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
            var environmentVariables = values?.ToList() ?? [];
            _node.EnvironmentVariables = environmentVariables;
            if (HasPendingConfigurationChanges) return;

            EnvironmentItems.Clear();

            foreach (var entry in environmentVariables)
            {
                int separator = entry.IndexOf('=');
                var item = new EnvironmentVariableDisplayItem
                {
                    Key = separator >= 0 ? entry[..separator] : entry,
                    Value = separator >= 0 ? entry[(separator + 1)..] : string.Empty
                };
                item.Changed = MarkConfigurationDirty;
                EnvironmentItems.Add(item);
            }
        }

        private void UpdateGeneralConfiguration(Docker.DotNet.Models.ContainerInspectResponse info)
        {
            GeneralConfigurationItems.Clear();
            LabelItems.Clear();
            var config = info.Config;
            var host = info.HostConfig;
            if (config == null) return;

            void Add(string name, string? value) => GeneralConfigurationItems.Add(
                new ConfigurationDisplayItem
                {
                    Name = name,
                    Value = string.IsNullOrWhiteSpace(value) ? "-" : value
                });

            Add("이미지", config.Image);
            Add("진입점 (Entrypoint)", config.Entrypoint == null ? null : string.Join(" ", config.Entrypoint));
            Add("실행 명령", config.Cmd == null ? null : string.Join(" ", config.Cmd));
            Add("작업 디렉터리", config.WorkingDir);
            Add("사용자", config.User);
            Add("호스트 이름", config.Hostname);
            Add("터미널 / 표준 입력", $"TTY={config.Tty}, OpenStdin={config.OpenStdin}");
            Add("재시작 정책", _node.RestartPolicy);
            Add("네트워크 모드", host?.NetworkMode);
            Add("PID / IPC 모드", $"PID={host?.PidMode ?? "-"}, IPC={host?.IpcMode ?? "-"}");
            Add("권한 / 읽기 전용 루트", $"Privileged={host?.Privileged == true}, ReadOnly={host?.ReadonlyRootfs == true}");
            Add("자동 삭제", (host?.AutoRemove == true).ToString());
            Add("추가된 Capability", host?.CapAdd == null ? null : string.Join(", ", host.CapAdd));
            Add("제거된 Capability", host?.CapDrop == null ? null : string.Join(", ", host.CapDrop));
            Add("보안 옵션", host?.SecurityOpt == null ? null : string.Join(", ", host.SecurityOpt));
            Add("DNS", host?.DNS == null ? null : string.Join(", ", host.DNS));
            Add("종료 신호", config.StopSignal);

            foreach (var pair in config.Labels ?? new Dictionary<string, string>())
            {
                LabelItems.Add(new EnvironmentVariableDisplayItem
                {
                    Key = pair.Key,
                    Value = pair.Value
                });
            }
        }

        private void UpdatePortBindings(Docker.DotNet.Models.ContainerInspectResponse info)
        {
            var nodePortBindings = new List<string>();
            var portSummaries = new List<string>();
            var draftItems = new List<PortBindingDisplayItem>();
            var livePortBindings = info.NetworkSettings?.Ports ?? info.HostConfig?.PortBindings;
            if (livePortBindings == null)
            {
                _node.PortBindings = nodePortBindings;
                _node.PortInfo = string.Empty;
                if (!HasPendingConfigurationChanges)
                    PortItems.Clear();
                return;
            }

            foreach (var binding in livePortBindings)
            {
                var portParts = binding.Key.Split('/', 2);
                string containerPort = portParts[0];
                string protocol = portParts.Length > 1 ? portParts[1] : "tcp";

                var hostBindings = binding.Value ?? Array.Empty<Docker.DotNet.Models.PortBinding>();
                if (hostBindings.Count == 0)
                {
                    draftItems.Add(new PortBindingDisplayItem
                    {
                        HostIp = "0.0.0.0",
                        HostPort = string.Empty,
                        ContainerPort = containerPort,
                        Protocol = protocol
                    });
                }

                foreach (var hostBinding in hostBindings)
                {
                    if (!string.IsNullOrEmpty(hostBinding.HostPort))
                    {
                        nodePortBindings.Add($"{hostBinding.HostPort}:{containerPort}");
                        string hostIpPrefix = string.IsNullOrWhiteSpace(hostBinding.HostIP) ||
                                              hostBinding.HostIP is "0.0.0.0" or "::"
                            ? string.Empty
                            : $"{hostBinding.HostIP}:";
                        portSummaries.Add(
                            $"{hostIpPrefix}{hostBinding.HostPort}->{containerPort}/{protocol}");
                    }

                    draftItems.Add(new PortBindingDisplayItem
                    {
                        HostIp = string.IsNullOrWhiteSpace(hostBinding.HostIP) ? "0.0.0.0" : hostBinding.HostIP,
                        HostPort = hostBinding.HostPort ?? string.Empty,
                        ContainerPort = containerPort,
                        Protocol = protocol
                    });
                }
            }

            _node.PortBindings = nodePortBindings;
            _node.PortInfo = string.Join(", ", portSummaries.Distinct(StringComparer.OrdinalIgnoreCase));
            if (!HasPendingConfigurationChanges)
            {
                PortItems.Clear();
                foreach (var item in draftItems)
                {
                    item.Changed = MarkConfigurationDirty;
                    PortItems.Add(item);
                }
            }
        }

        private void UpdateMounts(
            IList<Docker.DotNet.Models.MountPoint>? mounts,
            IList<Docker.DotNet.Models.Mount>? configuredMounts)
        {
            _cachedMounts = mounts?.ToList() ?? [];
            List<ContainerMountSpec> specifications = ContainerMountMapper.FromInspect(_cachedMounts, configuredMounts);
            _node.SetMountSpecifications(specifications);
            var namedItems = new List<MountDisplayItem>();
            var bindItems = new List<MountDisplayItem>();
            var tmpfsItems = new List<MountDisplayItem>();

            foreach (ContainerMountSpec mount in specifications)
            {
                var item = new MountDisplayItem
                {
                    Kind = mount.Kind,
                    Source = mount.Source,
                    Target = mount.Target,
                    Mode = mount.ReadOnly ? "ro" : "rw",
                    BindPropagation = mount.BindPropagation,
                    VolumeNoCopy = mount.VolumeNoCopy,
                    TmpfsSizeBytes = mount.TmpfsSizeBytes,
                    TmpfsMode = Convert.ToString(mount.TmpfsMode, 8)
                };

                if (mount.Kind == MountKind.Volume)
                    namedItems.Add(item);
                else if (mount.Kind == MountKind.Bind)
                    bindItems.Add(item);
                else if (mount.Kind == MountKind.Tmpfs)
                    tmpfsItems.Add(item);
            }

            if (!HasPendingStorageChanges)
            {
                NamedVolumeItems.Clear();
                BindMountItems.Clear();
                TmpfsMountItems.Clear();
                foreach (var item in namedItems)
                {
                    item.Changed = MarkStorageDirty;
                    NamedVolumeItems.Add(item);
                }
                foreach (var item in bindItems)
                {
                    item.Changed = MarkStorageDirty;
                    BindMountItems.Add(item);
                }
                foreach (var item in tmpfsItems)
                {
                    item.Changed = MarkStorageDirty;
                    TmpfsMountItems.Add(item);
                }
            }

            _node.ParentSheet?.SynchronizeContainerMounts(_node, specifications);
        }

        private async Task OpenDetailWindowAsync()
        {
            if (!IsConnectedContainer && !IsConnectedSwarmService && !IsKubernetesResource) return;

            if (_node.IsSwarmService && !_node.IsRuntimeUnavailable && _node.IsDockerConnected)
                await _node.RefreshSwarmServiceAsync();

            if (_node.IsKubernetesResource && !_node.IsRuntimeUnavailable && _node.IsDockerConnected)
                await _node.RefreshKubernetesResourceAsync();

            if (IsConnectedContainer)
                await _node.RefreshDetailsAsync();

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
            double requestedCpu = monitoring.CpuLimitEnabled ? monitoring.TargetCpuCount : 0;
            long requestedMemory = monitoring.MemoryLimitEnabled ? monitoring.TargetMemoryMb : 0;
            bool confirm = _dialogService.ShowConfirm(
                $"컨테이너 리소스를 실시간으로 제한하시겠습니까? (재시작 없음)\n\n" +
                $"- CPU 제한: {(monitoring.CpuLimitEnabled ? $"{monitoring.TargetCpuCount:0.##} Core" : "무제한")}\n" +
                $"- Memory 제한: {(monitoring.MemoryLimitEnabled ? $"{monitoring.TargetMemoryMb} MB" : "무제한")}\n\n" +
                "메모리 스왑 설정은 변경하지 않습니다.",
                "실시간 리소스 변경");
            if (!confirm) return;

            try
            {
                await _containerService.UpdateContainerResourcesAsync(
                    _node.ContainerId,
                    requestedCpu,
                    requestedMemory);
                monitoring.AcceptResourceLimitDraft();
                await _node.RefreshDetailsAsync(refreshMonitoring: false);
                _dialogService.ShowInfo("리소스 제한이 무중단으로 성공적으로 적용되었습니다.", "업데이트 완료");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"리소스 업데이트 실패: {ex.Message}\n(참고: CPU 제한이 호스트 코어 수를 넘을 수 없습니다.)",
                    "오류");
            }
        }

        private async Task ResetResourceLimitsAsync(object? parameter)
        {
            _node.Monitoring.DiscardResourceLimitDraft();
            await _node.RefreshDetailsAsync(refreshMonitoring: false);
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
                AppLog.Warning("컨테이너 로그 스트림 오류", ex);
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
                    _node.ContainerLogs = string.IsNullOrWhiteSpace(logs) ? string.Empty : logs;
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
                _node.ContainerLogs = string.IsNullOrEmpty(logs) ? string.Empty : logs;
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
