using System.Text.RegularExpressions;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class MountDialog : Window
    {
        public string MountPath => txtPath.Text.Trim();
        public string VolumeOwner => txtOwner.Text.Trim();

        public MountDialog()
        {
            InitializeComponent();
            txtPath.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MountPath))
            {
                MessageBox.Show("컨테이너 내부 경로(Container Path)는 필수 입력값입니다.");
                txtPath.Focus();
                return;
            }

            // [수정됨] Windows 컨테이너 호환성 해결
            // 1. 리눅스 스타일 절대 경로: /app/data
            // 2. 윈도우 스타일 절대 경로: C:\app\data (대소문자 무관)
            bool isLinuxStyle = MountPath.StartsWith("/");
            bool isWindowsStyle = Regex.IsMatch(MountPath, @"^[a-zA-Z]:[\\/]");

            if (!isLinuxStyle && !isWindowsStyle)
            {
                MessageBox.Show("경로는 절대 경로여야 합니다.\n(예: /app/data 또는 C:\\app\\data)");
                txtPath.Focus();
                return;
            }

            DialogResult = true;
        }
    }
}