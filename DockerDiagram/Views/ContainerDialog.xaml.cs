using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DockerDiagram.Views
{
    public partial class ContainerDialog : Window
    {
        public string ContainerName => txtName.Text.Trim(); // 도커 컨테이너 이름
        public string ImageName // 도커 이미지 이름
        {
            get => txtImage.Text.Trim();
            set => txtImage.Text = value;
        }

        public List<string> Ports // 포트 바인딩 리스트
        {
            get
            {
                var list = new List<string>();
                foreach (var line in txtPorts.Text.Split('\n')) // 줄 단위로 분리
                {
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                }
                return list;
            }
        }

        public List<string> EnvVars // 환경 변수 리스트
        {
            get
            {
                var list = new List<string>();
                foreach (var line in txtEnv.Text.Split('\n')) // 줄 단위로 분리
                {
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                }
                return list;
            }
        }

        public string RestartPolicy => (cmbRestart.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "no"; // 재시작 정책

        public long MemoryMb => long.TryParse(txtMem.Text, out var v) ? v : 0; // 메모리 제한 (MB 단위)

        public double CpuCount => double.TryParse(txtCpu.Text, out var v) ? v : 0; // CPU 제한 (코어 수 단위)

        // [변경] 기존 Volumes 속성은 전체 리스트를 반환용으로 유지 (하위 호환성)
        // 하지만 실제 로직에서는 아래의 분리된 두 리스트를 사용하는 것을 권장합니다.
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

        // ★ [추가] Bind Mount(호스트 경로)로 판별된 항목들
        public List<string> ResultBindMounts { get; private set; } = new List<string>();

        // ★ [추가] Named Volume(도커 볼륨)으로 판별된 항목들
        public List<string> ResultNamedVolumes { get; private set; } = new List<string>();

        public ContainerDialog()
        {
            InitializeComponent();
            txtName.Focus();
        }

        // ★ [수정] 확인 버튼 클릭 시 분류 로직 수행
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ImageName))
            {
                MessageBox.Show("컨테이너 명과 이미지 명을 입력하세요.");
                return;
            }

            // 분류 리스트 초기화
            ResultBindMounts.Clear();
            ResultNamedVolumes.Clear();

            // 리스트 박스에 있는 모든 항목을 검사하여 분류
            foreach (var item in lstVolumes.Items)
            {
                if (item is string volumeString)
                {
                    // volumeString 예: "C:\Users\Data:/app" 또는 "my-vol:/app"

                    // 소스(Source) 부분 추출을 위한 간단한 로직
                    // 윈도우 경로(C:) 때문에 단순 Split(':')은 위험할 수 있으나, 
                    // "맨 앞부분이 경로 패턴인가?"만 확인하면 되므로 전체 문자열로 검사해도 무방합니다.

                    // 1. 윈도우 절대 경로 패턴 (C:\...)
                    bool isWindowsPath = Regex.IsMatch(volumeString, @"^[a-zA-Z]:[\\/]");
                    // 2. 리눅스/Mac 절대 경로 패턴 (/...)
                    bool isLinuxPath = volumeString.StartsWith("/");
                    // 3. 상대 경로 패턴 (./...)
                    bool isRelativePath = volumeString.StartsWith("./") || volumeString.StartsWith("../");

                    if (isWindowsPath || isLinuxPath || isRelativePath)
                    {
                        // -> Bind Mount (속성으로 저장)
                        ResultBindMounts.Add(volumeString);
                    }
                    else
                    {
                        // -> Named Volume (원기둥 노드로 생성)
                        ResultNamedVolumes.Add(volumeString);
                    }
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
                MessageBox.Show("호스트 경로와 컨테이너 경로가 필요합니다.");
                return;
            }

            // [유지] 컨테이너 경로(Target) 유효성 검사 (Windows/Linux 호환)
            bool isTargetLinux = target.StartsWith("/");
            bool isTargetWindows = Regex.IsMatch(target, @"^[a-zA-Z]:[\\/]");

            if (!isTargetLinux && !isTargetWindows)
            {
                MessageBox.Show($"컨테이너 경로는 절대 경로여야 합니다.\n입력값: {target}\n(예: /data 또는 C:\\data)",
                                "경로 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            else
            {
                MessageBox.Show("이미 추가된 볼륨 경로입니다.");
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
    }
}