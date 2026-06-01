using System.Text.RegularExpressions;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ContainerCommitWindow : Window
    {
        public string Repository => RepositoryBox.Text.Trim();
        public string ImageTag => ImageTagBox.Text.Trim();
        public string Message => CommitMessageBox.Text.Trim();
        public string Author => AuthorBox.Text.Trim();
        public bool Pause => PauseBox.IsChecked == true;

        public ContainerCommitWindow(string containerName)
        {
            InitializeComponent();
            ContainerText.Text = containerName;
            RepositoryBox.Text = MakeDefaultRepository(containerName);
            ImageTagBox.Text = "snapshot";
            RepositoryBox.Focus();
            RepositoryBox.SelectAll();
        }

        private void CommitButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Repository))
            {
                MessageBox.Show(this, "Repository is required.", "Commit Container", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(ImageTag))
            {
                MessageBox.Show(this, "Tag is required.", "Commit Container", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private static string MakeDefaultRepository(string value)
        {
            value = value.Trim().TrimStart('/').ToLowerInvariant();
            value = Regex.Replace(value, @"[^a-z0-9_.\-\/]", "-");
            return string.IsNullOrWhiteSpace(value) ? "container-snapshot" : value;
        }
    }
}
