using System.Windows;

namespace DockerDiagram.Views
{
    public partial class KubernetesPortForwardDialog : Window
    {
        public int LocalPort { get; private set; }
        public int RemotePort { get; private set; }

        public KubernetesPortForwardDialog(string kind, string target, int defaultLocalPort, int defaultRemotePort)
        {
            InitializeComponent();
            TargetTextBlock.Text = $"{kind} {target}";
            LocalPortTextBox.Text = defaultLocalPort.ToString();
            RemotePortTextBox.Text = defaultRemotePort.ToString();
            LocalPortTextBox.SelectAll();
            LocalPortTextBox.Focus();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadPort(LocalPortTextBox.Text, out int localPort))
            {
                ErrorTextBlock.Text = "Local Port는 1~65535 사이의 숫자여야 합니다.";
                return;
            }

            if (!TryReadPort(RemotePortTextBox.Text, out int remotePort))
            {
                ErrorTextBlock.Text = "Remote Port는 1~65535 사이의 숫자여야 합니다.";
                return;
            }

            LocalPort = localPort;
            RemotePort = remotePort;
            DialogResult = true;
        }

        private static bool TryReadPort(string text, out int port)
        {
            return int.TryParse(text, out port) && port is >= 1 and <= 65535;
        }
    }
}
