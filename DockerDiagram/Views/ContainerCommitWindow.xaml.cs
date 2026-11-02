using System.Text.RegularExpressions;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ContainerCommitWindow : Window
    {
        private readonly DockerDiagram.Contracts.IDialogService _dialogService;
        public string Repository => RepositoryBox.Text.Trim();
        public string ImageTag => ImageTagBox.Text.Trim();
        public string Message => CommitMessageBox.Text.Trim();
        public string Author => AuthorBox.Text.Trim();
        public bool Pause => PauseBox.IsChecked == true;

        public ContainerCommitWindow(DockerDiagram.Contracts.IDialogService dialogService, string containerName)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
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
                _dialogService.ShowInfo("Repository is required.", "Commit Container");
                return;
            }

            if (string.IsNullOrWhiteSpace(ImageTag))
            {
                _dialogService.ShowInfo("Tag is required.", "Commit Container");
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private static string MakeDefaultRepository(string value)
        {
            value = value.Trim().TrimStart('/').ToLowerInvariant();
            value = MyRegex().Replace(value, "-");
            return string.IsNullOrWhiteSpace(value) ? "container-snapshot" : value;
        }

        [GeneratedRegex(@"[^a-z0-9_.\-\/]")]
        private static partial Regex MyRegex();
    }
}
