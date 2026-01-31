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

        public List<string> Volumes // 볼륨 마운트 리스트
        {
            get
            {
                var list = new List<string>();
                foreach (var item in lstVolumes.Items) // 리스트 아이템 순회
                {
                    if (item != null)
                    {
                        list.Add(item.ToString() ?? "");
                    }
                }
                return list;
            }
        }

        public ContainerDialog()
        {
            InitializeComponent();
            txtName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) // 확인 버튼
        {
            if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ImageName)) // 이름과 이미지가 비어있는지 확인
            {
                MessageBox.Show("컨테이너 명과 이미지 명을 입력하세요.");
                return;
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
    }
}