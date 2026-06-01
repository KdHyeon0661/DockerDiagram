using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ImagePushWindow : Window
    {
        public string Repository => RepositoryBox.Text.Trim();
        public string ImageTag => ImageTagBox.Text.Trim();
        public string ServerAddress => ServerBox.Text.Trim();
        public string Username => UsernameBox.Text.Trim();
        public string Password => PasswordBox.Password;

        public ImagePushWindow(string repository, string tag)
        {
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
                MessageBox.Show(this, "Repository is required.", "Push Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(ImageTag))
            {
                MessageBox.Show(this, "Tag is required.", "Push Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
