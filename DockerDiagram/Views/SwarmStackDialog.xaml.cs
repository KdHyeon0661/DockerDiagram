using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class SwarmStackDialog : Window
    {
        private const string SampleYaml = "services:\n  web:\n    image: nginx:alpine\n    deploy:\n      replicas: 1\n";
        private readonly HashSet<string> _reservedStackNames;

        public bool DeployRequested { get; private set; }
        public SwarmStackDeploymentOptions Options { get; private set; } = new();

        public SwarmStackDialog(
            SwarmStackSheetState state,
            IEnumerable<string>? reservedStackNames = null)
        {
            ArgumentNullException.ThrowIfNull(state);
            _reservedStackNames = new HashSet<string>(
                reservedStackNames ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            InitializeComponent();
            StackNameTextBox.Text = state.StackName;
            SourcePathTextBox.Text = state.SourcePath;
            YamlTextBox.Text = string.IsNullOrWhiteSpace(state.Yaml) ? SampleYaml : state.Yaml;
            StateText.Text = $"State: {state.DeployState}  {state.RuntimeSummary}";
            UpdatePreview();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Swarm Stack YAML 선택",
                Filter = "YAML files (*.yml;*.yaml)|*.yml;*.yaml|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                SourcePathTextBox.Text = dialog.FileName;
                YamlTextBox.Text = File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.GetBaseException().Message, "YAML 읽기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

        private void UpdatePreview()
        {
            if (CommandPreviewText == null || StackNameTextBox == null) return;
            string name = StackNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                CommandPreviewText.Text = "docker stack deploy ...";
                return;
            }

            var options = BuildOptions();
            try
            {
                CommandPreviewText.Text = SwarmStackCommandBuilder.Preview(
                    SwarmStackCommandBuilder.BuildDeployArguments(options, "<temporary-stack.yml>"));
            }
            catch (ArgumentException)
            {
                CommandPreviewText.Text = "Invalid stack name";
            }
        }

        private SwarmStackDeploymentOptions BuildOptions() => new()
        {
            StackName = StackNameTextBox.Text.Trim(),
            SourcePath = SourcePathTextBox.Text.Trim(),
            Yaml = YamlTextBox.Text,
            Prune = PruneCheckBox.IsChecked == true,
            WithRegistryAuth = RegistryAuthCheckBox.IsChecked == true,
            ResolveImage = "always"
        };

        private bool TryAccept(bool deploy)
        {
            SwarmStackDeploymentOptions options = BuildOptions();
            try
            {
                SwarmStackNamePolicy.Validate(options.StackName);
                if (!SwarmStackIdentityPolicy.IsUnique(options.StackName, _reservedStackNames))
                    throw new ArgumentException(SwarmStackIdentityPolicy.DuplicateMessage(options.StackName));
                if (deploy) options.Validate();
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(this, ex.Message, "Swarm Stack", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            Options = options;
            DeployRequested = deploy;
            DialogResult = true;
            return true;
        }

        private void Save_Click(object sender, RoutedEventArgs e) => TryAccept(deploy: false);
        private void Deploy_Click(object sender, RoutedEventArgs e) => TryAccept(deploy: true);
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}

