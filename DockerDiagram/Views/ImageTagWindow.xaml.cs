using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ImageTagWindow : Window
    {
        public string Repository => RepositoryBox.Text.Trim();
        public string ImageTag => ImageTagBox.Text.Trim();
        public bool Force => ForceBox.IsChecked == true;

        public ImageTagWindow(string sourceImage, string repository, string tag)
        {
            InitializeComponent();
            SourceText.Text = sourceImage;
            RepositoryBox.Text = repository == "<none>" ? string.Empty : repository;
            ImageTagBox.Text = tag == "<none>" ? "latest" : tag;
            RepositoryBox.Focus();
            RepositoryBox.SelectAll();
        }

        private void TagButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Repository))
            {
                MessageBox.Show(this, "Repository is required.", "Tag Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(ImageTag))
            {
                MessageBox.Show(this, "Tag is required.", "Tag Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
