using DockerDiagram.Contracts;
using DockerDiagram.Common;
using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 볼륨 메타데이터, 사용 현황, 백업·복원 및 재생성 작업을 관리합니다.
    /// </summary>
    public sealed class VolumeNodeViewModel : ViewModelBase
    {
        private const int SidebarUsageLimit = 3;
        private readonly NodeViewModel _node;
        private readonly IVolumeService _volumeService;
        private readonly IDialogService _dialogService;
        private string _dockerVolumeName = string.Empty;
        private bool _external;
        private Dictionary<string, string> _labels = new();
        private Dictionary<string, string> _driverOptions = new();
        private string _labelsText = "None";
        private string _driverOptionsText = "None";
        private string _sizeText = "Unknown";
        private long _refCount;
        private int _usedContainerCount;

        public VolumeNodeViewModel(
            NodeViewModel node,
            IVolumeService volumeService,
            IDialogService dialogService)
        {
            _node = node;
            _volumeService = volumeService;
            _dialogService = dialogService;

            BackupCommand = new AsyncRelayCommand(ExecuteBackupAsync, _ => _node.Type == NodeType.Volume);
            RestoreCommand = new AsyncRelayCommand(ExecuteRestoreAsync, _ => _node.Type == NodeType.Volume);
            RecreateCommand = new AsyncRelayCommand(ExecuteRecreateAsync, _ => _node.Type == NodeType.Volume);
            OpenDetailCommand = new AsyncRelayCommand(OpenDetailAsync, _ => _node.Type == NodeType.Volume && _node.IsDockerConnected);
        }

        public ICommand BackupCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand RecreateCommand { get; }
        public ICommand OpenDetailCommand { get; }

        public string DockerVolumeName
        {
            get => _dockerVolumeName;
            set
            {
                if (SetProperty(ref _dockerVolumeName, value))
                    OnPropertyChanged(nameof(EffectiveVolumeName));
            }
        }

        public bool External
        {
            get => _external;
            set
            {
                if (SetProperty(ref _external, value))
                    OnPropertyChanged(nameof(ExternalText));
            }
        }

        public string ExternalText => External ? "Yes" : "No";

        public Dictionary<string, string> Labels
        {
            get => _labels;
            set
            {
                if (SetProperty(ref _labels, value ?? new Dictionary<string, string>()))
                    LabelsText = FormatKeyValueMap(_labels);
            }
        }

        public Dictionary<string, string> DriverOptions
        {
            get => _driverOptions;
            set
            {
                if (SetProperty(ref _driverOptions, value ?? new Dictionary<string, string>()))
                    DriverOptionsText = FormatKeyValueMap(_driverOptions);
            }
        }

        public string LabelsText
        {
            get => _labelsText;
            private set => SetProperty(ref _labelsText, value);
        }

        public string DriverOptionsText
        {
            get => _driverOptionsText;
            private set => SetProperty(ref _driverOptionsText, value);
        }

        public string SizeText
        {
            get => _sizeText;
            private set
            {
                if (SetProperty(ref _sizeText, value))
                    OnPropertyChanged(nameof(DisplaySizeText));
            }
        }

        public string DisplaySizeText =>
            string.Equals(SizeText, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? "Not available"
                : SizeText;

        public long RefCount
        {
            get => _refCount;
            private set => SetProperty(ref _refCount, value);
        }

        public int UsedContainerCount
        {
            get => _usedContainerCount;
            private set
            {
                if (!SetProperty(ref _usedContainerCount, value)) return;

                OnPropertyChanged(nameof(UsedContainerCountText));
                OnPropertyChanged(nameof(HasVolumeUsage));
                OnPropertyChanged(nameof(HasSidebarUsageOverflow));
                OnPropertyChanged(nameof(SidebarUsageOverflowText));
            }
        }

        public string UsedContainerCountText =>
            $"{UsedContainerCount} container{(UsedContainerCount == 1 ? string.Empty : "s")}";

        public bool HasVolumeUsage => UsedContainerCount > 0;
        public bool HasSidebarUsageOverflow => UsedContainerCount > SidebarUsageLimit;
        public string SidebarUsageOverflowText => $"+{Math.Max(0, UsedContainerCount - SidebarUsageLimit)} more";

        public string EffectiveVolumeName =>
            string.IsNullOrWhiteSpace(DockerVolumeName) ? _node.Name : DockerVolumeName;

        public ObservableCollection<string> UsedByContainers { get; } = new();
        public ObservableCollection<VolumeUsageInfo> UsageDetails { get; } = new();
        public IReadOnlyList<VolumeUsageInfo> SidebarUsageDetails => UsageDetails
            .GroupBy(usage => usage.ContainerName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(SidebarUsageLimit)
            .ToList();

        private async Task OpenDetailAsync(object? parameter)
        {
            await _node.RefreshDetailsAsync();

            if (_node.IsDockerConnected)
                _dialogService.ShowVolumeDetail(_node);
        }

        public async Task RefreshDetailsAsync()
        {
            var volumeName = EffectiveVolumeName;
            var volume = await _volumeService.InspectVolumeAsync(volumeName);

            _node.IsDockerConnected = true;
            _node.DetailStatus = "Created";
            _node.Driver = volume.Driver;
            _node.Mountpoint = volume.Mountpoint;
            Labels = volume.Labels?.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty)
                     ?? new Dictionary<string, string>();
            DriverOptions = volume.Options?.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty)
                            ?? new Dictionary<string, string>();
            _node.CreatedDate = DateTime.TryParse(volume.CreatedAt, out var created)
                ? created.ToString("yyyy-MM-dd HH:mm:ss")
                : volume.CreatedAt;

            List<VolumeUsageInfo> usageDetails;
            try
            {
                usageDetails = await _volumeService.GetVolumeUsageDetailsAsync(volumeName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeDetails] Usage lookup failed for '{volumeName}': {ex.Message}");
                usageDetails = new List<VolumeUsageInfo>();
            }

            var users = usageDetails
                .Select(usage => usage.ContainerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            UsedByContainers.Clear();
            UsageDetails.Clear();

            if (users.Count == 0)
                UsedByContainers.Add("None");
            else
                foreach (var user in users) UsedByContainers.Add(user);

            foreach (var usage in usageDetails)
                UsageDetails.Add(usage);

            UsedContainerCount = users.Count;
            OnPropertyChanged(nameof(SidebarUsageDetails));

            if (_volumeService is ISystemService systemService)
            {
                try
                {
                    var diskUsage = await systemService.GetSystemDiskUsageAsync();
                    var volumeUsage = diskUsage.Volumes.FirstOrDefault(item =>
                        string.Equals(item.Name, volumeName, StringComparison.OrdinalIgnoreCase));
                    SizeText = volumeUsage?.FormattedSize ?? "Unknown";
                    RefCount = volumeUsage?.RefCount ?? usageDetails.Count;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VolumeDetails] Disk usage lookup failed for '{volumeName}': {ex.Message}");
                    SizeText = "Unknown";
                    RefCount = usageDetails.Count;
                }
            }
            else
            {
                SizeText = "Unknown";
                RefCount = usageDetails.Count;
            }

            _node.IsRunning = false;
            _node.IsPaused = false;
            _node.StatusColor = "#E67E22";
        }

        public async Task<bool> ReconnectAsync()
        {
            var volumes = await _volumeService.GetVolumesAsync();
            var match = volumes.FirstOrDefault(volume =>
                            string.Equals(
                                volume.Name,
                                EffectiveVolumeName,
                                StringComparison.OrdinalIgnoreCase))
                        ?? volumes.FirstOrDefault(volume =>
                            string.Equals(volume.Name, _node.Name, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                _dialogService.ShowInfo(
                    $"Docker에서 '{EffectiveVolumeName}' 볼륨을 찾지 못했습니다.",
                    "Reconnect");
                _node.IsDockerConnected = false;
                return false;
            }

            DockerVolumeName = match.Name;
            _node.IsDockerConnected = true;
            await _node.RefreshDetailsAsync();
            return true;
        }

        private async Task ExecuteBackupAsync(object? parameter)
        {
            var backupPath = _dialogService.ShowSaveFileDialog(
                "Tar Archive (*.tar)|*.tar",
                ".tar",
                $"{EffectiveVolumeName}_backup_{DateTime.Now:yyyyMMdd_HHmmss}.tar",
                $"[{EffectiveVolumeName}] 볼륨 백업 저장");
            if (string.IsNullOrWhiteSpace(backupPath)) return;

            _node.DetailStatus = "Backing up...";
            _node.StatusColor = "#007ACC";

            try
            {
                await _volumeService.BackupVolumeAsync(EffectiveVolumeName, backupPath);
                _dialogService.ShowInfo($"볼륨 백업이 완료되었습니다.\n저장 위치: {backupPath}", "백업 성공");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"백업 중 오류가 발생했습니다.\n{ex.Message}", "백업 실패");
            }
            finally
            {
                await _node.RefreshDetailsAsync();
            }
        }

        private async Task ExecuteRestoreAsync(object? parameter)
        {
            bool confirm = _dialogService.ShowConfirm(
                $"[{EffectiveVolumeName}] 볼륨에 데이터를 복원하시겠습니까?\n기존 데이터가 덮어씌워질 수 있습니다.",
                "복원 경고");
            if (!confirm) return;

            var backupPath = _dialogService.ShowOpenFileDialog(
                "Tar Archive (*.tar)|*.tar|All Files (*.*)|*.*",
                "복원할 백업 파일(.tar) 선택");
            if (string.IsNullOrWhiteSpace(backupPath)) return;

            _node.DetailStatus = "Restoring...";
            _node.StatusColor = "#E67E22";

            try
            {
                await _volumeService.RestoreVolumeAsync(EffectiveVolumeName, backupPath);
                _dialogService.ShowInfo("볼륨 데이터가 성공적으로 복원되었습니다.", "복원 성공");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"복원 중 오류가 발생했습니다.\n{ex.Message}", "복원 실패");
            }
            finally
            {
                await _node.RefreshDetailsAsync();
            }
        }

        private async Task ExecuteRecreateAsync(object? parameter)
        {
            var currentOptions = new VolumeCreateOptions
            {
                Name = _node.Name,
                DockerVolumeName = DockerVolumeName,
                Driver = string.IsNullOrWhiteSpace(_node.Driver) || _node.Driver == "-" ? "local" : _node.Driver,
                External = External,
                Labels = new Dictionary<string, string>(Labels),
                DriverOptions = new Dictionary<string, string>(DriverOptions)
            };

            if (!_dialogService.TryShowVolumeOptionsDialog(currentOptions, out var newOptions)) return;

            string oldVolumeName = EffectiveVolumeName;
            string newVolumeName = newOptions.EffectiveDockerVolumeName;

            try
            {
                _dialogService.SetBusyCursor(true);

                if (newOptions.External)
                {
                    var existing = await _volumeService.InspectVolumeAsync(newVolumeName);
                    ApplyOptions(newOptions, newVolumeName, external: true);
                    _node.Driver = string.IsNullOrWhiteSpace(existing.Driver) ? newOptions.Driver : existing.Driver;
                    _node.ImageName = _node.Driver;
                    await _node.RefreshDetailsAsync();
                    return;
                }

                if (External)
                {
                    _dialogService.ShowError(
                        $"외부 볼륨 '{oldVolumeName}'은(는) 앱이 소유한 볼륨이 아니므로 삭제/재생성하지 않습니다.\n" +
                        "관리형 볼륨으로 바꾸려면 새 볼륨을 별도로 생성한 뒤 데이터를 직접 이전해 주세요.",
                        "Volume Recreate");
                    return;
                }

                var users = await _volumeService.GetContainersUsingVolumeAsync(oldVolumeName);
                if (users.Count > 0)
                {
                    _dialogService.ShowError(
                        $"볼륨 '{oldVolumeName}'은(는) 현재 컨테이너에서 사용 중이라 재생성할 수 없습니다.\n\n" +
                        $"사용 중인 컨테이너:\n{string.Join("\n", users.Select(user => $"- {user}"))}",
                        "Volume Recreate");
                    return;
                }

                if (!_dialogService.ShowConfirm(
                    $"볼륨 '{oldVolumeName}'을(를) 백업한 뒤 새 옵션으로 재생성하시겠습니까?\n\n" +
                    "진행 순서: backup -> remove -> create -> restore",
                    "Volume Recreate"))
                {
                    return;
                }

                string backupPath = Path.Combine(
                    Path.GetTempPath(),
                    $"DockerDiagram_volume_recreate_{Guid.NewGuid():N}.tar");
                bool backupCreated = false;
                bool oldRemoved = false;
                bool newCreated = false;
                bool recreateSucceeded = false;

                try
                {
                    await _volumeService.BackupVolumeAsync(oldVolumeName, backupPath);
                    backupCreated = File.Exists(backupPath);
                    if (!backupCreated)
                        throw new InvalidOperationException("볼륨 백업 파일이 생성되지 않아 재생성을 중단했습니다.");

                    await _volumeService.RemoveVolumeAsync(oldVolumeName, force: false);
                    oldRemoved = true;
                    await _volumeService.CreateVolumeAsync(newOptions);
                    newCreated = true;
                    await _volumeService.RestoreVolumeAsync(newVolumeName, backupPath);
                    recreateSucceeded = true;
                }
                catch (Exception recreateException)
                {
                    if (oldRemoved && backupCreated)
                    {
                        try
                        {
                            if (newCreated)
                                await _volumeService.RemoveVolumeAsync(newVolumeName, force: false);

                            currentOptions.External = false;
                            await _volumeService.CreateVolumeAsync(currentOptions);
                            await _volumeService.RestoreVolumeAsync(oldVolumeName, backupPath);
                            await _node.RefreshDetailsAsync();

                            _dialogService.ShowError(
                                $"볼륨 재생성에 실패해서 원래 볼륨 '{oldVolumeName}'으로 롤백했습니다.\n\n" +
                                $"실패 원인:\n{recreateException.Message}",
                                "Volume Recreate Rollback");
                            return;
                        }
                        catch (Exception rollbackException)
                        {
                            _dialogService.ShowError(
                                $"볼륨 재생성과 롤백이 모두 실패했습니다.\n\n" +
                                $"백업 파일은 삭제하지 않았습니다:\n{backupPath}\n\n" +
                                $"재생성 실패:\n{recreateException.Message}\n\n" +
                                $"롤백 실패:\n{rollbackException.Message}",
                                "Volume Recreate Failed");
                            return;
                        }
                    }

                    _dialogService.ShowError(
                        $"볼륨 재생성 중 오류가 발생했습니다.\n{recreateException.Message}\n\n" +
                        (backupCreated
                            ? $"백업 파일은 삭제하지 않았습니다:\n{backupPath}"
                            : "볼륨 삭제 전 단계에서 실패했습니다."),
                        "Volume Recreate");
                    return;
                }
                finally
                {
                    if (recreateSucceeded && backupCreated && File.Exists(backupPath))
                        File.Delete(backupPath);
                }

                ApplyOptions(newOptions, newVolumeName, external: false);
                await _node.RefreshDetailsAsync();
                _dialogService.ShowInfo($"볼륨 '{newVolumeName}' 재생성이 완료되었습니다.", "Volume Recreate");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"볼륨 재생성 중 오류가 발생했습니다.\n{ex.Message}", "Volume Recreate");
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
            }
        }

        private void ApplyOptions(VolumeCreateOptions options, string dockerVolumeName, bool external)
        {
            _node.Name = options.Name;
            DockerVolumeName = dockerVolumeName;
            External = external;
            _node.Driver = string.IsNullOrWhiteSpace(options.Driver) ? "local" : options.Driver;
            _node.ImageName = _node.Driver;
            Labels = new Dictionary<string, string>(options.Labels);
            DriverOptions = new Dictionary<string, string>(options.DriverOptions);
        }

        private static string FormatKeyValueMap(Dictionary<string, string>? values)
        {
            if (values == null || values.Count == 0) return "None";
            return string.Join("\n", values.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }
}
