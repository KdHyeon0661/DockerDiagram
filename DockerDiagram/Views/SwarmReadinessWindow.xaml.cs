using System.Text;
using System.Windows;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    public partial class SwarmReadinessWindow : Window, IDisposable
    {
        private readonly IDockerService _dockerService;
        private readonly SwarmReadinessAuditService _auditService;
        private readonly ApplicationLifetimeCoordinator _lifetime = new();
        private SwarmReadinessReport? _report;
        private bool _disposed;

        public SwarmReadinessWindow(IDockerService dockerService)
        {
            _dockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
            _auditService = new SwarmReadinessAuditService(new DockerCliProbe());
            InitializeComponent();
            Loaded += Window_Loaded;
            Closed += Window_Closed;
            _lifetime.Register(() =>
            {
                Loaded -= Window_Loaded;
                Closed -= Window_Closed;
            });
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            RefreshButton.IsEnabled = false;
            ProgressText.Text = "Checking read-only cluster state...";
            try
            {
                _report = await _auditService.AuditAsync(_dockerService, _lifetime.Token);
                ChecksList.ItemsSource = _report.Checks;
                SummaryText.Text = _report.Summary;
                CheckedAtText.Text = _report.CheckedAt.ToLocalTime().ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture);
                ProgressText.Text = _report.OverallSeverity == SwarmReadinessSeverity.Pass
                    ? "Automated readiness checks passed. Live workload validation is still separate."
                    : "Review warning and blocked checks before changing the cluster.";
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                ProgressText.Text = "취소됨";
            }
            catch (Exception ex)
            {
                ProgressText.Text = $"Readiness audit failed: {ex.GetBaseException().Message}";
            }
            finally
            {
                if (!_lifetime.IsCancellationRequested) RefreshButton.IsEnabled = true;
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null) return;
            var text = new StringBuilder()
                .AppendLine(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Swarm 준비 상태 · {_report.CheckedAt:O}")
                .AppendLine(_report.Summary);
            foreach (SwarmReadinessCheck check in _report.Checks)
                text.AppendLine(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"[{check.StatusText}] {check.Category} / {check.Name}: {check.Detail}");
            Clipboard.SetText(text.ToString());
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closed(object? sender, EventArgs e)
            => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
