using DockerDiagram.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DockerDiagram.Views
{
    public enum SwarmNodeManagementAction
    {
        None,
        Update,
        Remove
    }

    public partial class SwarmNodeManagementDialog : Window
    {
        private readonly SwarmNodeEditSnapshot _snapshot;

        public SwarmNodeManagementDialog(SwarmNodeEditSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            InitializeComponent();
            _snapshot = snapshot;
            HostnameTextBlock.Text = snapshot.Hostname;
            NodeIdTextBlock.Text = snapshot.NodeId;
            NodeIdTextBlock.ToolTip = snapshot.NodeId;
            StatusBadgeTextBlock.Text = DisplayName(snapshot.Status, "Unknown");
            RoleBadgeTextBlock.Text = DisplayName(snapshot.Role, "Unknown role");
            ManagerStatusBadgeTextBlock.Text = DisplayName(snapshot.ManagerStatus, string.Empty);
            ManagerStatusBadge.Visibility = string.IsNullOrWhiteSpace(snapshot.ManagerStatus)
                ? Visibility.Collapsed
                : Visibility.Visible;
            RevisionTextBlock.Text = $"Spec revision {snapshot.Version}";
            ConfigureStatusBadge(snapshot.Status);
            SelectTag(RoleComboBox, snapshot.Role);
            SelectTag(AvailabilityComboBox, snapshot.Availability);
            LabelsTextBox.Text = string.Join(
                Environment.NewLine,
                snapshot.Labels.OrderBy(label => label.Key, StringComparer.Ordinal)
                    .Select(label => $"{label.Key}={label.Value}"));
            ConfigureSafetyNotice(snapshot);
            ConfigureRemoveButton(snapshot);
        }

        public SwarmNodeManagementAction RequestedAction { get; private set; }
        public SwarmNodeUpdateOptions? UpdateOptions { get; private set; }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            ClearValidation();
            try
            {
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in LabelsTextBox.Text.Split(
                             new[] { "\r\n", "\n" },
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                        throw new ArgumentException($"Label은 key=value 형식이어야 합니다: {line}");
                    string key = line[..separator].Trim();
                    if (!labels.TryAdd(key, line[(separator + 1)..].Trim()))
                        throw new ArgumentException($"중복된 label key입니다: {key}");
                }

                var options = new SwarmNodeUpdateOptions
                {
                    NodeId = _snapshot.NodeId,
                    Version = _snapshot.Version,
                    Role = SelectedTag(RoleComboBox),
                    Availability = SelectedTag(AvailabilityComboBox),
                    Labels = labels
                };
                options.Validate();
                UpdateOptions = options;
                RequestedAction = SwarmNodeManagementAction.Update;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ShowValidation(ex.Message);
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_snapshot.IsLocalNode)
            {
                ShowValidation("현재 연결에 사용 중인 노드는 여기서 제거할 수 없습니다. Swarm Leave를 사용해 주세요.");
                return;
            }
            if (_snapshot.Role.Equals("manager", StringComparison.OrdinalIgnoreCase))
            {
                ShowValidation("Manager 노드는 먼저 Worker로 강등하고 변경 사항을 적용한 뒤 제거해야 합니다.");
                return;
            }

            RequestedAction = SwarmNodeManagementAction.Remove;
            DialogResult = true;
        }

        private void CopyNodeIdButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_snapshot.NodeId);
                CopyNodeIdButton.Content = "Copied";
                CopyNodeIdButton.ToolTip = "Node ID copied to the clipboard.";
            }
            catch (Exception ex)
            {
                ShowValidation($"Node ID를 복사하지 못했습니다: {ex.GetBaseException().Message}");
            }
        }

        private void ConfigureStatusBadge(string status)
        {
            if (status.Equals("ready", StringComparison.OrdinalIgnoreCase)) return;

            if (status.Equals("down", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
            {
                StatusBadgeBorder.Background = BrushFrom("#FEF3F2");
                StatusBadgeBorder.BorderBrush = BrushFrom("#FECDCA");
                StatusBadgeTextBlock.Foreground = BrushFrom("#B42318");
                return;
            }

            StatusBadgeBorder.Background = BrushFrom("#F2F4F7");
            StatusBadgeBorder.BorderBrush = BrushFrom("#D0D5DD");
            StatusBadgeTextBlock.Foreground = BrushFrom("#475467");
        }

        private void ConfigureSafetyNotice(SwarmNodeEditSnapshot snapshot)
        {
            SafetyNoticeTextBlock.Text = snapshot.IsLocalNode
                ? "This node is used by the current Docker connection. Role or availability changes can move tasks or affect quorum. Removal is unavailable here; use Swarm Leave."
                : snapshot.Role.Equals("manager", StringComparison.OrdinalIgnoreCase)
                    ? "Role or availability changes can move tasks or affect quorum. A Manager must be demoted to Worker before it can be removed. Labels are used by placement constraints."
                    : "Availability changes can move or stop tasks. Labels are used by service placement constraints. Removing a Ready Worker requires force confirmation.";
        }

        private void ConfigureRemoveButton(SwarmNodeEditSnapshot snapshot)
        {
            bool isManager = snapshot.Role.Equals("manager", StringComparison.OrdinalIgnoreCase);
            RemoveNodeButton.IsEnabled = !snapshot.IsLocalNode && !isManager;
            RemoveNodeButton.ToolTip = snapshot.IsLocalNode
                ? "Use Swarm Leave to remove the node used by the current connection."
                : isManager
                    ? "Demote this Manager to Worker and apply the change before removing it."
                    : "Remove this Worker from the Swarm.";
        }

        private void ShowValidation(string message)
        {
            ValidationTextBlock.Text = message;
            ValidationBorder.Visibility = Visibility.Visible;
        }

        private void ClearValidation()
        {
            ValidationTextBlock.Text = string.Empty;
            ValidationBorder.Visibility = Visibility.Collapsed;
        }

        private static string DisplayName(string value, string fallback)
        {
            string normalized = value?.Trim() ?? string.Empty;
            return normalized.Length == 0
                ? fallback
                : char.ToUpperInvariant(normalized[0]) + normalized[1..];
        }

        private static SolidColorBrush BrushFrom(string value) =>
            (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;

        private static string SelectedTag(ComboBox comboBox) =>
            (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

        private static void SelectTag(ComboBox comboBox, string value)
        {
            comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase));
            if (comboBox.SelectedItem == null && comboBox.Items.Count > 0)
                comboBox.SelectedIndex = 0;
        }
    }
}
