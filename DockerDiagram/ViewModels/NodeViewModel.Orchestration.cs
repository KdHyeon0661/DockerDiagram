using DockerDiagram.Contracts;
using DockerDiagram.Common;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DockerDiagram.ViewModels
{
    public partial class NodeViewModel
    {
        public async Task RefreshSwarmServiceAsync()
        {
            if (!IsSwarmService || _containerService is not ISwarmService swarmService)
                return;

            if (IsRuntimeUnavailable)
            {
                _dialogService.ShowInfo("현재 시트는 오프라인 스냅샷입니다. 런타임을 사용할 수 있을 때 다시 동기화해 주세요.", "Offline Snapshot");
                return;
            }

            try
            {
                var services = await swarmService.GetSwarmServicesAsync();
                var match = services.FirstOrDefault(service =>
                                !string.IsNullOrWhiteSpace(ContainerId) &&
                                service.Id.Equals(ContainerId, StringComparison.OrdinalIgnoreCase))
                            ?? services.FirstOrDefault(service =>
                                service.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    IsDockerConnected = false;
                    StatusColor = "#808080";
                    SwarmServiceInspectJson = "Swarm service not found.";
                    SwarmTasks.Clear();
                    OnPropertyChanged(nameof(SwarmTaskSummary));
                    RaiseSwarmCommandStates();
                    return;
                }

                ContainerId = match.Id;
                Name = match.Name;
                ImageName = match.Image;
                PortInfo = match.Ports;
                SwarmMode = match.SwarmMode;
                SwarmDesiredReplicas = match.SwarmDesiredReplicas;
                SwarmRunningReplicas = match.SwarmRunningReplicas;
                TargetSwarmReplicas = SwarmDesiredReplicas;
                DetailStatus = PortInfo;
                StatusColor = match.StateColor;
                IsDockerConnected = true;
                IsRunning = true;
                IsPaused = false;

                object raw = await swarmService.InspectSwarmServiceRawAsync(ContainerId);
                SwarmServiceInspectJson = raw.ToString() ?? string.Empty;
                var tasks = await swarmService.GetSwarmServiceTasksAsync(ContainerId);
                SwarmTasks.Clear();
                foreach (var task in tasks)
                    SwarmTasks.Add(task);
                OnPropertyChanged(nameof(CanScaleSwarmService));
                OnPropertyChanged(nameof(SwarmReplicaSummary));
                OnPropertyChanged(nameof(SwarmTaskSummary));
                RaiseSwarmCommandStates();
            }
            catch (Exception ex)
            {
                SwarmServiceInspectJson = $"Swarm service refresh failed: {ex.Message}";
                _dialogService.ShowError($"Swarm service 갱신 실패:\n{ex.Message}", "Swarm Service");
            }
        }

        private async Task ExecuteScaleSwarmServiceAsync()
        {
            if (!IsSwarmService || _containerService is not ISwarmService swarmService) return;
            if (!CanScaleSwarmService)
            {
                _dialogService.ShowInfo("global mode service는 replica 수를 직접 조절할 수 없습니다.", "Swarm Scale");
                return;
            }

            try
            {
                await swarmService.ScaleSwarmServiceAsync(ContainerId, TargetSwarmReplicas);
                await RefreshSwarmServiceAsync();
                _dialogService.ShowInfo($"'{Name}' service replica를 {TargetSwarmReplicas}개로 변경했습니다.", "Swarm Scale");
                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Swarm scale 실패:\n{ex.Message}", "Swarm Scale");
            }
        }

        private async Task ExecuteRemoveSwarmServiceAsync()
        {
            if (!IsSwarmService || _containerService is not ISwarmService swarmService) return;
            if (!_dialogService.ShowConfirm(
                    $"Swarm service '{Name}'을 Docker에서 삭제하시겠습니까?\n시트의 노드도 함께 제거됩니다.",
                    "Remove Swarm Service"))
            {
                return;
            }

            try
            {
                await swarmService.RemoveSwarmServiceAsync(ContainerId);

                if (ParentSheet != null)
                    await ParentSheet.RemoveNodeAsync(this);

                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Swarm service 삭제 실패:\n{ex.Message}", "Remove Swarm Service");
            }
        }

        private void RaiseSwarmCommandStates()
        {
            OnPropertyChanged(nameof(IsRuntimeUnavailable));
            OnPropertyChanged(nameof(IsOfflineSnapshot));
            OnPropertyChanged(nameof(CanControlSwarmService));
            OnPropertyChanged(nameof(CanScaleSwarmService));
            OnPropertyChanged(nameof(SwarmReplicaSummary));

            if (RefreshSwarmServiceCommand is AsyncRelayCommand refresh)
                refresh.RaiseCanExecuteChanged();
            if (ScaleSwarmServiceCommand is AsyncRelayCommand scale)
                scale.RaiseCanExecuteChanged();
            if (RemoveSwarmServiceCommand is AsyncRelayCommand remove)
                remove.RaiseCanExecuteChanged();
        }

        public async Task RefreshKubernetesPodAsync()
        {
            if (!IsKubernetesPod || _containerService is not IKubernetesService kubernetesService)
                return;

            if (IsRuntimeUnavailable)
            {
                var message = "현재 시트는 오프라인 스냅샷입니다. 런타임을 사용할 수 있을 때 다시 동기화해 주세요.";
                KubernetesPodDescribeText = message;
                KubernetesPodYamlText = message;
                KubernetesPodJsonText = message;
                ContainerLogs = message;
                return;
            }

            try
            {
                var pods = await kubernetesService.GetKubernetesPodsAsync();
                var match = pods.FirstOrDefault(pod =>
                                !string.IsNullOrWhiteSpace(ContainerId) &&
                                pod.Id.Equals(ContainerId, StringComparison.OrdinalIgnoreCase))
                            ?? pods.FirstOrDefault(pod =>
                                pod.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    IsDockerConnected = false;
                    StatusColor = "#808080";
                    KubernetesPodDescribeText = "Kubernetes Pod not found.";
                    KubernetesPodYamlText = "Kubernetes Pod not found.";
                    KubernetesPodJsonText = "Kubernetes Pod not found.";
                    ContainerLogs = "Kubernetes Pod not found.";
                    RaiseKubernetesCommandStates();
                    return;
                }

                ContainerId = match.Id;
                Name = match.Name;
                ImageName = match.Image;
                PortInfo = match.Ports;
                DetailStatus = match.State;
                StatusColor = match.StateColor;
                KubernetesNamespace = match.KubernetesNamespace;
                KubernetesNodeName = match.KubernetesNodeName;
                KubernetesReady = match.KubernetesReady;
                KubernetesRestarts = match.KubernetesRestarts;
                KubernetesPodIp = match.KubernetesPodIp;
                IsDockerConnected = true;
                IsRunning = match.State.Equals("Running", StringComparison.OrdinalIgnoreCase);
                IsPaused = false;

                object raw = await kubernetesService.InspectKubernetesPodRawAsync(KubernetesNamespace, KubernetesPodName);
                KubernetesPodJsonText = raw.ToString() ?? string.Empty;
                KubernetesPodYamlText = await kubernetesService.GetKubernetesPodYamlAsync(KubernetesNamespace, KubernetesPodName);
                KubernetesPodDescribeText = await kubernetesService.DescribeKubernetesPodAsync(KubernetesNamespace, KubernetesPodName);
                ContainerLogs = await kubernetesService.GetKubernetesPodLogsAsync(KubernetesNamespace, KubernetesPodName, 500);
                if (string.IsNullOrWhiteSpace(ContainerLogs))
                    ContainerLogs = "(No logs found)";

                RaiseKubernetesCommandStates();
            }
            catch (Exception ex)
            {
                var message = $"Kubernetes Pod refresh failed: {ex.Message}";
                KubernetesPodDescribeText = message;
                KubernetesPodYamlText = message;
                KubernetesPodJsonText = message;
                ContainerLogs = message;
                _dialogService.ShowError($"Kubernetes Pod 갱신 실패:\n{ex.Message}", "Kubernetes Pod");
            }
        }

        public async Task RefreshKubernetesResourceAsync()
        {
            if (!IsKubernetesResource || _containerService is not IKubernetesService kubernetesService)
                return;

            if (IsKubernetesPod)
            {
                await RefreshKubernetesPodAsync();
                return;
            }

            if (IsRuntimeUnavailable)
            {
                var message = "현재 시트는 오프라인 스냅샷입니다. 런타임을 사용할 수 있을 때 다시 동기화해 주세요.";
                KubernetesPodDescribeText = message;
                KubernetesPodYamlText = message;
                KubernetesPodJsonText = message;
                return;
            }

            try
            {
                object raw = await kubernetesService.InspectKubernetesResourceRawAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);
                KubernetesPodJsonText = raw.ToString() ?? string.Empty;
                KubernetesPodYamlText = await kubernetesService.GetKubernetesResourceYamlAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);
                KubernetesPodDescribeText = await kubernetesService.DescribeKubernetesResourceAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);
                UpdateKubernetesReplicaStateFromJson();
                IsDockerConnected = true;
                RaiseKubernetesCommandStates();
            }
            catch (Exception ex)
            {
                var message = $"Kubernetes resource refresh failed: {ex.Message}";
                KubernetesPodDescribeText = message;
                KubernetesPodYamlText = message;
                KubernetesPodJsonText = message;
                _dialogService.ShowError($"Kubernetes 리소스 갱신 실패:\n{ex.Message}", "Kubernetes Resource");
            }
        }

        private async Task ExecuteScaleKubernetesDeploymentAsync()
        {
            if (!CanScaleKubernetesDeployment || _containerService is not IKubernetesService kubernetesService)
                return;

            try
            {
                await kubernetesService.ScaleKubernetesDeploymentAsync(
                    KubernetesNamespace,
                    KubernetesResourceName,
                    TargetKubernetesReplicas);
                await RefreshKubernetesResourceAsync();
                _dialogService.ShowInfo($"'{KubernetesResourceName}' deployment replica를 {TargetKubernetesReplicas}개로 변경했습니다.", "Kubernetes Scale");
                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes scale 실패:\n{ex.Message}", "Kubernetes Scale");
            }
        }

        private async Task ExecuteRestartKubernetesRolloutAsync()
        {
            if (!CanRestartKubernetesRollout || _containerService is not IKubernetesService kubernetesService)
                return;

            if (!_dialogService.ShowConfirm(
                    $"'{KubernetesResourceName}' deployment를 rollout restart 하시겠습니까?",
                    "Kubernetes Rollout Restart"))
            {
                return;
            }

            try
            {
                await kubernetesService.RolloutRestartKubernetesResourceAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);
                await RefreshKubernetesResourceAsync();
                _dialogService.ShowInfo($"'{KubernetesResourceName}' deployment rollout restart를 요청했습니다.", "Kubernetes Rollout Restart");
                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes rollout restart 실패:\n{ex.Message}", "Kubernetes Rollout Restart");
            }
        }

        private async Task ExecuteDeleteKubernetesResourceAsync()
        {
            if (!CanDeleteKubernetesResource || _containerService is not IKubernetesService kubernetesService)
                return;

            string kind = string.IsNullOrWhiteSpace(KubernetesKind) ? KubernetesApiResource : KubernetesKind;
            if (!_dialogService.ShowConfirm(
                    $"{kind} '{KubernetesNamespace}/{KubernetesResourceName}'을 Kubernetes에서 삭제하시겠습니까?\n시트의 노드도 함께 제거됩니다.",
                    "Delete Kubernetes Resource"))
            {
                return;
            }

            try
            {
                await kubernetesService.DeleteKubernetesResourceAsync(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName);

                if (ParentSheet != null)
                    await ParentSheet.RemoveNodeAsync(this);

                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes 리소스 삭제 실패:\n{ex.Message}", "Delete Kubernetes Resource");
            }
        }

        private void ExecuteOpenKubernetesLogsFollow()
        {
            if (!CanOpenKubernetesLogsFollow || _containerService is not IKubernetesService kubernetesService)
                return;

            try
            {
                kubernetesService.OpenKubernetesLogsFollow(KubernetesNamespace, KubernetesPodName);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes live logs 실행 실패:\n{ex.Message}", "Kubernetes Logs");
            }
        }

        private void ExecuteOpenKubernetesPortForward()
        {
            if (!CanOpenKubernetesPortForward || _containerService is not IKubernetesService kubernetesService)
                return;

            var (defaultLocalPort, defaultRemotePort) = GetDefaultKubernetesPortForwardPorts();
            if (!_dialogService.TryShowKubernetesPortForwardDialog(
                    KubernetesKind,
                    $"{KubernetesNamespace}/{KubernetesResourceName}",
                    defaultLocalPort,
                    defaultRemotePort,
                    out int localPort,
                    out int remotePort))
            {
                return;
            }

            try
            {
                kubernetesService.OpenKubernetesPortForward(
                    KubernetesApiResource,
                    KubernetesNamespace,
                    KubernetesResourceName,
                    localPort,
                    remotePort);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes port-forward 실행 실패:\n{ex.Message}", "Kubernetes Port Forward");
            }
        }

        private async Task ExecuteExportKubernetesYamlAsync()
        {
            if (!IsKubernetesResource)
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(KubernetesPodYamlText) && CanRefreshKubernetesResource)
                    await RefreshKubernetesResourceAsync();

                if (string.IsNullOrWhiteSpace(KubernetesPodYamlText))
                {
                    _dialogService.ShowInfo("내보낼 Kubernetes YAML이 없습니다. 리소스를 먼저 갱신해 주세요.", "Export Kubernetes YAML");
                    return;
                }

                string defaultName = $"{SanitizeFileName(KubernetesKind)}-{SanitizeFileName(KubernetesResourceName)}.yaml";
                string? path = _dialogService.ShowSaveFileDialog(
                    "Kubernetes YAML (*.yaml)|*.yaml|YAML (*.yml)|*.yml|All files (*.*)|*.*",
                    ".yaml",
                    defaultName,
                    "Export Kubernetes YAML");
                if (string.IsNullOrWhiteSpace(path))
                    return;

                File.WriteAllText(path, KubernetesPodYamlText, Encoding.UTF8);
                _dialogService.ShowInfo($"Kubernetes YAML을 내보냈습니다.\n{path}", "Export Kubernetes YAML");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes YAML 내보내기 실패:\n{ex.Message}", "Export Kubernetes YAML");
            }
        }

        private async Task ExecuteApplyKubernetesManifestAsync()
        {
            if (!CanApplyKubernetesManifest || _containerService is not IKubernetesService kubernetesService)
                return;

            string? path = _dialogService.ShowOpenFileDialog(
                "Kubernetes YAML (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*",
                "Apply Kubernetes Manifest");
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!_dialogService.ShowConfirm(
                    $"선택한 manifest를 현재 Kubernetes context에 적용하시겠습니까?\n{path}",
                    "Apply Kubernetes Manifest"))
            {
                return;
            }

            try
            {
                await kubernetesService.ApplyKubernetesManifestAsync(path);
                await RefreshKubernetesResourceAsync();
                _dialogService.ShowInfo("Kubernetes manifest를 적용했습니다.", "Apply Kubernetes Manifest");
                NotifyModified();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Kubernetes manifest 적용 실패:\n{ex.Message}", "Apply Kubernetes Manifest");
            }
        }

        private void UpdateKubernetesReplicaStateFromJson()
        {
            if (!IsKubernetesDeployment || string.IsNullOrWhiteSpace(KubernetesPodJsonText))
                return;

            try
            {
                var raw = JObject.Parse(KubernetesPodJsonText);
                KubernetesDesiredReplicas = raw["spec"]?["replicas"]?.Value<int>() ?? KubernetesDesiredReplicas;
                KubernetesReadyReplicas = raw["status"]?["readyReplicas"]?.Value<int>() ?? 0;
                TargetKubernetesReplicas = KubernetesDesiredReplicas;
                OnPropertyChanged(nameof(KubernetesReplicaSummary));
            }
            catch
            {
                // 저장된 스냅샷의 JSON이 비어 있거나 오래된 형식이어도 상세 창은 계속 열려야 합니다.
            }
        }

        private (int LocalPort, int RemotePort) GetDefaultKubernetesPortForwardPorts()
        {
            const int fallbackLocal = 8080;
            const int fallbackRemote = 80;

            try
            {
                if (string.IsNullOrWhiteSpace(KubernetesPodJsonText))
                    return (fallbackLocal, fallbackRemote);

                var raw = JObject.Parse(KubernetesPodJsonText);
                int? remotePort = KubernetesKind.Equals("Service", StringComparison.OrdinalIgnoreCase)
                    ? raw["spec"]?["ports"]?.OfType<JObject>().Select(port => port["port"]?.Value<int>()).FirstOrDefault(port => port.HasValue)
                    : raw.SelectTokens("$..containers[*].ports[*].containerPort").Select(token => token.Value<int?>()).FirstOrDefault(port => port.HasValue);

                int resolvedRemote = remotePort.GetValueOrDefault(fallbackRemote);
                int resolvedLocal = resolvedRemote is 80 or 443 ? 8080 : resolvedRemote;
                return (resolvedLocal, resolvedRemote);
            }
            catch
            {
                return (fallbackLocal, fallbackRemote);
            }
        }

        private static string SanitizeFileName(string value)
        {
            string fallback = string.IsNullOrWhiteSpace(value) ? "resource" : value;
            var invalid = Path.GetInvalidFileNameChars();
            var chars = fallback.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
            string sanitized = new string(chars).Trim('-', ' ');
            return string.IsNullOrWhiteSpace(sanitized) ? "resource" : sanitized;
        }

        private void RaiseKubernetesCommandStates()
        {
            OnPropertyChanged(nameof(IsRuntimeUnavailable));
            OnPropertyChanged(nameof(IsOfflineSnapshot));
            OnPropertyChanged(nameof(IsDockerRuntimeContainer));
            OnPropertyChanged(nameof(IsGenericKubernetesResource));
            OnPropertyChanged(nameof(IsKubernetesResource));
            OnPropertyChanged(nameof(CanRefreshKubernetesResource));
            OnPropertyChanged(nameof(CanRefreshKubernetesPod));
            OnPropertyChanged(nameof(IsKubernetesDeployment));
            OnPropertyChanged(nameof(CanScaleKubernetesDeployment));
            OnPropertyChanged(nameof(CanRestartKubernetesRollout));
            OnPropertyChanged(nameof(CanDeleteKubernetesResource));
            OnPropertyChanged(nameof(CanOpenKubernetesLogsFollow));
            OnPropertyChanged(nameof(CanOpenKubernetesPortForward));
            OnPropertyChanged(nameof(CanExportKubernetesYaml));
            OnPropertyChanged(nameof(CanApplyKubernetesManifest));
            OnPropertyChanged(nameof(KubernetesReplicaSummary));
            OnPropertyChanged(nameof(KubernetesPodName));
            OnPropertyChanged(nameof(KubernetesResourceName));

            if (RefreshKubernetesPodCommand is AsyncRelayCommand refresh)
                refresh.RaiseCanExecuteChanged();
            if (RefreshKubernetesResourceCommand is AsyncRelayCommand refreshResource)
                refreshResource.RaiseCanExecuteChanged();
            if (ScaleKubernetesDeploymentCommand is AsyncRelayCommand scale)
                scale.RaiseCanExecuteChanged();
            if (RestartKubernetesRolloutCommand is AsyncRelayCommand restart)
                restart.RaiseCanExecuteChanged();
            if (DeleteKubernetesResourceCommand is AsyncRelayCommand delete)
                delete.RaiseCanExecuteChanged();
            if (OpenKubernetesLogsFollowCommand is RelayCommand logsFollow)
                logsFollow.RaiseCanExecuteChanged();
            if (OpenKubernetesPortForwardCommand is RelayCommand portForward)
                portForward.RaiseCanExecuteChanged();
            if (ExportKubernetesYamlCommand is AsyncRelayCommand exportYaml)
                exportYaml.RaiseCanExecuteChanged();
            if (ApplyKubernetesManifestCommand is AsyncRelayCommand applyManifest)
                applyManifest.RaiseCanExecuteChanged();
        }
    }
}
