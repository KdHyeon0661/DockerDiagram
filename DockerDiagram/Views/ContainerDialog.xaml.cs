using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 새로운 도커 컨테이너를 생성하기 위해 사용자로부터 다양한 설정값을 입력받는 다목적 UI 팝업 창(View)입니다.
    /// 직관적인 UI 폼 설정, CLI 명령어 기반 생성, Dockerfile 직접 빌드 등 3가지 방식의 생성 워크플로우를 지원하며,
    /// 입력된 데이터를 검증한 후 뷰모델(MainViewModel)로 전달하는 역할을 합니다.
    /// </summary>
    public partial class ContainerDialog : Window
    {
        private readonly IDialogService _dialogService;
        private readonly IImageService? _imageService;
        private readonly INetworkService? _networkService;
        private readonly ConnectionProfile? _connectionProfile;
        private readonly ObservableCollection<ContainerMountSpec> _mounts = [];
        private ContainerMountSpec? _editingMount;
        private readonly Dictionary<string, Control> _profileInputs =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _imageLookupTimer;
        private ContainerImageProfile? _activeImageProfile;
        private ContainerImageMetadata? _imageMetadata;
        private CancellationTokenSource? _imageLookupCancellation;
        private string _suggestionsAppliedForImage = string.Empty;
        private readonly HashSet<string> _autoAppliedPorts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _autoAppliedEnvironment = new(StringComparer.Ordinal);
        private readonly HashSet<string> _autoAppliedVolumes = new(StringComparer.OrdinalIgnoreCase);
        private string _autoAppliedCommand = string.Empty;
        private IReadOnlyList<string>? _validatedEnvironmentVariables;

        // =====================================
        // [공통] 현재 사용자가 선택한 생성 방식 (0: UI, 1: CLI, 2: Dockerfile)
        // =====================================

        /// <summary>
        /// 사용자가 3개의 탭(UI 설정, CLI 명령어, Dockerfile 빌드) 중 어떤 방식을 선택했는지 나타냅니다.
        /// </summary>
        public int SelectedCreationMode => MainTabControl.SelectedIndex;

        // =====================================
        // [탭 1: 직접 설정 (UI)] 데이터
        // =====================================
        public string ContainerName => txtName.Text.Trim();
        public string ImageName
        {
            get => txtImage.Text.Trim();
            set => txtImage.Text = value;
        }

        public string SelectedNetwork => cmbNetwork.Text.Trim();
        public bool IsInteractive => chkInteractive.IsChecked == true;
        public string Command => txtCommand.Text.Trim();

        public string RegServer => txtRegServer.Text.Trim();
        public string RegUser => txtRegUser.Text.Trim();
        public string RegPass => txtRegPass.Password.Trim();

        public List<string> Ports
        {
            get => ReadNonEmptyLines(txtPorts.Text);
        }

        public List<string> EnvVars
        {
            get => (_validatedEnvironmentVariables ?? ReadNonEmptyLines(txtEnv.Text)).ToList();
        }

        public string RestartPolicy => (cmbRestart.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "no";
        public long MemoryMb => long.TryParse(txtMem.Text, out var v) ? v : 0;
        public double CpuCount => double.TryParse(txtCpu.Text, out var v) ? v : 0;
        public List<string> Volumes
            => _mounts.Where(mount => mount.Kind != MountKind.Tmpfs).Select(mount => mount.ToLegacyString()).ToList();
        public List<ContainerMountSpec> Mounts => _mounts.Select(mount => mount.Clone()).ToList();

        // =====================================
        // [탭 2: 명령어로 생성 (CLI)] 데이터
        // =====================================
        public string CliCommand => txtCliCommand.Text.Trim();

        // =====================================
        // [탭 3: 도커파일 (Build)] 데이터
        // =====================================
        public string DockerfileContent => txtDockerfile.Text.Trim();
        public string BuildImageTag => txtBuildImageTag.Text.Trim();
        public string UploadedDockerfilePath { get; set; } = "";

        /// <summary>
        /// 컨테이너 생성 대화상자를 초기화하고 내부에서 사용할 다이얼로그 서비스를 연결합니다.
        /// </summary>
        public ContainerDialog(
            IDialogService dialogService,
            IDockerService? dockerService = null,
            ConnectionProfile? connectionProfile = null)
        {
            InitializeComponent();
            _dialogService = dialogService;
            _imageService = dockerService;
            _networkService = dockerService;
            _connectionProfile = connectionProfile;
            lstVolumes.ItemsSource = _mounts;
            _imageLookupTimer = new DispatcherTimer
            {
                Interval = ContainerCreationTiming.MetadataLookupDebounce
            };
            _imageLookupTimer.Tick += ImageLookupTimer_Tick;
            Closed += (_, _) =>
            {
                _imageLookupTimer.Stop();
                _imageLookupCancellation?.Cancel();
                _imageLookupCancellation?.Dispose();
            };
            Loaded += ContainerDialog_Loaded;
            UpdateImageProfile();
            UpdateMountEditor();
            txtName.Focus();
        }

        private async void ContainerDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ContainerDialog_Loaded;
            await RefreshNetworkChoicesAsync();
        }

        private void ImageName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (tabImageSetup == null)
                return;

            if (!string.IsNullOrWhiteSpace(_suggestionsAppliedForImage) &&
                !string.Equals(_suggestionsAppliedForImage, ImageName, StringComparison.OrdinalIgnoreCase))
            {
                RemoveAutoAppliedSuggestions();
            }

            UpdateImageProfile();
            ClearImageMetadata();
            _imageLookupTimer.Stop();

            if (_imageService != null && IsLookupCandidate(ImageName))
                _imageLookupTimer.Start();
        }

        private void ContainerName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_autoAppliedVolumes.Count == 0) return;

            string requestedName = string.IsNullOrWhiteSpace(ContainerName)
                ? _activeImageProfile?.Id ?? "container"
                : ContainerName;
            string containerName = SanitizeDockerName(requestedName);
            var updatedMappings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string previousMapping in _autoAppliedVolumes.ToList())
            {
                int itemIndex = _mounts
                    .Select((mount, index) => (mount, index))
                    .FirstOrDefault(entry => string.Equals(
                        entry.mount.ToLegacyString(),
                        previousMapping,
                        StringComparison.OrdinalIgnoreCase)).index;
                bool found = itemIndex >= 0 && itemIndex < _mounts.Count &&
                             string.Equals(_mounts[itemIndex].ToLegacyString(), previousMapping, StringComparison.OrdinalIgnoreCase);

                // 사용자가 자동 생성된 매핑을 직접 수정하거나 삭제했다면 더 이상 추적하지 않습니다.
                if (!found) continue;

                string target = GetVolumeTarget(previousMapping);
                if (string.IsNullOrWhiteSpace(target))
                {
                    updatedMappings.Add(previousMapping);
                    continue;
                }

                string suffix = _activeImageProfile?.Volumes
                    .FirstOrDefault(volume => string.Equals(
                        volume.ContainerPath,
                        target,
                        StringComparison.Ordinal))
                    ?.NameSuffix ?? GetVolumeNameSuffix(target, 1);
                string updatedMapping = $"{containerName}-{suffix}:{target}";
                ContainerMountSpec updated = _mounts[itemIndex].Clone();
                updated.Source = $"{containerName}-{suffix}";
                _mounts[itemIndex] = updated;
                updatedMappings.Add(updatedMapping);
            }

            _autoAppliedVolumes.Clear();
            foreach (string mapping in updatedMappings)
                _autoAppliedVolumes.Add(mapping);
        }

        private async void ImageLookupTimer_Tick(object? sender, EventArgs e)
        {
            _imageLookupTimer.Stop();
            await InspectImageAsync();
        }

        private async void InspectImage_Click(object sender, RoutedEventArgs e)
        {
            _imageLookupTimer.Stop();
            await InspectImageAsync();
        }

        private async void RefreshNetworks_Click(object sender, RoutedEventArgs e)
        {
            await RefreshNetworkChoicesAsync();
        }

        private async void CreateNetwork_Click(object sender, RoutedEventArgs e)
        {
            if (_networkService == null)
            {
                _dialogService.ShowError(
                    "현재 Docker 연결에서는 네트워크를 생성할 수 없습니다.",
                    "네트워크 생성");
                return;
            }

            var dialog = new NetworkDialog(_dialogService)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true) return;

            NetworkCreateOptions options = dialog.CreateOptions;
            string dockerNetworkName = options.External &&
                                       !string.IsNullOrWhiteSpace(options.ComposeNetworkName)
                ? options.ComposeNetworkName
                : options.Name;

            btnCreateNetwork.IsEnabled = false;
            txtNetworkStatus.Text = $"'{dockerNetworkName}' 네트워크를 확인하는 중입니다.";

            try
            {
                if (options.External)
                {
                    var existingNetworks = await _networkService.GetNetworksAsync();
                    bool exists = existingNetworks.Any(network => string.Equals(
                        string.IsNullOrWhiteSpace(network.Name) ? network.Id : network.Name,
                        dockerNetworkName,
                        StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                    {
                        _dialogService.ShowError(
                            $"External 네트워크 '{dockerNetworkName}'을(를) Docker에서 찾을 수 없습니다.",
                            "네트워크 생성");
                        return;
                    }
                }
                else
                {
                    await _networkService.CreateNetworkAsync(options);
                }

                await RefreshNetworkChoicesAsync(dockerNetworkName);
                txtNetworkStatus.Text = options.External
                    ? $"기존 네트워크 '{dockerNetworkName}'을(를) 선택했습니다."
                    : $"네트워크 '{dockerNetworkName}'을(를) 생성하고 선택했습니다.";
            }
            catch (Exception ex)
            {
                txtNetworkStatus.Text = "네트워크를 생성하지 못했습니다.";
                _dialogService.ShowError(
                    $"네트워크 '{dockerNetworkName}' 생성 실패:\n{ex.GetBaseException().Message}",
                    "네트워크 생성");
            }
            finally
            {
                btnCreateNetwork.IsEnabled = _networkService != null;
            }
        }

        private async Task RefreshNetworkChoicesAsync(string? networkToSelect = null)
        {
            string requestedNetwork = string.IsNullOrWhiteSpace(networkToSelect)
                ? cmbNetwork.Text.Trim()
                : networkToSelect.Trim();

            if (_networkService == null)
            {
                cmbNetwork.Items.Clear();
                cmbNetwork.Items.Add(DockerNetworkSafety.Bridge);
                cmbNetwork.Items.Add(DockerNetworkSafety.Host);
                cmbNetwork.Items.Add(DockerNetworkSafety.None);
                cmbNetwork.Text = string.IsNullOrWhiteSpace(requestedNetwork) ? DockerNetworkSafety.Bridge : requestedNetwork;
                btnRefreshNetworks.IsEnabled = false;
                btnCreateNetwork.IsEnabled = false;
                txtNetworkStatus.Text = "현재 Docker 연결에서는 네트워크 조회와 생성을 사용할 수 없습니다.";
                return;
            }

            btnRefreshNetworks.IsEnabled = false;
            btnCreateNetwork.IsEnabled = false;
            txtNetworkStatus.Text = "Docker 네트워크 목록을 불러오는 중입니다.";

            try
            {
                var networks = await _networkService.GetNetworksAsync();
                var names = networks
                    .Select(network => string.IsNullOrWhiteSpace(network.Name) ? network.Id : network.Name)
                    .Where(DockerNetworkSafety.IsUserSelectable)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => string.Equals(name, DockerNetworkSafety.Bridge, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!names.Contains(DockerNetworkSafety.Bridge, StringComparer.OrdinalIgnoreCase))
                    names.Insert(0, DockerNetworkSafety.Bridge);
                names.Add(DockerNetworkSafety.Host);
                names.Add(DockerNetworkSafety.None);

                cmbNetwork.Items.Clear();
                foreach (string name in names)
                    cmbNetwork.Items.Add(name);

                cmbNetwork.Text = string.IsNullOrWhiteSpace(requestedNetwork)
                    ? DockerNetworkSafety.Bridge
                    : requestedNetwork;
                int selectableNetworkCount = names.Count(DockerNetworkSafety.IsUserSelectable);
                txtNetworkStatus.Text = $"연결 가능한 Docker 네트워크 {selectableNetworkCount}개를 불러왔습니다.";
            }
            catch (Exception ex)
            {
                if (cmbNetwork.Items.Count == 0)
                {
                    cmbNetwork.Items.Add(DockerNetworkSafety.Bridge);
                    cmbNetwork.Items.Add(DockerNetworkSafety.Host);
                    cmbNetwork.Items.Add(DockerNetworkSafety.None);
                }
                cmbNetwork.Text = string.IsNullOrWhiteSpace(requestedNetwork) ? DockerNetworkSafety.Bridge : requestedNetwork;
                txtNetworkStatus.Text = $"네트워크 목록을 불러오지 못했습니다. ({GetShortError(ex)})";
            }
            finally
            {
                btnRefreshNetworks.IsEnabled = true;
                btnCreateNetwork.IsEnabled = true;
            }
        }

        private async Task InspectImageAsync()
        {
            if (_imageService == null)
            {
                txtImageLookupStatus.Text = "현재 Docker 연결에서는 이미지 조회를 사용할 수 없습니다.";
                return;
            }

            string imageReference = ImageName;
            if (!IsLookupCandidate(imageReference))
            {
                txtImageLookupStatus.Text = "조회할 이미지 이름을 입력하세요.";
                return;
            }

            _imageLookupCancellation?.Cancel();
            _imageLookupCancellation?.Dispose();
            var lookupCancellation = new CancellationTokenSource();
            _imageLookupCancellation = lookupCancellation;
            CancellationToken cancellationToken = lookupCancellation.Token;

            btnInspectImage.IsEnabled = false;
            txtImageLookupStatus.Text = $"'{imageReference}' 설정을 조회하는 중...";

            try
            {
                ContainerImageMetadata? metadata = await _imageService.GetImageMetadataAsync(
                    imageReference,
                    RegUser,
                    RegPass,
                    RegServer,
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested ||
                    !ImageName.Equals(imageReference, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _imageMetadata = metadata;
                if (metadata == null)
                {
                    txtImageLookupStatus.Text = "표준 이미지 설정을 찾지 못했습니다. 기존 탭에서 직접 설정할 수 있습니다.";
                }
                else
                {
                    txtImageLookupStatus.Text =
                        $"{metadata.Source}에서 포트 {metadata.ExposedPorts.Count}개, " +
                        $"볼륨 {metadata.Volumes.Count}개를 찾았습니다.";
                }

                RefreshSetupPresentation();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _imageMetadata = null;
                    txtImageLookupStatus.Text =
                        $"자동 조회하지 못했습니다. 직접 설정은 계속 사용할 수 있습니다. ({GetShortError(ex)})";
                    RefreshSetupPresentation();
                }
            }
            finally
            {
                if (ReferenceEquals(_imageLookupCancellation, lookupCancellation))
                    btnInspectImage.IsEnabled = true;
            }
        }

        private void UpdateImageProfile()
        {
            var profile = ContainerImageProfileCatalog.Find(txtImage.Text);
            if (string.Equals(
                    profile?.Id,
                    _activeImageProfile?.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _activeImageProfile = profile;
            _profileInputs.Clear();
            pnlProfileFields.Children.Clear();

            bool hasProfile = profile != null;
            profileHeader.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
            profileFormScroll.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
            pnlNoImageProfile.Visibility = hasProfile ? Visibility.Collapsed : Visibility.Visible;
            tabImageSetup.Header = "초기 설정";

            if (!hasProfile)
            {
                RefreshSetupPresentation();
                return;
            }

            txtProfileCategory.Text = string.IsNullOrWhiteSpace(profile!.Category)
                ? "컨테이너 프로필"
                : profile.Category;
            txtProfileName.Text = $"{profile.DisplayName} initial configuration";
            txtProfileDescription.Text = profile.Description;
            txtProfileNotes.Text = profile.Notes.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, profile.Notes.Select(note => "- " + note));
            chkApplyProfilePorts.Visibility = profile.Fields.Any(field =>
                !string.IsNullOrWhiteSpace(field.ContainerPort))
                ? Visibility.Visible
                : Visibility.Collapsed;
            chkApplyProfileVolumes.Visibility = profile.Volumes.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            txtProfileCommandHint.Text = string.IsNullOrWhiteSpace(profile.CommandTemplate)
                ? string.Empty
                : "Command 탭을 비워 두면 이 이미지의 권장 시작 명령이 자동 적용됩니다.";

            foreach (var field in profile.Fields)
                AddProfileField(field);

            RefreshSetupPresentation();
        }

        private void RefreshSetupPresentation()
        {
            bool hasProfile = _activeImageProfile != null;
            bool hasMetadata = _imageMetadata != null;
            pnlProfileOptions.Visibility = hasProfile || hasMetadata
                ? Visibility.Visible
                : Visibility.Collapsed;
            metadataSummaryBorder.Visibility = hasMetadata
                ? Visibility.Visible
                : Visibility.Collapsed;
            btnApplyImageSuggestions.IsEnabled = hasProfile || hasMetadata;

            if (!hasMetadata)
            {
                txtMetadataSummary.Text = string.Empty;
                return;
            }

            string ports = _imageMetadata!.ExposedPorts.Count > 0
                ? string.Join(", ", _imageMetadata.ExposedPorts)
                : "none";
            string volumes = _imageMetadata.Volumes.Count > 0
                ? string.Join(", ", _imageMetadata.Volumes)
                : "none";
            string entrypoint = _imageMetadata.Entrypoint.Count > 0
                ? string.Join(" ", _imageMetadata.Entrypoint)
                : "default";
            string command = _imageMetadata.Command.Count > 0
                ? string.Join(" ", _imageMetadata.Command)
                : "default";

            txtMetadataSummary.Text =
                $"Ports: {ports}\nVolumes: {volumes}\n" +
                $"Default environment: {_imageMetadata.Environment.Count} entries (already included in image)\n" +
                $"Entrypoint: {entrypoint}\nCommand: {command}";
        }

        private void ClearImageMetadata()
        {
            _imageLookupCancellation?.Cancel();
            _imageMetadata = null;
            txtImageLookupStatus.Text = string.IsNullOrWhiteSpace(ImageName)
                ? "이미지를 입력하면 포트와 볼륨 설정을 조회합니다."
                : "입력을 마치면 자동으로 조회합니다.";
            RefreshSetupPresentation();
        }

        private void AddProfileField(ContainerImageProfileField field)
        {
            if (field.Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
            {
                var checkBox = new CheckBox
                {
                    Content = field.Label,
                    IsChecked = field.DefaultValue.Equals("true", StringComparison.OrdinalIgnoreCase),
                    Margin = new Thickness(0, 5, 0, 10),
                    Tag = field
                };
                pnlProfileFields.Children.Add(checkBox);
                _profileInputs[field.Key] = checkBox;
                return;
            }

            pnlProfileFields.Children.Add(new TextBlock
            {
                Text = field.Label,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.DarkSlateGray,
                Margin = new Thickness(0, 2, 0, 4)
            });

            Control input;
            if (field.Type.Equals("password", StringComparison.OrdinalIgnoreCase))
            {
                input = new PasswordBox
                {
                    Password = field.DefaultValue,
                    Padding = new Thickness(6),
                    Margin = new Thickness(0, 0, 0, 5),
                    Tag = field
                };
            }
            else
            {
                var textBox = new TextBox
                {
                    Text = field.DefaultValue,
                    Padding = new Thickness(6),
                    Margin = new Thickness(0, 0, 0, 5),
                    Tag = field
                };
                if (field.Type.Equals("port", StringComparison.OrdinalIgnoreCase))
                    textBox.PreviewTextInput += NumberValidationTextBox;
                input = textBox;
            }

            pnlProfileFields.Children.Add(input);
            _profileInputs[field.Key] = input;

            if (!string.IsNullOrWhiteSpace(field.HelpText))
            {
                pnlProfileFields.Children.Add(new TextBlock
                {
                    Text = field.HelpText,
                    FontSize = 10,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 9)
                });
            }
            else
            {
                input.Margin = new Thickness(0, 0, 0, 10);
            }
        }

        /// <summary>
        /// 호스트 PC의 탐색기를 열어 빌드할 로컬 Dockerfile을 선택하고 내용을 불러옵니다.
        /// </summary>
        private void BtnBrowseDockerfile_Click(object sender, RoutedEventArgs e)
        {
            var openDlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Dockerfile|*.*|All Files|*.*",
                Title = "도커파일 선택"
            };

            if (openDlg.ShowDialog() == true)
            {
                UploadedDockerfilePath = openDlg.FileName;
                txtDockerfile.Text = System.IO.File.ReadAllText(openDlg.FileName);
            }
        }

        /// <summary>
        /// 'Create / Run' 버튼 클릭 시 호출되며, 현재 선택된 탭(생성 모드)에 따라 필수 입력값이 잘 들어왔는지 검증합니다.
        /// 검증을 통과하면 창을 닫고 뷰모델에 생성을 지시합니다.
        /// </summary>
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCreationMode == 0) // UI 방식
            {
                if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ImageName))
                {
                    _dialogService.ShowInfo("컨테이너 명과 이미지 명을 입력하세요.", "입력 오류");
                    return;
                }

                if (!ValidateManualEnvironmentVariables())
                    return;
                if (!ValidateManualMounts())
                    return;

            }
            else if (SelectedCreationMode == 1) // CLI 방식
            {
                if (string.IsNullOrWhiteSpace(CliCommand))
                {
                    _dialogService.ShowInfo("명령어를 입력해주세요.", "입력 오류");
                    return;
                }
            }
            else if (SelectedCreationMode == 2) // 도커파일 방식
            {
                if (string.IsNullOrWhiteSpace(BuildImageTag) || string.IsNullOrWhiteSpace(DockerfileContent))
                {
                    _dialogService.ShowInfo("이미지 태그와 Dockerfile 내용을 모두 입력해주세요.", "입력 오류");
                    return;
                }
            }

            DialogResult = true;
        }

        private void ApplyImageSuggestions_Click(object sender, RoutedEventArgs e)
        {
            // 같은 이미지에서 값을 바꿔 다시 적용하거나 다른 이미지로 전환한 경우,
            // 이전에 자동으로 추가했던 값만 먼저 제거합니다. 사용자가 직접 입력하거나
            // 수정한 값은 추적 목록과 일치하지 않으므로 그대로 보존됩니다.
            RemoveAutoAppliedSuggestions();

            var ports = ReadNonEmptyLines(txtPorts.Text);
            var environments = ReadNonEmptyLines(txtEnv.Text);
            var volumes = _mounts
                .Where(mount => mount.Kind != MountKind.Tmpfs)
                .Select(mount => mount.ToLegacyString())
                .ToList();

            if (_activeImageProfile != null)
            {
                foreach (var profileField in _activeImageProfile.Fields)
                {
                    string value = profileField.Type.Equals("bool", StringComparison.OrdinalIgnoreCase)
                        ? (GetProfileBoolValue(profileField.Key)
                            ? profileField.TrueValue
                            : profileField.FalseValue)
                        : GetProfileValue(profileField.Key);

                    if (!string.IsNullOrWhiteSpace(profileField.EnvironmentVariable) &&
                        !string.IsNullOrWhiteSpace(value))
                    {
                        string mapping = $"{profileField.EnvironmentVariable}={value}";
                        if (AddEnvironmentIfMissing(
                                environments,
                                profileField.EnvironmentVariable,
                                value))
                        {
                            _autoAppliedEnvironment.Add(mapping);
                        }
                    }

                    if (chkApplyProfilePorts.IsChecked == true &&
                        !string.IsNullOrWhiteSpace(profileField.ContainerPort) &&
                        int.TryParse(value, out int port) &&
                        port is >= 1 and <= 65535)
                    {
                        string mapping = $"{port}:{profileField.ContainerPort}";
                        if (AddPortIfMissing(ports, mapping))
                            _autoAppliedPorts.Add(mapping);
                    }
                }

                if (chkApplyProfileVolumes.IsChecked == true)
                {
                    string containerName = SanitizeDockerName(
                        string.IsNullOrWhiteSpace(ContainerName)
                            ? _activeImageProfile.Id
                            : ContainerName);
                    foreach (var volume in _activeImageProfile.Volumes)
                    {
                        string mapping = $"{containerName}-{volume.NameSuffix}:{volume.ContainerPath}";
                        if (AddVolumeIfMissing(volumes, mapping))
                            _autoAppliedVolumes.Add(mapping);
                    }
                }

                if (string.IsNullOrWhiteSpace(txtCommand.Text) &&
                    CanResolveProfileCommand())
                {
                    string command = ResolveProfileTemplate(_activeImageProfile.CommandTemplate);
                    txtCommand.Text = command;
                    _autoAppliedCommand = command;
                }
            }

            if (_imageMetadata != null)
            {
                if (chkApplyProfilePorts.IsChecked == true)
                {
                    foreach (string exposedPort in _imageMetadata.ExposedPorts)
                    {
                        string containerPort = exposedPort.Split('/')[0];
                        if (int.TryParse(containerPort, out int port))
                        {
                            string mapping = $"{port}:{exposedPort}";
                            if (AddPortIfMissing(ports, mapping))
                                _autoAppliedPorts.Add(mapping);
                        }
                    }
                }

                if (chkApplyProfileVolumes.IsChecked == true)
                {
                    string containerName = SanitizeDockerName(
                        string.IsNullOrWhiteSpace(ContainerName) ? "container" : ContainerName);
                    int index = 1;
                    foreach (string target in _imageMetadata.Volumes)
                    {
                        string suffix = GetVolumeNameSuffix(target, index++);
                        string mapping = $"{containerName}-{suffix}:{target}";
                        if (AddVolumeIfMissing(volumes, mapping))
                            _autoAppliedVolumes.Add(mapping);
                    }
                }
            }

            txtPorts.Text = string.Join(Environment.NewLine, ports);
            txtEnv.Text = string.Join(Environment.NewLine, environments);
            foreach (string volume in volumes)
            {
                if (_mounts.Any(mount => string.Equals(
                        mount.ToLegacyString(),
                        volume,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                _mounts.Add(ContainerMountParser.ParseLegacy(volume));
            }

            if (_autoAppliedPorts.Count > 0 ||
                _autoAppliedEnvironment.Count > 0 ||
                _autoAppliedVolumes.Count > 0 ||
                !string.IsNullOrWhiteSpace(_autoAppliedCommand))
            {
                _suggestionsAppliedForImage = ImageName;
            }

            txtImageLookupStatus.Text =
                "제안 내용을 설정 탭에 적용했습니다. 생성 전에 자유롭게 수정할 수 있습니다.";
        }

        private void RemoveAutoAppliedSuggestions()
        {
            if (_autoAppliedPorts.Count > 0)
            {
                txtPorts.Text = string.Join(
                    Environment.NewLine,
                    ReadNonEmptyLines(txtPorts.Text).Where(value => !_autoAppliedPorts.Contains(value)));
            }

            if (_autoAppliedEnvironment.Count > 0)
            {
                txtEnv.Text = string.Join(
                    Environment.NewLine,
                    ReadNonEmptyLines(txtEnv.Text).Where(value => !_autoAppliedEnvironment.Contains(value)));
            }

            if (_autoAppliedVolumes.Count > 0)
            {
                foreach (ContainerMountSpec mount in _mounts
                             .Where(item => _autoAppliedVolumes.Contains(item.ToLegacyString()))
                             .ToList())
                {
                    _mounts.Remove(mount);
                }
            }

            if (!string.IsNullOrWhiteSpace(_autoAppliedCommand) &&
                string.Equals(txtCommand.Text.Trim(), _autoAppliedCommand, StringComparison.Ordinal))
            {
                txtCommand.Clear();
            }

            _autoAppliedPorts.Clear();
            _autoAppliedEnvironment.Clear();
            _autoAppliedVolumes.Clear();
            _autoAppliedCommand = string.Empty;
            _suggestionsAppliedForImage = string.Empty;
        }

        private bool ValidateManualEnvironmentVariables()
        {
            FormParseResult<IReadOnlyList<string>> validation = ContainerCreateFormValidator.ParseEnvironmentVariables(
                txtEnv.Text,
                key => EnvironmentVariableNameRegex().IsMatch(key));
            if (validation.IsValid)
            {
                _validatedEnvironmentVariables = validation.Value;
                return true;
            }

            _dialogService.ShowInfo(validation.Message, "환경변수 오류");
            return false;
        }

        private bool ValidateManualMounts()
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (ContainerMountSpec mount in _mounts)
            {
                if (!ContainerMountParser.IsAbsoluteContainerPath(mount.Target))
                {
                    _dialogService.ShowInfo(
                        $"컨테이너 마운트 경로는 절대 경로여야 합니다: {mount.Target}",
                        "마운트 경로 오류");
                    return false;
                }
                if (!targets.Add(mount.Target))
                {
                    _dialogService.ShowInfo($"컨테이너 마운트 경로가 중복되었습니다: {mount.Target}", "마운트 중복");
                    return false;
                }
                if (mount.Kind == MountKind.Bind && string.IsNullOrWhiteSpace(mount.Source))
                {
                    _dialogService.ShowInfo("마운트 원본이 비어 있습니다.", "마운트 입력 오류");
                    return false;
                }
                if (mount.Kind == MountKind.Bind && !ContainerMountParser.IsAbsoluteHostPath(mount.Source))
                {
                    _dialogService.ShowInfo($"Bind Mount 원본은 절대 호스트 경로여야 합니다: {mount.Source}", "마운트 경로 오류");
                    return false;
                }
                if (mount.Kind == MountKind.Bind && !IsValidBindPropagation(mount.BindPropagation))
                {
                    _dialogService.ShowInfo($"Bind propagation 값이 올바르지 않습니다: {mount.BindPropagation}", "마운트 옵션 오류");
                    return false;
                }
            }
            return true;
        }

        private static bool IsValidBindPropagation(string value) =>
            value.Trim().ToLowerInvariant() is "private" or "rprivate" or "shared" or "rshared" or "slave" or "rslave";

        private string GetProfileValue(string key)
        {
            if (!_profileInputs.TryGetValue(key, out Control? input))
                return string.Empty;

            return input switch
            {
                TextBox textBox => textBox.Text.Trim(),
                PasswordBox passwordBox => passwordBox.Password,
                CheckBox checkBox => checkBox.IsChecked == true ? "true" : "false",
                _ => string.Empty
            };
        }

        private bool GetProfileBoolValue(string key) =>
            _profileInputs.TryGetValue(key, out Control? input) &&
            input is CheckBox checkBox &&
            checkBox.IsChecked == true;

        private string ResolveProfileTemplate(string template)
        {
            if (_activeImageProfile == null || string.IsNullOrWhiteSpace(template))
                return string.Empty;

            string result = template;
            foreach (var field in _activeImageProfile.Fields)
            {
                string escapedValue = GetProfileValue(field.Key).Replace("\"", "\\\"");
                result = result.Replace(
                    "${" + field.Key + "}",
                    escapedValue,
                    StringComparison.OrdinalIgnoreCase);
            }
            return result;
        }

        private bool CanResolveProfileCommand()
        {
            if (_activeImageProfile == null ||
                string.IsNullOrWhiteSpace(_activeImageProfile.CommandTemplate))
            {
                return false;
            }

            return _activeImageProfile.Fields
                .Where(profileField => _activeImageProfile.CommandTemplate.Contains(
                    "${" + profileField.Key + "}",
                    StringComparison.OrdinalIgnoreCase))
                .All(profileField => !string.IsNullOrWhiteSpace(GetProfileValue(profileField.Key)));
        }

        private static List<string> ReadNonEmptyLines(string text) =>
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                .ToList();

        private static string GetContainerPort(string mapping)
        {
            string[] parts = mapping.Split(':');
            string port = parts.Length > 1 ? parts[^1] : parts[0];
            int protocolIndex = port.IndexOf('/');
            return protocolIndex >= 0 ? port[..protocolIndex] : port;
        }

        private static string GetVolumeTarget(string mapping)
        {
            if (MyRegex().IsMatch(mapping))
            {
                int separator = mapping.IndexOf(':', 2);
                return separator >= 0 ? mapping[(separator + 1)..].Split(':')[0] : string.Empty;
            }

            string[] parts = mapping.Split(':');
            return parts.Length > 1 ? parts[1] : string.Empty;
        }

        private static string SanitizeDockerName(string value)
        {
            string sanitized = InvalidDockerNameCharacterRegex().Replace(value.Trim(), "-");
            return string.IsNullOrWhiteSpace(sanitized) ? "container" : sanitized;
        }

        private static bool AddEnvironmentIfMissing(
            List<string> environments,
            string key,
            string value)
        {
            bool exists = environments.Any(line =>
                line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (exists) return false;

            environments.Add($"{key}={value}");
            return true;
        }

        private static bool AddPortIfMissing(List<string> ports, string mapping)
        {
            string containerPort = GetContainerPort(mapping);
            bool exists = ports.Any(port =>
                GetContainerPort(port).Equals(containerPort, StringComparison.OrdinalIgnoreCase));
            if (exists) return false;

            ports.Add(mapping);
            return true;
        }

        private static bool AddVolumeIfMissing(List<string> volumes, string mapping)
        {
            string target = GetVolumeTarget(mapping);
            bool exists = volumes.Any(volume =>
                GetVolumeTarget(volume).Equals(target, StringComparison.OrdinalIgnoreCase));
            if (exists) return false;

            volumes.Add(mapping);
            return true;
        }

        private static string GetVolumeNameSuffix(string target, int index)
        {
            string segment = target.TrimEnd('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? $"data-{index}";
            string suffix = InvalidLowerDockerNameCharacterRegex().Replace(segment.ToLowerInvariant(), "-");
            return string.IsNullOrWhiteSpace(suffix) ? $"data-{index}" : suffix;
        }

        private static bool IsLookupCandidate(string imageReference) =>
            imageReference.Length >= 2 &&
            !imageReference.Any(char.IsWhiteSpace);

        private static string GetShortError(Exception exception)
        {
            string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return message.Length <= 90 ? message : message[..90] + "...";
        }

        /// <summary>
        /// 선택한 유형의 마운트를 목록에 추가하거나 기존 항목을 수정합니다.
        /// </summary>
        private void BtnAddVolume_Click(object sender, RoutedEventArgs e)
        {
            MountKind kind = SelectedMountKind;
            string source = txtVolSource.Text.Trim();
            string target = txtVolTarget.Text.Trim();
            bool anonymousVolume = kind == MountKind.Volume && chkAnonymousVolume.IsChecked == true;

            if ((kind != MountKind.Tmpfs && !anonymousVolume && string.IsNullOrWhiteSpace(source)) ||
                string.IsNullOrWhiteSpace(target))
            {
                _dialogService.ShowMessage(kind == MountKind.Tmpfs
                    ? "컨테이너 경로가 필요합니다."
                    : "원본과 컨테이너 경로가 필요합니다.");
                return;
            }

            if (!ContainerMountParser.IsAbsoluteContainerPath(target))
            {
                _dialogService.ShowInfo($"컨테이너 경로는 절대 경로여야 합니다.\n입력값: {target}", "경로 오류");
                txtVolTarget.Focus();
                return;
            }

            if (kind == MountKind.Bind && !ContainerMountParser.IsAbsoluteHostPath(source))
            {
                _dialogService.ShowInfo("Bind Mount 원본은 절대 호스트 경로여야 합니다.", "경로 오류");
                txtVolSource.Focus();
                return;
            }

            if (_mounts.Any(mount => !ReferenceEquals(mount, _editingMount) &&
                                     string.Equals(mount.Target, target, StringComparison.Ordinal)))
            {
                _dialogService.ShowInfo($"컨테이너 경로가 중복되었습니다: {target}", "마운트 중복");
                return;
            }

            ContainerMountSpec mount;
            try
            {
                mount = new ContainerMountSpec
                {
                    Kind = kind,
                    Source = kind == MountKind.Tmpfs || anonymousVolume ? string.Empty : source,
                    Target = target,
                    ReadOnly = string.Equals(
                        (cmbMountMode.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                        "ro",
                        StringComparison.OrdinalIgnoreCase),
                    BindPropagation = string.IsNullOrWhiteSpace(txtBindPropagation.Text)
                        ? "rprivate"
                        : txtBindPropagation.Text.Trim(),
                    VolumeNoCopy = chkVolumeNoCopy.IsChecked == true,
                    TmpfsSizeBytes = kind == MountKind.Tmpfs
                        ? ContainerMountParser.ParseByteSize(txtTmpfsSize.Text)
                        : 0,
                    TmpfsMode = kind == MountKind.Tmpfs
                        ? ContainerMountParser.ParseOctalMode(txtTmpfsMode.Text)
                        : Convert.ToUInt32("1777", 8)
                };
            }
            catch (FormatException ex)
            {
                _dialogService.ShowInfo(ex.Message, "마운트 옵션 오류");
                return;
            }

            if (_editingMount == null)
            {
                _mounts.Add(mount);
            }
            else
            {
                int index = _mounts.IndexOf(_editingMount);
                _autoAppliedVolumes.Remove(_editingMount.ToLegacyString());
                if (index >= 0) _mounts[index] = mount;
                _editingMount = null;
            }

            ClearMountEditor();
        }

        private MountKind SelectedMountKind =>
            Enum.TryParse((cmbMountType.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out MountKind kind)
                ? kind
                : MountKind.Volume;

        private void MountType_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMountEditor();

        private void UpdateMountEditor()
        {
            if (lblMountSource == null) return;
            MountKind kind = SelectedMountKind;
            lblMountSource.Text = kind switch
            {
                MountKind.Bind => "Docker 호스트 경로",
                MountKind.Tmpfs => "Memory",
                _ => "볼륨 이름"
            };
            txtVolSource.IsEnabled = kind != MountKind.Tmpfs &&
                                     !(kind == MountKind.Volume && chkAnonymousVolume.IsChecked == true);
            btnBrowseBindSource.Visibility = kind == MountKind.Bind ? Visibility.Visible : Visibility.Collapsed;
            pnlVolumeOptions.Visibility = kind == MountKind.Volume ? Visibility.Visible : Visibility.Collapsed;
            pnlBindOptions.Visibility = kind == MountKind.Bind ? Visibility.Visible : Visibility.Collapsed;
            pnlTmpfsOptions.Visibility = kind == MountKind.Tmpfs ? Visibility.Visible : Visibility.Collapsed;

            bool local = IsLocalDockerHost();
            btnBrowseBindSource.IsEnabled = local;
            txtMountHostHint.Text = kind switch
            {
                MountKind.Bind when local => "내 PC의 경로를 Docker 호스트에 연결합니다.",
                MountKind.Bind => "원격 Docker 호스트 기준 경로를 직접 입력하세요.",
                MountKind.Tmpfs => "컨테이너와 함께 사라지는 메모리 기반 저장소입니다.",
                _ => "Docker가 관리하는 이름 있는 볼륨입니다."
            };
        }

        private bool IsLocalDockerHost() =>
            _connectionProfile?.Type == EndpointType.Local ||
            (_connectionProfile?.Type == EndpointType.DockerContext &&
             _connectionProfile.DockerEndpoint?.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) == true);

        private void BrowseBindSource_Click(object sender, RoutedEventArgs e)
        {
            if (!IsLocalDockerHost())
            {
                _dialogService.ShowInfo(
                    "현재 연결은 원격 Docker입니다. Docker 호스트의 경로를 직접 입력하세요.",
                    "원격 Docker 경로");
                return;
            }
            string? folder = _dialogService.ShowOpenFolderDialog(
                "Bind mount로 사용할 폴더 선택",
                System.IO.Directory.Exists(txtVolSource.Text) ? txtVolSource.Text : null);
            if (!string.IsNullOrWhiteSpace(folder)) txtVolSource.Text = folder;
        }

        private void AnonymousVolume_Changed(object sender, RoutedEventArgs e)
        {
            if (chkAnonymousVolume?.IsChecked == true)
                txtVolSource?.Clear();
            UpdateMountEditor();
        }

        private void BtnEditMount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ContainerMountSpec mount }) return;
            _editingMount = mount;
            SelectMountKind(mount.Kind);
            txtVolSource.Text = mount.Source;
            txtVolTarget.Text = mount.Target;
            cmbMountMode.SelectedIndex = mount.ReadOnly ? 1 : 0;
            txtBindPropagation.Text = mount.BindPropagation;
            chkAnonymousVolume.IsChecked = mount.Kind == MountKind.Volume && string.IsNullOrWhiteSpace(mount.Source);
            chkVolumeNoCopy.IsChecked = mount.VolumeNoCopy;
            txtTmpfsSize.Text = mount.TmpfsSizeBytes > 0
                ? mount.TmpfsSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "0";
            txtTmpfsMode.Text = Convert.ToString(mount.TmpfsMode, 8);
            btnAddMount.Content = "적용";
        }

        private void SelectMountKind(MountKind kind)
        {
            foreach (ComboBoxItem item in cmbMountType.Items)
            {
                if (string.Equals(item.Tag?.ToString(), kind.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    cmbMountType.SelectedItem = item;
                    break;
                }
            }
            UpdateMountEditor();
        }

        private void ClearMountEditor()
        {
            _editingMount = null;
            txtVolSource.Clear();
            txtVolTarget.Clear();
            cmbMountMode.SelectedIndex = 0;
            txtBindPropagation.Text = "rprivate";
            chkAnonymousVolume.IsChecked = false;
            chkVolumeNoCopy.IsChecked = false;
            txtTmpfsSize.Text = "64m";
            txtTmpfsMode.Text = "1777";
            btnAddMount.Content = "추가";
            txtVolSource.Focus();
        }

        /// <summary>
        /// 볼륨 마운트 리스트에서 특정 항목(X 버튼 클릭)을 제거합니다.
        /// </summary>
        private void BtnRemoveVolume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ContainerMountSpec item)
            {
                _autoAppliedVolumes.Remove(item.ToLegacyString());
                _mounts.Remove(item);
                if (ReferenceEquals(_editingMount, item)) ClearMountEditor();
            }
        }

        /// <summary>
        /// CPU나 메모리 입력 칸에 숫자와 소수점만 입력될 수 있도록 제어합니다.
        /// </summary>
        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            e.Handled = InvalidNumericInputRegex().IsMatch(e.Text);
        }

        /// <summary>
        /// 시스템 탐색기를 열어 .env 형식의 환경변수 파일을 선택합니다.
        /// </summary>
        private void BtnLoadEnv_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Env files (*.env)|*.env|All files (*.*)|*.*",
                Title = ".env 파일 선택",
                Multiselect = false // 여러 개 선택 금지
            };

            if (openFileDialog.ShowDialog() == true)
            {
                LoadEnvFile(openFileDialog.FileName);
            }
        }

        /// <summary>
        /// UI 화면에 환경변수(.env) 파일을 마우스로 드래그 앤 드롭했을 때 파일을 로드합니다.
        /// </summary>
        private void Env_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string? envFile = files.FirstOrDefault(); // 무조건 1개만 추출

                if (!string.IsNullOrEmpty(envFile))
                {
                    LoadEnvFile(envFile);
                }
            }
        }

        /// <summary>
        /// 선택된 .env 파일을 읽어 주석(#)과 빈 줄을 제외한 KEY=VALUE 쌍만 환경변수 입력창에 일괄 추가합니다.
        /// </summary>
        private void LoadEnvFile(string filePath)
        {
            if (!System.IO.File.Exists(filePath)) return;

            try
            {
                var lines = System.IO.File.ReadAllLines(filePath);
                int addedCount = 0;

                // 기존에 적혀있던 텍스트가 있다면 줄바꿈을 한번 해줍니다.
                if (!string.IsNullOrWhiteSpace(txtEnv.Text) && !txtEnv.Text.EndsWith('\n'))
                {
                    txtEnv.Text += "\r\n";
                }

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();

                    // 주석(#)과 빈 줄 무시
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                        continue;

                    // KEY=VALUE 형태일 때 TextBox에 텍스트를 추가
                    if (trimmed.Contains('='))
                    {
                        txtEnv.Text += trimmed + "\r\n";
                        addedCount++;
                    }
                }

                if (addedCount > 0)
                {
                    _dialogService.ShowInfo($"{addedCount}개의 환경변수를 성공적으로 불러왔습니다.", "로드 완료");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowInfo($"파일을 읽는 중 오류가 발생했습니다:\n{ex.Message}", "오류");
            }
        }

        [GeneratedRegex(@"^[a-zA-Z]:[\\/]")]
        private static partial Regex MyRegex();

        [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*$")]
        private static partial Regex EnvironmentVariableNameRegex();

        [GeneratedRegex(@"[^A-Za-z0-9_.-]")]
        private static partial Regex InvalidDockerNameCharacterRegex();

        [GeneratedRegex(@"[^a-z0-9_.-]")]
        private static partial Regex InvalidLowerDockerNameCharacterRegex();

        [GeneratedRegex("[^0-9.]+")]
        private static partial Regex InvalidNumericInputRegex();
    }
}
