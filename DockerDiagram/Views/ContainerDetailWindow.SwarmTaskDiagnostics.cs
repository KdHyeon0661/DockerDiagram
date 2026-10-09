using DockerDiagram.Contracts;
using DockerDiagram.ViewModels;
using System.Windows;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow
    {
        private void OpenSwarmTaskDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not NodeViewModel node ||
                !node.CanControlSwarmService ||
                node.ParentSheet?.DockerService is not ISwarmTaskDiagnosticService diagnosticService)
            {
                return;
            }

            var window = new Views.SwarmTaskDiagnosticsWindow(
                diagnosticService,
                _dialogService,
                node.ContainerId,
                node.Name)
            {
                Owner = this
            };
            window.ShowDialog();
        }
    }
}
