using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

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
        private readonly Dictionary<string, Control> _profileInputs =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _imageLookupTimer;
        private ContainerImageProfile? _activeImageProfile;
        private ContainerImageMetadata? _imageMetadata;
        private CancellationTokenSource? _imageLookupCancellation;

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
            get => ReadNonEmptyLines(txtEnv.Text);
        }

        public string RestartPolicy => (cmbRestart.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "no";
        public long MemoryMb => long.TryParse(txtMem.Text, out var v) ? v : 0;
        public double CpuCount => double.TryParse(txtCpu.Text, out var v) ? v : 0;
        public List<string> Volumes
        {
            get
            {
                var list = new List<string>();
                foreach (var item in lstVolumes.Items)
                {
                    if (item != null) list.Add(item.ToString() ?? "");
                }

                return list;
            }
        }
        public List<string> ResultBindMounts { get; private set; } = new List<string>();
        public List<string> ResultNamedVolumes { get; private set; } = new List<string>();

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
        public ContainerDialog(IDialogService dialogService, IImageService? imageService = null)
        {
            InitializeComponent();
            _dialogService = dialogService;
            _imageService = imageService;
            _imageLookupTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _imageLookupTimer.Tick += ImageLookupTimer_Tick;
            Closed += (_, _) =>
            {
                _imageLookupTimer.Stop();
                _imageLookupCancellation?.Cancel();
                _imageLookupCancellation?.Dispose();
            };
            UpdateImageProfile();
            txtName.Focus();
        }

        private void ImageName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (tabImageSetup == null)
                return;

            UpdateImageProfile();
            ClearImageMetadata();
            _imageLookupTimer.Stop();

            if (_imageService != null && IsLookupCandidate(ImageName))
                _imageLookupTimer.Start();
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
            tabImageSetup.Header = "Initial Setup";

            if (!hasProfile)
            {
                RefreshSetupPresentation();
                return;
            }

            txtProfileCategory.Text = string.IsNullOrWhiteSpace(profile!.Category)
                ? "Container profile"
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

                ResultBindMounts.Clear();
                ResultNamedVolumes.Clear();
                foreach (string volumeString in Volumes)
                {
                    bool isWindowsPath = Regex.IsMatch(volumeString, @"^[a-zA-Z]:[\\/]");
                    bool isLinuxPath = volumeString.StartsWith("/");
                    bool isRelativePath = volumeString.StartsWith("./") || volumeString.StartsWith("../");

                    if (isWindowsPath || isLinuxPath || isRelativePath) ResultBindMounts.Add(volumeString);
                    else ResultNamedVolumes.Add(volumeString);
                }
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
            var ports = ReadNonEmptyLines(txtPorts.Text);
            var environments = ReadNonEmptyLines(txtEnv.Text);
            var volumes = lstVolumes.Items.Cast<object>()
                .Select(item => item?.ToString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
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
                        AddEnvironmentIfMissing(
                            environments,
                            profileField.EnvironmentVariable,
                            value);
                    }

                    if (chkApplyProfilePorts.IsChecked == true &&
                        !string.IsNullOrWhiteSpace(profileField.ContainerPort) &&
                        int.TryParse(value, out int port) &&
                        port is >= 1 and <= 65535)
                    {
                        AddPortIfMissing(ports, $"{port}:{profileField.ContainerPort}");
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
                        AddVolumeIfMissing(
                            volumes,
                            $"{containerName}-{volume.NameSuffix}:{volume.ContainerPath}");
                    }
                }

                if (string.IsNullOrWhiteSpace(txtCommand.Text) &&
                    CanResolveProfileCommand())
                {
                    txtCommand.Text = ResolveProfileTemplate(_activeImageProfile.CommandTemplate);
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
                            AddPortIfMissing(ports, $"{port}:{exposedPort}");
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
                        AddVolumeIfMissing(
                            volumes,
                            $"{containerName}-{suffix}:{target}");
                    }
                }
            }

            txtPorts.Text = string.Join(Environment.NewLine, ports);
            txtEnv.Text = string.Join(Environment.NewLine, environments);
            lstVolumes.Items.Clear();
            foreach (string volume in volumes)
                lstVolumes.Items.Add(volume);

            txtImageLookupStatus.Text =
                "제안 내용을 설정 탭에 적용했습니다. 생성 전에 자유롭게 수정할 수 있습니다.";
        }

        private bool ValidateManualEnvironmentVariables()
        {
            foreach (string line in ReadNonEmptyLines(txtEnv.Text))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0 ||
                    !Regex.IsMatch(line[..separator].Trim(), @"^[A-Za-z_][A-Za-z0-9_.-]*$"))
                {
                    _dialogService.ShowInfo(
                        $"환경변수는 KEY=VALUE 형식이어야 합니다.\n입력값: {line}",
                        "환경변수 오류");
                    return false;
                }
            }

            return true;
        }

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
            if (Regex.IsMatch(mapping, @"^[a-zA-Z]:[\\/]"))
            {
                int separator = mapping.IndexOf(':', 2);
                return separator >= 0 ? mapping[(separator + 1)..].Split(':')[0] : string.Empty;
            }

            string[] parts = mapping.Split(':');
            return parts.Length > 1 ? parts[1] : string.Empty;
        }

        private static string SanitizeDockerName(string value)
        {
            string sanitized = Regex.Replace(value.Trim(), @"[^A-Za-z0-9_.-]", "-");
            return string.IsNullOrWhiteSpace(sanitized) ? "container" : sanitized;
        }

        private static void AddEnvironmentIfMissing(
            ICollection<string> environments,
            string key,
            string value)
        {
            bool exists = environments.Any(line =>
                line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (!exists)
                environments.Add($"{key}={value}");
        }

        private static void AddPortIfMissing(ICollection<string> ports, string mapping)
        {
            string containerPort = GetContainerPort(mapping);
            bool exists = ports.Any(port =>
                GetContainerPort(port).Equals(containerPort, StringComparison.OrdinalIgnoreCase));
            if (!exists)
                ports.Add(mapping);
        }

        private static void AddVolumeIfMissing(ICollection<string> volumes, string mapping)
        {
            string target = GetVolumeTarget(mapping);
            bool exists = volumes.Any(volume =>
                GetVolumeTarget(volume).Equals(target, StringComparison.OrdinalIgnoreCase));
            if (!exists)
                volumes.Add(mapping);
        }

        private static string GetVolumeNameSuffix(string target, int index)
        {
            string segment = target.TrimEnd('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? $"data-{index}";
            string suffix = Regex.Replace(segment.ToLowerInvariant(), @"[^a-z0-9_.-]", "-");
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
        /// 볼륨 마운트 리스트에 새로운 경로 매핑(Host:Container)을 추가합니다.
        /// </summary>
        private void BtnAddVolume_Click(object sender, RoutedEventArgs e)
        {
            string source = txtVolSource.Text.Trim();
            string target = txtVolTarget.Text.Trim();

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                _dialogService.ShowMessage("호스트 경로와 컨테이너 경로가 필요합니다.");
                return;
            }

            bool isTargetLinux = target.StartsWith("/");
            bool isTargetWindows = Regex.IsMatch(target, @"^[a-zA-Z]:[\\/]");

            if (!isTargetLinux && !isTargetWindows)
            {
                _dialogService.ShowInfo($"컨테이너 경로는 절대 경로여야 합니다.\n입력값: {target}", "경로 오류");
                txtVolTarget.Focus();
                return;
            }

            string volumeString = $"{source}:{target}";
            if (!lstVolumes.Items.Contains(volumeString))
            {
                lstVolumes.Items.Add(volumeString);
                txtVolSource.Clear();
                txtVolTarget.Clear();
                txtVolSource.Focus();
            }
        }

        /// <summary>
        /// 볼륨 마운트 리스트에서 특정 항목(X 버튼 클릭)을 제거합니다.
        /// </summary>
        private void BtnRemoveVolume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string item)
            {
                lstVolumes.Items.Remove(item);
            }
        }

        /// <summary>
        /// CPU나 메모리 입력 칸에 숫자와 소수점만 입력될 수 있도록 제어합니다.
        /// </summary>
        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9.]+");
            e.Handled = regex.IsMatch(e.Text);
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
                if (!string.IsNullOrWhiteSpace(txtEnv.Text) && !txtEnv.Text.EndsWith("\n"))
                {
                    txtEnv.Text += "\r\n";
                }

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();

                    // 주석(#)과 빈 줄 무시
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                        continue;

                    // KEY=VALUE 형태일 때 TextBox에 텍스트를 추가
                    if (trimmed.Contains("="))
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
    }
}
