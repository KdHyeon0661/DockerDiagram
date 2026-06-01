using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ImageImportWindow : Window
    {
        public string TarPath => TarPathBox.Text.Trim();
        public string Repository => RepositoryBox.Text.Trim();
        public string ImageTag => ImageTagBox.Text.Trim();
        public string Message => ImportMessageBox.Text.Trim();

        public ImageImportWindow()
        {
            InitializeComponent();
            ImageTagBox.Text = "latest";
            TarPathBox.Focus();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select container filesystem tar",
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
                MessageBox.Show(this, "A valid tar file is required.", "Import Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(Repository))
            {
                MessageBox.Show(this, "Repository is required.", "Import Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(ImageTag))
            {
                MessageBox.Show(this, "Tag is required.", "Import Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
