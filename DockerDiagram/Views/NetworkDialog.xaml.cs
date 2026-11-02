using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    public enum NetworkDialogMode
    {
        DockerNetwork,
        SwarmOverlay
    }

    public partial class NetworkDialog : Window
    {
        private readonly IDialogService _dialogService;
        private readonly NetworkDialogMode _mode;
        private NetworkCreateOptions? _createOptions;

        public string NetworkName => txtName.Text.Trim();

        public string Driver => (cmbDriver.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "bridge";

        public NetworkCreateOptions CreateOptions => _createOptions ??
            throw new InvalidOperationException("네트워크 입력이 아직 검증되지 않았습니다.");

        public NetworkDialog(
            IDialogService dialogService,
            NetworkDialogMode mode = NetworkDialogMode.DockerNetwork)
        {
            InitializeComponent();
            _dialogService = dialogService;
            _mode = mode;

            if (_mode == NetworkDialogMode.SwarmOverlay)
            {
                Title = "Create Swarm Overlay Network";
                overlayDriverItem.Visibility = Visibility.Visible;
                SelectDriver("overlay");
                cmbDriver.IsEnabled = false;
                chkAttachable.IsChecked = true;
                btnCreate.Content = "Create Overlay";
                btnCreate.Width = 110;
                txtComposeName.ToolTip = "For External, enter the existing Docker overlay network name";
            }

            UpdateDriverSpecificOptions();
            txtName.Focus();
        }

        private void SelectDriver(string driver)
        {
            ComboBoxItem? item = cmbDriver.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Content?.ToString(),
                    driver,
                    System.StringComparison.OrdinalIgnoreCase));
            if (item != null) cmbDriver.SelectedItem = item;
        }

        private void Driver_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (spDriverSpecificOptions != null)
                UpdateDriverSpecificOptions();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var input = new NetworkCreateFormInput(
                NetworkName,
                Driver,
                txtSubnet.Text,
                txtGateway.Text,
                txtIpRange.Text,
                chkInternal.IsChecked == true,
                chkAttachable.IsChecked == true,
                chkEnableIPv6.IsChecked == true,
                chkExternal.IsChecked == true,
                txtComposeName.Text,
                txtLabels.Text,
                txtDriverOptions.Text,
                txtAuxAddresses.Text,
                txtParentInterface.Text,
                IsMacvlanOrIpvlan()
                    ? GetComboBoxContent(Driver.Equals("macvlan", System.StringComparison.OrdinalIgnoreCase)
                        ? cmbMacvlanMode
                        : cmbIpvlanMode)
                    : string.Empty);
            FormParseResult<NetworkCreateOptions> result = NetworkCreateFormValidator.Parse(
                input,
                _mode == NetworkDialogMode.SwarmOverlay);
            if (!result.IsValid)
            {
                _dialogService.ShowError(result.Message, "Input Error");
                return;
            }
            _createOptions = result.Value!;

            if (chkExternal.IsChecked == true && HasCreateOnlyOptions())
            {
                _dialogService.ShowInfo(
                    "External networks reference an existing Docker network.\n" +
                    "Creation-only options are stored for Compose export, but Docker network create will not be called.",
                    "External Network");
            }

            DialogResult = true;
        }

        private void UpdateDriverSpecificOptions()
        {
            bool isMacvlan = string.Equals(Driver, "macvlan", System.StringComparison.OrdinalIgnoreCase);
            bool isIpvlan = string.Equals(Driver, "ipvlan", System.StringComparison.OrdinalIgnoreCase);

            spDriverSpecificOptions.Visibility = isMacvlan || isIpvlan ? Visibility.Visible : Visibility.Collapsed;
            gridMacvlanMode.Visibility = isMacvlan ? Visibility.Visible : Visibility.Collapsed;
            gridIpvlanMode.Visibility = isIpvlan ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool IsMacvlanOrIpvlan()
        {
            return string.Equals(Driver, "macvlan", System.StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Driver, "ipvlan", System.StringComparison.OrdinalIgnoreCase);
        }

        private bool HasCreateOnlyOptions()
        {
            return !string.IsNullOrWhiteSpace(txtSubnet.Text) ||
                   !string.IsNullOrWhiteSpace(txtGateway.Text) ||
                   !string.IsNullOrWhiteSpace(txtIpRange.Text) ||
                   !string.IsNullOrWhiteSpace(txtParentInterface.Text) ||
                   chkInternal.IsChecked == true ||
                   chkAttachable.IsChecked == true ||
                   chkEnableIPv6.IsChecked == true ||
                   KeyValueTextParser.ReadEntries(txtAuxAddresses.Text).Count > 0 ||
                   KeyValueTextParser.ReadEntries(txtLabels.Text).Count > 0 ||
                   KeyValueTextParser.ReadEntries(txtDriverOptions.Text).Count > 0;
        }

        private static string GetComboBoxContent(ComboBox comboBox)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        }
    }
}
