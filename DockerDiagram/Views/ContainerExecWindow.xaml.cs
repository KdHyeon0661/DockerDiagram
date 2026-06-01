using DockerDiagram.Helpers;
using DockerDiagram.ViewModels;
using System;
using System.Text;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ContainerExecWindow : Window
    {
        private readonly IDockerService _dockerService;
        private readonly NodeViewModel _node;

        public ContainerExecWindow(IDockerService dockerService, NodeViewModel node)
        {
            InitializeComponent();
            _dockerService = dockerService;
            _node = node;

            ContainerTitleText.Text = $"Exec Command - {_node.Name}";
            ContainerIdText.Text = _node.ContainerId;
            CommandBox.Text = "pwd && ls -la";
            CommandBox.Focus();
            CommandBox.SelectAll();
            StatusText.Text = "Ready";
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var command = CommandBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                StatusText.Text = "Command is required.";
                return;
            }

            try
            {
                RunButton.IsEnabled = false;
                StatusText.Text = "Running...";

                var startedAt = DateTime.Now;
                var result = await _dockerService.ExecuteCommandWithOutputAsync(_node.ContainerId, command);
                var elapsed = DateTime.Now - startedAt;

                var output = new StringBuilder();
                output.AppendLine($"> {command}");
                output.AppendLine($"Exit code: {result.ExitCode}");
                output.AppendLine($"Elapsed: {elapsed.TotalSeconds:0.00}s");

                if (!string.IsNullOrWhiteSpace(result.Stdout))
                {
                    output.AppendLine();
                    output.AppendLine("[stdout]");
                    output.AppendLine(result.Stdout.TrimEnd());
                }

                if (!string.IsNullOrWhiteSpace(result.Stderr))
                {
                    output.AppendLine();
                    output.AppendLine("[stderr]");
                    output.AppendLine(result.Stderr.TrimEnd());
                }

                if (string.IsNullOrWhiteSpace(result.Stdout) && string.IsNullOrWhiteSpace(result.Stderr))
                {
                    output.AppendLine();
                    output.AppendLine("(No output)");
                }

                OutputBox.Text = output.ToString();
                StatusText.Text = result.IsSuccess ? "Completed" : "Completed with error";
            }
            catch (Exception ex)
            {
                OutputBox.Text = ex.ToString();
                StatusText.Text = "Failed";
            }
            finally
            {
                RunButton.IsEnabled = true;
            }
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(OutputBox.Text))
            {
                Clipboard.SetText(OutputBox.Text);
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            OutputBox.Clear();
            StatusText.Text = "Ready";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
