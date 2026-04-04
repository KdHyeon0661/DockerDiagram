using System.Windows;

namespace DockerDiagram.Views
{
    public partial class BuildImageDialog : Window
    {
        public string TagName => txtImageTag.Text.Trim();
        public string DockerfileContent => txtDockerfile.Text.Trim();
        public string DockerfilePath { get; private set; } = "";

        public BuildImageDialog()
        {
            InitializeComponent();
            txtImageTag.Focus();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var openDlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Dockerfile|*.*|All Files|*.*",
                Title = "빌드할 도커파일 선택"
            };

            if (openDlg.ShowDialog() == true)
            {
                DockerfilePath = openDlg.FileName;
                txtDockerfile.Text = System.IO.File.ReadAllText(openDlg.FileName);
            }
        }

        private void BtnBuild_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TagName) || string.IsNullOrWhiteSpace(DockerfileContent))
            {
                MessageBox.Show("이미지 이름과 Dockerfile 내용을 모두 입력해주세요.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}