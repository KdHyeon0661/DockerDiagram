using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json;

namespace DockerDiagram.ViewModels
{
    public sealed partial class ContainerOperationsViewModel
    {
        private void AddEnvironmentDraft()
        {
            var item = new EnvironmentVariableDisplayItem { Key = "NEW_VARIABLE", Value = string.Empty };
            item.Changed = MarkConfigurationDirty;
            EnvironmentItems.Add(item);
            MarkConfigurationDirty();
        }

        private void RemoveEnvironmentDraft(EnvironmentVariableDisplayItem? item)
        {
            if (item == null || !EnvironmentItems.Remove(item)) return;
            MarkConfigurationDirty();
        }

        private static void ToggleEnvironmentSecret(EnvironmentVariableDisplayItem? item)
        {
            if (item?.IsSecret != true) return;
            item.IsValueVisible = !item.IsValueVisible;
        }

        private void AddPortDraft()
        {
            var item = new PortBindingDisplayItem
            {
                HostIp = "0.0.0.0",
                HostPort = string.Empty,
                ContainerPort = "8080",
                Protocol = "tcp"
            };
            item.Changed = MarkConfigurationDirty;
            PortItems.Add(item);
            MarkConfigurationDirty();
        }

        private void RemovePortDraft(PortBindingDisplayItem? item)
        {
            if (item == null || !PortItems.Remove(item)) return;
            MarkConfigurationDirty();
        }

        private void AddNamedVolumeDraft()
        {
            var item = new MountDisplayItem { Kind = MountKind.Volume, Source = "volume-name", Target = "/data", Mode = "rw" };
            item.Changed = MarkStorageDirty;
            NamedVolumeItems.Add(item);
            MarkStorageDirty();
        }

        private void AddBindMountDraft()
        {
            var item = new MountDisplayItem { Kind = MountKind.Bind, Source = string.Empty, Target = "/data", Mode = "rw", BindPropagation = "rprivate" };
            item.Changed = MarkStorageDirty;
            BindMountItems.Add(item);
            MarkStorageDirty();
        }

        private void AddTmpfsMountDraft()
        {
            var item = new MountDisplayItem
            {
                Kind = MountKind.Tmpfs,
                Target = "/tmp",
                Mode = "rw",
                TmpfsMode = "1777"
            };
            item.Changed = MarkStorageDirty;
            TmpfsMountItems.Add(item);
            MarkStorageDirty();
        }

        private void RemoveNamedVolumeDraft(MountDisplayItem? item)
        {
            if (item == null || !NamedVolumeItems.Remove(item)) return;
            MarkStorageDirty();
        }

        private void RemoveBindMountDraft(MountDisplayItem? item)
        {
            if (item == null || !BindMountItems.Remove(item)) return;
            MarkStorageDirty();
        }

        private void RemoveTmpfsMountDraft(MountDisplayItem? item)
        {
            if (item == null || !TmpfsMountItems.Remove(item)) return;
            MarkStorageDirty();
        }

        private void BrowseBindMount(MountDisplayItem? item)
        {
            if (item == null) return;
            ConnectionProfile? profile = _node.ParentSheet?.Profile;
            bool isLocal = profile?.Type == EndpointType.Local ||
                           (profile?.Type == EndpointType.DockerContext &&
                            profile.DockerEndpoint?.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) == true);
            if (!isLocal)
            {
                _dialogService.ShowInfo(
                    "Bind 경로는 Docker가 실행되는 원격 호스트 기준입니다. 원격 경로를 직접 입력하세요.",
                    "원격 Docker 경로");
                return;
            }

            string? selected = _dialogService.ShowOpenFolderDialog(
                "Bind mount로 사용할 호스트 폴더 선택",
                Directory.Exists(item.Source) ? item.Source : null);
            if (!string.IsNullOrWhiteSpace(selected))
                item.Source = selected;
        }

        private void MarkConfigurationDirty()
        {
            HasPendingConfigurationChanges = true;
            RecreateStatus = "Configuration 변경사항이 적용 대기 중입니다.";
        }

        private void MarkStorageDirty()
        {
            HasPendingStorageChanges = true;
            RecreateStatus = "Storage 변경사항이 적용 대기 중입니다.";
        }

        private void RaiseRecreateCommandStates()
        {
            ApplyRecreateChangesCommand?.RaiseCanExecuteChanged();
            ResetRecreateChangesCommand?.RaiseCanExecuteChanged();
        }

        private async Task ResetRecreateChangesAsync(object? parameter)
        {
            HasPendingConfigurationChanges = false;
            HasPendingStorageChanges = false;
            await _node.RefreshDetailsAsync(refreshMonitoring: false);
            HasPendingConfigurationChanges = false;
            HasPendingStorageChanges = false;
            RecreateStatus = "변경사항을 취소하고 Docker의 현재 설정을 다시 불러왔습니다.";
        }

        private async Task ApplyRecreateChangesAsync(object? parameter)
        {
            if (!HasPendingRecreateChanges || string.IsNullOrWhiteSpace(_node.ContainerId)) return;
            if (!ValidateDrafts(out string validationError))
            {
                _dialogService.ShowError(validationError, "컨테이너 설정 확인");
                return;
            }

            if (!_dialogService.ShowConfirm(
                    "환경 변수, 포트 또는 마운트 변경에는 컨테이너 재생성이 필요합니다.\n\n" +
                    "현재 writable layer를 임시 snapshot 이미지로 보존한 뒤 교체하며, " +
                    "실패하면 원본 설정으로 자동 복구합니다. 계속하시겠습니까?",
                    "컨테이너 재생성"))
            {
                return;
            }

            string originalContainerId = _node.ContainerId;
            ContainerInspectResponse? inspect = null;
            bool originalRemoved = false;
            bool wasRunning = false;
            string? replacementId = null;
            string? snapshotImageId = null;

            _dialogService.SetBusyCursor(true);
            RecreateStatus = "현재 컨테이너 파일시스템을 snapshot으로 보존하는 중...";
            try
            {
                inspect = await _containerService.InspectContainerAsync(originalContainerId);
                if (inspect.HostConfig?.AutoRemove == true)
                {
                    throw new InvalidOperationException(
                        "--rm(AutoRemove) 컨테이너는 중지 시 즉시 삭제되므로 안전한 재생성을 지원하지 않습니다.");
                }

                wasRunning = inspect.State?.Running == true;
                string tag = $"{MakeTagPart(_node.Name)}-{DateTime.UtcNow:yyyyMMddHHmmss}";
                string snapshotReference = $"dockerdiagram-recreate-snapshot:{tag}";
                snapshotImageId = await _containerService.CommitContainerAsync(
                    originalContainerId,
                    "dockerdiagram-recreate-snapshot",
                    tag,
                    "DockerDiagram safe container reconfiguration",
                    "DockerDiagram",
                    pause: true);

                // 재생성된 컨테이너가 writable layer의 기존 파일도 그대로 사용하게 합니다.
                inspect.Config.Image = snapshotReference;

                var environment = EnvironmentItems
                    .Select(item => $"{item.Key.Trim()}={item.Value}")
                    .ToList();
                BuildPortDrafts(out var portBindings, out var exposedPorts);
                var recreatedMounts = BuildRecreatedMounts(inspect);

                if (wasRunning)
                    await _containerService.StopContainerAsync(originalContainerId);

                await _containerService.RemoveContainerAsync(originalContainerId);
                originalRemoved = true;
                RecreateStatus = "변경된 설정으로 컨테이너를 생성하는 중...";

                replacementId = await _containerService.RecreateContainerFromInspectAsync(
                    inspect.Name,
                    inspect,
                    [],
                    wasRunning,
                    recreatedMounts,
                    environment,
                    portBindings,
                    exposedPorts);

                _node.ContainerId = replacementId;
                HasPendingConfigurationChanges = false;
                HasPendingStorageChanges = false;
                await _node.RefreshDetailsAsync();
                HasPendingConfigurationChanges = false;
                HasPendingStorageChanges = false;
                RecreateStatus = "재생성이 완료되었습니다.";
                _node.NotifyModified();
                _dialogService.ShowInfo(
                    "변경사항을 적용해 컨테이너를 재생성했습니다.\n" +
                    "기존 writable layer는 dockerdiagram-recreate-snapshot 이미지에 보존되어 있습니다.",
                    "재생성 완료");
            }
            catch (Exception ex)
            {
                ContainerRecoveryResult recovery = await ContainerReplacementRecovery.RecoverAsync(
                    _node,
                    _containerService,
                    originalContainerId,
                    inspect,
                    originalRemoved,
                    wasRunning,
                    replacementId);
                RecreateStatus = "재생성에 실패했습니다.";

                if (!originalRemoved && !string.IsNullOrWhiteSpace(snapshotImageId) &&
                    _containerService is IImageService imageService)
                {
                    try
                    {
                        await imageService.DeleteImageAsync(snapshotImageId);
                    }
                    catch (Exception cleanupException)
                    {
                        AppLog.Warning("사용되지 않은 재생성 snapshot 정리 실패", cleanupException);
                    }
                }

                _dialogService.ShowError(
                    $"컨테이너 재생성 실패: {ex.GetBaseException().Message}{recovery.UserMessage}",
                    "재생성 실패");
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
                RaiseRecreateCommandStates();
            }
        }

        private bool ValidateDrafts(out string error)
        {
            var envKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in EnvironmentItems)
            {
                string key = item.Key.Trim();
                if (!IsValidEnvironmentKey(key))
                {
                    error = $"환경 변수 이름이 올바르지 않습니다: {item.Key}";
                    return false;
                }
                if (!envKeys.Add(key))
                {
                    error = $"중복된 환경 변수입니다: {key}";
                    return false;
                }
            }

            var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in PortItems)
            {
                if (!int.TryParse(item.ContainerPort, out int containerPort) || containerPort is < 1 or > 65535)
                {
                    error = $"컨테이너 포트가 올바르지 않습니다: {item.ContainerPort}";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(item.HostPort) &&
                    (!int.TryParse(item.HostPort, out int hostPort) || hostPort is < 1 or > 65535))
                {
                    error = $"호스트 포트가 올바르지 않습니다: {item.HostPort}";
                    return false;
                }
                string protocol = item.Protocol.Trim().ToLowerInvariant();
                if (protocol is not ("tcp" or "udp" or "sctp"))
                {
                    error = $"프로토콜은 tcp, udp 또는 sctp여야 합니다: {item.Protocol}";
                    return false;
                }
                string identity = $"{item.HostIp.Trim()}:{item.HostPort.Trim()}->{containerPort}/{protocol}";
                if (!ports.Add(identity))
                {
                    error = $"중복된 포트 설정입니다: {identity}";
                    return false;
                }
            }

            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mount in NamedVolumeItems.Concat(BindMountItems).Concat(TmpfsMountItems))
            {
                if ((mount.Kind != MountKind.Tmpfs && string.IsNullOrWhiteSpace(mount.Source)) ||
                    string.IsNullOrWhiteSpace(mount.Target))
                {
                    error = mount.Kind == MountKind.Tmpfs
                        ? "Tmpfs의 Container path를 입력해야 합니다."
                        : "마운트의 Source와 Container path를 모두 입력해야 합니다.";
                    return false;
                }
                if (!ContainerMountParser.IsAbsoluteContainerPath(mount.Target))
                {
                    error = $"컨테이너 마운트 경로는 절대 경로여야 합니다: {mount.Target}";
                    return false;
                }
                if (mount.Kind == MountKind.Bind &&
                    !ContainerMountParser.IsAbsoluteHostPath(mount.Source))
                {
                    error = $"Bind Mount 원본은 절대 Docker 호스트 경로여야 합니다: {mount.Source}";
                    return false;
                }
                if (mount.Mode.Trim().ToLowerInvariant() is not ("ro" or "rw"))
                {
                    error = $"마운트 모드는 ro 또는 rw여야 합니다: {mount.Mode}";
                    return false;
                }
                if (!targets.Add(mount.Target.Trim()))
                {
                    error = $"중복된 컨테이너 마운트 경로입니다: {mount.Target}";
                    return false;
                }

                if (mount.Kind == MountKind.Bind && string.IsNullOrWhiteSpace(mount.BindPropagation))
                {
                    error = "Bind propagation 값을 입력해야 합니다.";
                    return false;
                }
                if (mount.Kind == MountKind.Bind &&
                    mount.BindPropagation.Trim().ToLowerInvariant() is not ("private" or "rprivate" or "shared" or "rshared" or "slave" or "rslave"))
                {
                    error = $"Bind propagation 값이 올바르지 않습니다: {mount.BindPropagation}";
                    return false;
                }
                if (mount.Kind == MountKind.Tmpfs)
                {
                    if (mount.TmpfsSizeBytes < 0)
                    {
                        error = "Tmpfs 크기는 0 이상이어야 합니다.";
                        return false;
                    }
                    try
                    {
                        _ = ContainerMountParser.ParseOctalMode(mount.TmpfsMode);
                    }
                    catch (FormatException ex)
                    {
                        error = ex.Message;
                        return false;
                    }
                }
            }

            error = string.Empty;
            return true;
        }

        private void BuildPortDrafts(
            out IDictionary<string, IList<PortBinding>> portBindings,
            out IDictionary<string, EmptyStruct> exposedPorts)
        {
            var bindings = new Dictionary<string, IList<PortBinding>>(StringComparer.OrdinalIgnoreCase);
            var exposed = new Dictionary<string, EmptyStruct>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in PortItems)
            {
                string key = $"{item.ContainerPort.Trim()}/{item.Protocol.Trim().ToLowerInvariant()}";
                exposed[key] = default;
                if (string.IsNullOrWhiteSpace(item.HostPort)) continue;
                if (!bindings.TryGetValue(key, out var values))
                {
                    values = new List<PortBinding>();
                    bindings[key] = values;
                }
                values.Add(new PortBinding
                {
                    HostIP = string.IsNullOrWhiteSpace(item.HostIp) ? "0.0.0.0" : item.HostIp.Trim(),
                    HostPort = item.HostPort.Trim()
                });
            }

            portBindings = bindings;
            exposedPorts = exposed;
        }

        private List<Mount> BuildRecreatedMounts(ContainerInspectResponse inspect)
        {
            var originalMounts = inspect.HostConfig?.Mounts?.ToList() ?? [];
            var result = originalMounts
                .Where(mount => !IsEditableMountType(mount.Type))
                .Select(CloneMount)
                .ToList();

            AddDraftMounts(result, originalMounts, NamedVolumeItems, MountKind.Volume);
            AddDraftMounts(result, originalMounts, BindMountItems, MountKind.Bind);
            AddDraftMounts(result, originalMounts, TmpfsMountItems, MountKind.Tmpfs);
            return result;
        }

        private static void AddDraftMounts(
            List<Mount> destination,
            IEnumerable<Mount> originals,
            IEnumerable<MountDisplayItem> drafts,
            MountKind kind)
        {
            string type = kind switch
            {
                MountKind.Bind => "bind",
                MountKind.Tmpfs => "tmpfs",
                _ => "volume"
            };
            foreach (var draft in drafts)
            {
                string source = draft.Source.Trim();
                string target = draft.Target.Trim();
                Mount? original = originals.FirstOrDefault(mount =>
                    string.Equals(mount.Type, type, StringComparison.OrdinalIgnoreCase) &&
                    (kind == MountKind.Tmpfs || string.Equals(mount.Source, source, StringComparison.Ordinal)) &&
                    string.Equals(mount.Target, target, StringComparison.Ordinal));
                Mount recreated = original == null ? new Mount() : CloneMount(original);
                recreated.Type = type;
                recreated.Source = kind == MountKind.Tmpfs ? string.Empty : source;
                recreated.Target = target;
                recreated.ReadOnly = string.Equals(draft.Mode.Trim(), "ro", StringComparison.OrdinalIgnoreCase);
                if (kind == MountKind.Bind)
                {
                    recreated.BindOptions = new BindOptions { Propagation = draft.BindPropagation.Trim() };
                    recreated.VolumeOptions = null;
                    recreated.TmpfsOptions = null;
                }
                else if (kind == MountKind.Volume)
                {
                    recreated.VolumeOptions = new VolumeOptions { NoCopy = draft.VolumeNoCopy };
                    recreated.BindOptions = null;
                    recreated.TmpfsOptions = null;
                }
                else
                {
                    recreated.TmpfsOptions = new TmpfsOptions
                    {
                        SizeBytes = draft.TmpfsSizeBytes,
                        Mode = ContainerMountParser.ParseOctalMode(draft.TmpfsMode)
                    };
                    recreated.BindOptions = null;
                    recreated.VolumeOptions = null;
                }
                destination.Add(recreated);
            }
        }

        private static bool IsEditableMountType(string? type) =>
            string.Equals(type, "volume", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "bind", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "tmpfs", StringComparison.OrdinalIgnoreCase);

        private static Mount CloneMount(Mount source) =>
            JsonConvert.DeserializeObject<Mount>(JsonConvert.SerializeObject(source))
            ?? throw new InvalidOperationException("마운트 설정을 복제하지 못했습니다.");

        private static bool IsValidEnvironmentKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
                return false;
            return value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
        }

        private static string MakeTagPart(string value)
        {
            string safe = new(value.ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-'
                    ? character
                    : '-')
                .ToArray());
            safe = safe.Trim('-');
            return string.IsNullOrWhiteSpace(safe) ? "container" : safe;
        }
    }
}
