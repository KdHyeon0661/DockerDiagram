using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DockerDiagram.Views
{
    public partial class ContainerDialog : Window
    {
        public string ContainerName => txtName.Text.Trim();
        public string ImageName
        {
            get => txtImage.Text.Trim();
            set => txtImage.Text = value;
        }

        public List<string> Ports
        {
            get
            {
                var list = new List<string>();
                foreach (var line in txtPorts.Text.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                }
                return list;
            }
        }

        public List<string> EnvVars
        {
            get
            {
                var list = new List<string>();
                foreach (var line in txtEnv.Text.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                }
                return list;
            }
        }

        public string RestartPolicy => (cmbRestart.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "no";

        // XAML의 이름인 txtMem 사용
        public long MemoryMb => long.TryParse(txtMem.Text, out var v) ? v : 0;

        public double CpuCount => double.TryParse(txtCpu.Text, out var v) ? v : 0;

        public List<string> Volumes
        {
            get
            {
                var list = new List<string>();
                foreach (var item in lstVolumes.Items)
                {
                    if (item != null)
                    {
                        list.Add(item.ToString() ?? "");
                    }
                }
                return list;
            }
        }

        public ContainerDialog()
        {
            InitializeComponent();
            txtName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ImageName))
            {
                MessageBox.Show("Container Name and Image Name are required.");
                return;
            }
            DialogResult = true;
        }

        private void BtnAddVolume_Click(object sender, RoutedEventArgs e)
        {
            string source = txtVolSource.Text.Trim();
            string target = txtVolTarget.Text.Trim();

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show("Host Path and Container Path are required.");
                return;
            }

            string volumeString = $"{source}:{target}";
            if (!lstVolumes.Items.Contains(volumeString))
            {
                lstVolumes.Items.Add(volumeString);
                txtVolSource.Clear();
                txtVolTarget.Clear();
                txtVolSource.Focus();
            }
        }

        private void BtnRemoveVolume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string item)
            {
                lstVolumes.Items.Remove(item);
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9.]+");
            e.Handled = regex.IsMatch(e.Text);
        }
    }
}