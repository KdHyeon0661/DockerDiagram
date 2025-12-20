using System.Windows;

namespace DockerDiagram.Views
{
    public partial class VolumeDialog : Window
    {
        public string VolumeName => txtName.Text.Trim();
        public string Driver => txtDriver.Text.Trim();

        public VolumeDialog()
        {
            InitializeComponent();
            txtName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VolumeName))
            {
                MessageBox.Show("Volume Name is required.");
                return;
            }
            DialogResult = true;
        }
    }
}