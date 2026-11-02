using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ImageTagWindow : Window
    {
        private readonly DockerDiagram.Contracts.IDialogService _dialogService;
        public string Repository => RepositoryBox.Text.Trim();
        public string ImageTag => ImageTagBox.Text.Trim();
        public bool Force => ForceBox.IsChecked == true;

        public ImageTagWindow(
            DockerDiagram.Contracts.IDialogService dialogService,
            string sourceImage,
            string repository,
            string tag)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
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
                _dialogService.ShowInfo("Repository is required.", "Tag Image");
                return;
            }

            if (string.IsNullOrWhiteSpace(ImageTag))
            {
                _dialogService.ShowInfo("Tag is required.", "Tag Image");
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
