using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    public partial class SwarmTaskDiagnosticsWindow : Window
    {
        private readonly ISwarmTaskDiagnosticService _diagnosticService;
        private readonly IDialogService _dialogService;
        private readonly string _serviceId;
        private readonly string _serviceName;
        private readonly ObservableCollection<SwarmTaskDiagnostic> _tasks = [];
        private bool _isBusy;

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
            if (_isBusy) return;

            try
            {
                SetBusy(true, "Swarm Task 기록을 불러오는 중...");
                SwarmTaskDiagnosticReport report = await _diagnosticService.GetSwarmTaskDiagnosticsAsync(_serviceId);
                _tasks.Clear();
                foreach (SwarmTaskDiagnostic task in report.Tasks) _tasks.Add(task);
                SummaryTextBlock.Text = report.Summary;
                StatusTextBlock.Text = report.FailureCount > 0
                    ? $"{report.FailureCount} current task failure(s) detected. Select a task for details."
                    : "현재 감지된 Task 실패가 없습니다.";
                if (_tasks.Count > 0) TasksListView.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = ex.GetBaseException().Message;
                _dialogService.ShowError($"Swarm Task 진단 조회 실패:\n{ex.GetBaseException().Message}", "Swarm Task 진단");
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
                : "상태와 실패 원인을 확인할 Task를 선택하세요.";
            TaskLogsButton.IsEnabled = !_isBusy && TasksListView.SelectedItem is SwarmTaskDiagnostic;
        }

        private async void ServiceLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            try
            {
                SetBusy(true, "Service 통합 로그를 불러오는 중...");
                LogsHeaderTextBlock.Text = $"Service 로그 · {_serviceName}";
                LogsTextBox.Text = await _diagnosticService.GetSwarmServiceLogsAsync(_serviceId, 500);
                LogsTextBox.ScrollToEnd();
            }
            catch (Exception ex)
            {
                LogsTextBox.Text = ex.GetBaseException().Message;
                _dialogService.ShowError(ex.GetBaseException().Message, "Swarm Service 로그");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void TaskLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (TasksListView.SelectedItem is not SwarmTaskDiagnostic task)
            {
                _dialogService.ShowInfo("로그를 볼 Task를 먼저 선택해 주세요.", "Swarm Task 로그");
                return;
            }

            try
            {
                SetBusy(true, "Task 컨테이너 로그를 불러오는 중...");
                LogsHeaderTextBlock.Text = $"Task 컨테이너 로그 · {task.ShortTaskId} · {task.NodeName}";
                LogsTextBox.Text = await _diagnosticService.GetSwarmTaskContainerLogsAsync(_serviceId, task.TaskId, 500);
                LogsTextBox.ScrollToEnd();
            }
            catch (Exception ex)
            {
                LogsTextBox.Text = ex.GetBaseException().Message;
                _dialogService.ShowError(ex.GetBaseException().Message, "Swarm Task 로그");
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
            _isBusy = busy;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
            RefreshButton.IsEnabled = !busy;
            ServiceLogsButton.IsEnabled = !busy;
            TasksListView.IsEnabled = !busy;
            TaskLogsButton.IsEnabled = !busy && TasksListView.SelectedItem is SwarmTaskDiagnostic;
            if (!string.IsNullOrWhiteSpace(status)) StatusTextBlock.Text = status;
        }
    }
}
