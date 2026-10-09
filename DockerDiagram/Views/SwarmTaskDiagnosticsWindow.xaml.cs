using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DockerDiagram.Views
{
    public partial class SwarmTaskDiagnosticsWindow : Window
    {
        private readonly ISwarmTaskDiagnosticService _diagnosticService;
        private readonly IDialogService _dialogService;
        private readonly string _serviceId;
        private readonly string _serviceName;
        private readonly ObservableCollection<SwarmTaskDiagnostic> _tasks = new();

        public SwarmTaskDiagnosticsWindow(
            ISwarmTaskDiagnosticService diagnosticService,
            IDialogService dialogService,
            string serviceId,
            string serviceName)
        {
            InitializeComponent();
            _diagnosticService = diagnosticService;
            _dialogService = dialogService;
            _serviceId = serviceId;
            _serviceName = serviceName;
            TitleTextBlock.Text = $"{serviceName} · Task Diagnostics";
            TasksListView.ItemsSource = _tasks;
            Loaded += async (_, _) => await RefreshDiagnosticsAsync();
        }

        private async Task RefreshDiagnosticsAsync()
        {
            try
            {
                SetBusy(true, "Loading Swarm task history...");
                SwarmTaskDiagnosticReport report = await _diagnosticService.GetSwarmTaskDiagnosticsAsync(_serviceId);
                _tasks.Clear();
                foreach (SwarmTaskDiagnostic task in report.Tasks) _tasks.Add(task);
                SummaryTextBlock.Text = report.Summary;
                StatusTextBlock.Text = report.FailureCount > 0
                    ? $"{report.FailureCount} current task failure(s) detected. Select a task for details."
                    : "No current task failure detected.";
                if (_tasks.Count > 0) TasksListView.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = ex.GetBaseException().Message;
                _dialogService.ShowError($"Swarm task 진단 조회 실패:\n{ex.GetBaseException().Message}", "Swarm Task Diagnostics");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await RefreshDiagnosticsAsync();

        private void TasksListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TaskDetailTextBox.Text = TasksListView.SelectedItem is SwarmTaskDiagnostic task
                ? task.DiagnosticText
                : "Select a task to inspect its state and failure reason.";
        }

        private async void ServiceLogsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true, "Loading aggregated service logs...");
                LogsHeaderTextBlock.Text = $"Service Logs · {_serviceName}";
                LogsTextBox.Text = await _diagnosticService.GetSwarmServiceLogsAsync(_serviceId, 500);
                LogsTextBox.ScrollToEnd();
            }
            catch (Exception ex)
            {
                LogsTextBox.Text = ex.GetBaseException().Message;
                _dialogService.ShowError(ex.GetBaseException().Message, "Swarm Service Logs");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void TaskLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (TasksListView.SelectedItem is not SwarmTaskDiagnostic task)
            {
                _dialogService.ShowInfo("로그를 볼 task를 먼저 선택해 주세요.", "Swarm Task Logs");
                return;
            }

            try
            {
                SetBusy(true, "Loading task container logs...");
                LogsHeaderTextBlock.Text = $"Task Container Logs · {task.ShortTaskId} · {task.NodeName}";
                LogsTextBox.Text = await _diagnosticService.GetSwarmTaskContainerLogsAsync(_serviceId, task.TaskId, 500);
                LogsTextBox.ScrollToEnd();
            }
            catch (Exception ex)
            {
                LogsTextBox.Text = ex.GetBaseException().Message;
                _dialogService.ShowError(ex.GetBaseException().Message, "Swarm Task Logs");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void CopyDiagnosisButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TaskDetailTextBox.Text))
                _dialogService.SetClipboardText(TaskDetailTextBox.Text);
        }

        private void SetBusy(bool busy, string? status = null)
        {
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
            if (!string.IsNullOrWhiteSpace(status)) StatusTextBlock.Text = status;
        }
    }
}
