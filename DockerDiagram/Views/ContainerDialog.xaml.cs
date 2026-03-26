using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DockerDiagram.Helpers;

namespace DockerDiagram.Views
{
    public partial class ContainerDialog : Window
    {
        private readonly IDialogService _dialogService;

        // =====================================
        // [공통] 현재 사용자가 선택한 생성 방식 (0: UI, 1: CLI, 2: Dockerfile)
        // =====================================
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

        public List<string> Ports
        {
            get
            {
                var list = new List<string>();
                foreach (var line in txtPorts.Text.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                return list;
            }
        }

        public List<string> EnvVars
        {
            get
            {
                var list = new List<string>();
                foreach (var line in txtEnv.Text.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                return list;
            }
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


        public ContainerDialog()
        {
            InitializeComponent();

            _dialogService = new DialogService();

            txtName.Focus();
        }

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

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCreationMode == 0) // UI 방식
            {
                if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ImageName))
                {
                    // ★ MessageBox.Show 대신 _dialogService.ShowInfo 사용!
                    _dialogService.ShowInfo("컨테이너 명과 이미지 명을 입력하세요.", "입력 오류");
                    return;
                }

                ResultBindMounts.Clear();
                ResultNamedVolumes.Clear();
                foreach (var item in lstVolumes.Items)
                {
                    if (item is string volumeString)
                    {
                        bool isWindowsPath = Regex.IsMatch(volumeString, @"^[a-zA-Z]:[\\/]");
                        bool isLinuxPath = volumeString.StartsWith("/");
                        bool isRelativePath = volumeString.StartsWith("./") || volumeString.StartsWith("../");

                        if (isWindowsPath || isLinuxPath || isRelativePath) ResultBindMounts.Add(volumeString);
                        else ResultNamedVolumes.Add(volumeString);
                    }
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

        private void BtnRemoveVolume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string item)
            {
                lstVolumes.Items.Remove(item);
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9.]+");
            e.Handled = regex.IsMatch(e.Text);
        }

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

        private void Env_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string envFile = files.FirstOrDefault(); // 무조건 1개만 추출

                if (!string.IsNullOrEmpty(envFile))
                {
                    LoadEnvFile(envFile);
                }
            }
        }

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
                    // ★ MessageBox 대신 일관성 있게 _dialogService 사용
                    _dialogService.ShowInfo($"{addedCount}개의 환경변수를 성공적으로 불러왔습니다.", "로드 완료");
                }
            }
            catch (Exception ex)
            {
                // ★ 여기도 _dialogService 로 통일
                _dialogService.ShowInfo($"파일을 읽는 중 오류가 발생했습니다:\n{ex.Message}", "오류");
            }
        }
    }
}