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
            if (!MountPath.StartsWith("/"))
            {
                MessageBox.Show("경로는 절대 경로(/)로 시작해야 합니다.");
                txtPath.Focus();
                return;
            }
            DialogResult = true;
        }
    }
}