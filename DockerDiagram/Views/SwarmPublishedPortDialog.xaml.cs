using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DockerDiagram.Views
{
    public partial class SwarmPublishedPortDialog : Window
    {
        private readonly IDialogService _dialogService;

        public SwarmPublishedPortOptions Options { get; private set; }

        public SwarmPublishedPortDialog(
            IDialogService dialogService,
            SwarmPublishedPortOptions initialOptions)
        {
            InitializeComponent();
            _dialogService = dialogService;
            Options = initialOptions;

            txtPublishedPort.Text = initialOptions.PublishedPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            txtTargetPort.Text = initialOptions.TargetPort.ToString(CultureInfo.InvariantCulture);
            Select(cmbProtocol, initialOptions.Protocol.ToString().ToLowerInvariant());
            Select(cmbPublishMode, initialOptions.PublishMode.ToString().ToLowerInvariant());
            txtPublishedPort.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!SwarmPublishedPortInputParser.TryParse(
                    txtPublishedPort.Text,
                    txtTargetPort.Text,
                    Selected(cmbProtocol),
                    Selected(cmbPublishMode),
                    out SwarmPublishedPortOptions options,
                    out string error))
            {
                _dialogService.ShowError(error, "Published Port");
                return;
            }

            Options = options;
            DialogResult = true;
        }

        private static string Selected(ComboBox comboBox) =>
            (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

        private static void Select(ComboBox comboBox, string value)
        {
            ComboBoxItem? item = comboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Content?.ToString(),
                    value,
                    StringComparison.OrdinalIgnoreCase));
            comboBox.SelectedItem = item ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
    }
}
