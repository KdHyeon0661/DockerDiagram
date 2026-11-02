using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Views
{
    public partial class ContainerExecWindow : Window
    {
        private readonly Func<string, Task<ExecCommandResult>> _executeCommand;

        public ContainerExecWindow(
            string containerName,
            string containerId,
            Func<string, Task<ExecCommandResult>> executeCommand)
        {
            InitializeComponent();
            _executeCommand = executeCommand ?? throw new ArgumentNullException(nameof(executeCommand));

            ContainerTitleText.Text = $"Exec Command - {containerName}";
            ContainerIdText.Text = containerId;
            CommandBox.Text = "pwd && ls -la";
            CommandBox.Focus();
            CommandBox.SelectAll();
            StatusText.Text = "준비";
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var command = CommandBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                StatusText.Text = "명령을 입력해 주세요.";
                return;
            }

            try
            {
                RunButton.IsEnabled = false;
                StatusText.Text = "실행 중...";

                var startedAt = DateTime.Now;
                var result = await _executeCommand(command);
                var elapsed = DateTime.Now - startedAt;

                var output = new StringBuilder();
                output.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"> {command}");
                output.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Exit code: {result.ExitCode}");
                output.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Elapsed: {elapsed.TotalSeconds:0.00}s");

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
                StatusText.Text = result.IsSuccess ? "완료" : "오류와 함께 완료";
            }
            catch (Exception ex)
            {
                OutputBox.Text = ex.ToString();
                StatusText.Text = "실패";
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
            StatusText.Text = "준비";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
