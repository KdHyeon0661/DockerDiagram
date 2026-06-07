using System.Windows;

namespace DockerDiagram.Views
{
    public partial class DockerContextCreateDialog : Window
    {
        public DockerContextCreateDialog()
        {
            InitializeComponent();
        }

        public string ContextName => NameBox.Text.Trim();
        public string DockerEndpoint => EndpointBox.Text.Trim();

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ContextName))
            {
                MessageBox.Show(this, "Context name is required.", "Create Docker Context", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(DockerEndpoint))
            {
                MessageBox.Show(this, "Docker host endpoint is required.", "Create Docker Context", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
