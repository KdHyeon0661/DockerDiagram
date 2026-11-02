using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ImagePushWindow : Window
    {
        private readonly DockerDiagram.Contracts.IDialogService _dialogService;
        public string Repository => RepositoryBox.Text.Trim();
        public string ImageTag => ImageTagBox.Text.Trim();
        public string ServerAddress => ServerBox.Text.Trim();
        public string Username => UsernameBox.Text.Trim();
        public string Password => PasswordBox.Password;

        public ImagePushWindow(
            DockerDiagram.Contracts.IDialogService dialogService,
            string repository,
            string tag)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            InitializeComponent();
            RepositoryBox.Text = repository;
            ImageTagBox.Text = tag;
            RepositoryBox.Focus();
            RepositoryBox.SelectAll();
        }

        private void PushButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Repository))
            {
                _dialogService.ShowInfo("Repository is required.", "Push Image");
                return;
            }

            if (string.IsNullOrWhiteSpace(ImageTag))
            {
                _dialogService.ShowInfo("Tag is required.", "Push Image");
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
