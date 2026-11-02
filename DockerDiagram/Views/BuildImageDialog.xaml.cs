using System.Windows;

namespace DockerDiagram.Views
{
    public partial class BuildImageDialog : Window
    {
        private readonly DockerDiagram.Contracts.IDialogService _dialogService;
        public string TagName => txtImageTag.Text.Trim();
        public string DockerfileContent => txtDockerfile.Text.Trim();
        public string DockerfilePath { get; private set; } = "";

        public BuildImageDialog(DockerDiagram.Contracts.IDialogService dialogService)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
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
                _dialogService.ShowInfo("이미지 이름과 Dockerfile 내용을 모두 입력해주세요.", "입력 오류");
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
