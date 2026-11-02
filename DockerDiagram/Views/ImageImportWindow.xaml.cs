using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DockerDiagram.Views
{
    public partial class ImageImportWindow : Window
    {
        private readonly DockerDiagram.Contracts.IDialogService _dialogService;
        public string TarPath => TarPathBox.Text.Trim();
        public string Repository => RepositoryBox.Text.Trim();
        public string ImageTag => ImageTagBox.Text.Trim();
        public string Message => ImportMessageBox.Text.Trim();

        public ImageImportWindow(DockerDiagram.Contracts.IDialogService dialogService)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            InitializeComponent();
            ImageTagBox.Text = "latest";
            TarPathBox.Focus();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "컨테이너 파일 시스템 tar 선택",
                Filter = "Tar files (*.tar)|*.tar|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                TarPathBox.Text = dialog.FileName;
                if (string.IsNullOrWhiteSpace(Repository))
                {
                    RepositoryBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName).ToLowerInvariant();
                }
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(TarPath))
            {
                _dialogService.ShowInfo("A valid tar file is required.", "Import Image");
                return;
            }

            if (string.IsNullOrWhiteSpace(Repository))
            {
                _dialogService.ShowInfo("Repository is required.", "Import Image");
                return;
            }

            if (string.IsNullOrWhiteSpace(ImageTag))
            {
                _dialogService.ShowInfo("Tag is required.", "Import Image");
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
