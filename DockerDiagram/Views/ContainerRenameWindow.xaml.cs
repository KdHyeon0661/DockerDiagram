using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ContainerRenameWindow : Window
    {
        public string NewName => NameBox.Text.Trim();

        public ContainerRenameWindow(string currentName)
        {
            InitializeComponent();
            NameBox.Text = currentName;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NewName))
            {
                MessageBox.Show(this, "Container name is required.", "Rename Container", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
