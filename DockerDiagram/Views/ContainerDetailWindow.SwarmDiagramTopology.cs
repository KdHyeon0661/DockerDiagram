using DockerDiagram.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow
    {
        private async void ApplySwarmDiagram_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not NodeViewModel node || !node.CanControlSwarmService) return;
            if (sender is Button button) button.IsEnabled = false;

            Cursor? previousCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                await node.ApplySwarmDiagramTopologyWithReferencesAsync();
            }
            finally
            {
                Mouse.OverrideCursor = previousCursor;
                if (sender is Button completedButton) completedButton.IsEnabled = node.CanControlSwarmService;
            }
        }
    }
}
