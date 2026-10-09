using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;
using System.Windows;
using System.Windows.Controls;

namespace DockerDiagram.Views
{
    public partial class SwarmServiceCreateDialog : Window
    {
        private IReadOnlyList<SwarmDataResourceSnapshot> _availableSecrets = Array.Empty<SwarmDataResourceSnapshot>();
        private IReadOnlyList<SwarmDataResourceSnapshot> _availableConfigs = Array.Empty<SwarmDataResourceSnapshot>();
        public SwarmServiceCreateOptions? CreateOptions { get; private set; }

        public SwarmServiceCreateDialog()
        {
            InitializeComponent();
        }

        public SwarmServiceCreateDialog(
            IReadOnlyList<SwarmDataResourceSnapshot> availableSecrets,
            IReadOnlyList<SwarmDataResourceSnapshot> availableConfigs) : this()
        {
            _availableSecrets = availableSecrets ?? Array.Empty<SwarmDataResourceSnapshot>();
            _availableConfigs = availableConfigs ?? Array.Empty<SwarmDataResourceSnapshot>();
            AvailableSecretsTextBlock.Text = FormatAvailable("Available", _availableSecrets);
            AvailableConfigsTextBlock.Text = FormatAvailable("Available", _availableConfigs);
        }

        private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReplicasTextBox == null) return;
            bool isGlobal = SelectedTag(ModeComboBox).Equals("global", StringComparison.OrdinalIgnoreCase);
            ReplicasTextBox.IsEnabled = !isGlobal;
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SwarmServiceCreateOptions parsed = SwarmServiceCreateInputParser.Parse(new SwarmServiceCreateFormInput
                {
                    Name = ServiceNameTextBox.Text,
                    Image = ImageTextBox.Text,
                    Mode = SelectedTag(ModeComboBox),
                    Replicas = ReplicasTextBox.Text,
                    Command = CommandTextBox.Text,
                    Arguments = ArgumentsTextBox.Text,
                    EnvironmentVariables = EnvironmentTextBox.Text,
                    Labels = LabelsTextBox.Text,
                    PublishedPorts = PortsTextBox.Text,
                    Networks = NetworksTextBox.Text,
                    Mounts = MountsTextBox.Text,
                    RestartCondition = SelectedTag(RestartConditionComboBox),
                    RestartDelaySeconds = RestartDelayTextBox.Text,
                    RestartMaxAttempts = RestartMaxAttemptsTextBox.Text,
                    RestartWindowSeconds = RestartWindowTextBox.Text,
                    UpdateParallelism = UpdateParallelismTextBox.Text,
                    UpdateDelaySeconds = UpdateDelayTextBox.Text,
                    UpdateMonitorSeconds = UpdateMonitorTextBox.Text,
                    UpdateMaxFailureRatio = UpdateRatioTextBox.Text,
                    UpdateFailureAction = SelectedTag(UpdateActionComboBox),
                    UpdateOrder = SelectedTag(UpdateOrderComboBox),
                    RollbackParallelism = RollbackParallelismTextBox.Text,
                    RollbackDelaySeconds = RollbackDelayTextBox.Text,
                    RollbackMonitorSeconds = RollbackMonitorTextBox.Text,
                    RollbackMaxFailureRatio = RollbackRatioTextBox.Text,
                    RollbackFailureAction = SelectedTag(RollbackActionComboBox),
                    RollbackOrder = SelectedTag(RollbackOrderComboBox)
                });
                CreateOptions = new SwarmServiceCreateOptions
                {
                    Spec = parsed.Spec,
                    PlacementConstraints = ParseLines(PlacementConstraintsTextBox.Text),
                    Secrets = ParseReferences(SecretsTextBox.Text, SwarmDataResourceKind.Secret, _availableSecrets),
                    Configs = ParseReferences(ConfigsTextBox.Text, SwarmDataResourceKind.Config, _availableConfigs)
                };
                CreateOptions.Validate();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ValidationMessageTextBlock.Text = ex.Message;
            }
        }

        private static string SelectedTag(ComboBox comboBox) =>
            (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

        private static IReadOnlyList<string> ParseLines(string text) =>
            (text ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        private static IReadOnlyList<SwarmServiceResourceReferenceOptions> ParseReferences(
            string text,
            SwarmDataResourceKind kind,
            IReadOnlyList<SwarmDataResourceSnapshot> available)
        {
            var result = new List<SwarmServiceResourceReferenceOptions>();
            foreach (string line in ParseLines(text))
            {
                string[] parts = line.Split('|');
                if (parts.Length > 5)
                    throw new ArgumentException($"{kind} 입력 형식이 올바르지 않습니다: {line}");
                string identity = parts[0].Trim();
                SwarmDataResourceSnapshot? resource = available.FirstOrDefault(candidate =>
                    candidate.Kind == kind &&
                    (candidate.Name.Equals(identity, StringComparison.OrdinalIgnoreCase) ||
                     candidate.Id.Equals(identity, StringComparison.OrdinalIgnoreCase)));
                if (resource == null)
                    throw new ArgumentException($"현재 Swarm에서 {kind} '{identity}'을(를) 찾을 수 없습니다.");

                string target = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : resource.Name;
                string uid = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : "0";
                string gid = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : "0";
                string mode = parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]) ? parts[4] : "0444";
                SwarmResourceTargetOptions targetOptions = SwarmResourceReferenceInputParser.Parse(target, uid, gid, mode);
                result.Add(new SwarmServiceResourceReferenceOptions(
                    resource.Id,
                    resource.Name,
                    targetOptions.FileName,
                    targetOptions.Uid,
                    targetOptions.Gid,
                    targetOptions.Mode));
            }
            return result;
        }

        private static string FormatAvailable(string prefix, IReadOnlyList<SwarmDataResourceSnapshot> resources) =>
            resources.Count == 0
                ? $"{prefix}: none"
                : $"{prefix}: {string.Join(", ", resources.Select(resource => resource.Name))}";
    }
}
