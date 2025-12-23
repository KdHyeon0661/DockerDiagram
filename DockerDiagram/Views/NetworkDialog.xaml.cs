using System.Windows;
using System.Windows.Controls;

namespace DockerDiagram.Views
{
    public partial class NetworkDialog : Window
    {
        public string NetworkName => txtName.Text.Trim();
        public string Driver => (cmbDriver.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "bridge";

        public NetworkDialog()
        {
            InitializeComponent();
            txtName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NetworkName))
            {
                MessageBox.Show("Network Name is required.");
                return;
            }
            DialogResult = true;
        }
    }
}