using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DockerDiagram.Views
{
    public partial class NetworkDialog : Window
    {
        private readonly IDialogService _dialogService;

        public string NetworkName => txtName.Text.Trim();

        public string Driver => (cmbDriver.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "bridge";

        public NetworkCreateOptions CreateOptions => new NetworkCreateOptions
        {
            Name = NetworkName,
            Driver = Driver,
            Subnet = txtSubnet.Text.Trim(),
            Gateway = txtGateway.Text.Trim(),
            IpRange = txtIpRange.Text.Trim(),
            Internal = chkInternal.IsChecked == true,
            Attachable = chkAttachable.IsChecked == true,
            EnableIPv6 = chkEnableIPv6.IsChecked == true,
            External = chkExternal.IsChecked == true,
            ComposeNetworkName = txtComposeName.Text.Trim(),
            Labels = ParseKeyValueLines(txtLabels.Text),
            DriverOptions = BuildDriverOptions(),
            AuxAddresses = ParseKeyValueLines(txtAuxAddresses.Text)
        };

        public NetworkDialog(IDialogService dialogService)
        {
            InitializeComponent();
            _dialogService = dialogService;

            UpdateDriverSpecificOptions();
            txtName.Focus();
        }

        private void Driver_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (spDriverSpecificOptions != null)
                UpdateDriverSpecificOptions();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NetworkName))
            {
                _dialogService.ShowError("Enter a network name.", "Input Error");
                return;
            }

            if (!ValidateKeyValueLines(txtAuxAddresses.Text, "Aux Addresses")) return;
            if (!ValidateKeyValueLines(txtLabels.Text, "Labels")) return;
            if (!ValidateKeyValueLines(txtDriverOptions.Text, "Driver Options")) return;

            if (IsMacvlanOrIpvlan() &&
                chkExternal.IsChecked != true &&
                string.IsNullOrWhiteSpace(txtParentInterface.Text))
            {
                _dialogService.ShowError($"{Driver} requires a parent interface, e.g. eth0.", "Input Error");
                return;
            }

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
                   GetKeyValueLines(txtAuxAddresses.Text).Any() ||
                   GetKeyValueLines(txtLabels.Text).Any() ||
                   GetKeyValueLines(txtDriverOptions.Text).Any();
        }

        private Dictionary<string, string> BuildDriverOptions()
        {
            var options = ParseKeyValueLines(txtDriverOptions.Text);

            if (IsMacvlanOrIpvlan())
            {
                var parent = txtParentInterface.Text.Trim();
                if (!string.IsNullOrWhiteSpace(parent))
                    options["parent"] = parent;

                if (string.Equals(Driver, "macvlan", System.StringComparison.OrdinalIgnoreCase))
                {
                    var mode = GetComboBoxContent(cmbMacvlanMode);
                    if (!string.IsNullOrWhiteSpace(mode))
                        options["macvlan_mode"] = mode;
                }
                else if (string.Equals(Driver, "ipvlan", System.StringComparison.OrdinalIgnoreCase))
                {
                    var mode = GetComboBoxContent(cmbIpvlanMode);
                    if (!string.IsNullOrWhiteSpace(mode))
                        options["ipvlan_mode"] = mode;
                }
            }

            return options;
        }

        private bool ValidateKeyValueLines(string text, string fieldName)
        {
            var invalidLine = GetKeyValueLines(text).FirstOrDefault(line => !line.Contains('=') || line.StartsWith("="));
            if (invalidLine == null) return true;

            _dialogService.ShowError($"{fieldName} must use key=value format.\n\nInvalid line: {invalidLine}", "Input Error");
            return false;
        }

        private static Dictionary<string, string> ParseKeyValueLines(string text)
        {
            return GetKeyValueLines(text)
                .Select(line => line.Split(new[] { '=' }, 2))
                .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                .GroupBy(parts => parts[0].Trim())
                .ToDictionary(group => group.Key, group => group.Last()[1].Trim());
        }

        private static IEnumerable<string> GetKeyValueLines(string text)
        {
            return (text ?? "")
                .Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line));
        }

        private static string GetComboBoxContent(ComboBox comboBox)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        }
    }
}
