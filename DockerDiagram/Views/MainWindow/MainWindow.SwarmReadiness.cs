using DockerDiagram.Models;
using System.Windows;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        private void OpenSwarmReadiness_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActiveSheet is not { RuntimeKind: RuntimeKind.DockerSwarm } sheet)
            {
                _dialogService.ShowError("활성 Docker Swarm 시트가 없습니다.", "Swarm Readiness");
                return;
            }

            var readinessWindow = new Views.SwarmReadinessWindow(sheet.DockerService)
            {
                Owner = this
            };
            readinessWindow.ShowDialog();
        }
    }
}
