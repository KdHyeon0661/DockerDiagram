using DockerDiagram.ApplicationServices;
using DockerDiagram.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow
    {
        private async void EditSwarmService_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not NodeViewModel node || !node.CanControlSwarmService) return;
            if (sender is Button actionButton) actionButton.IsEnabled = false;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SwarmServiceEditSnapshot snapshot = await node.LoadSwarmServiceEditSnapshotAsync();
                Mouse.OverrideCursor = null;

                var dialog = new Views.SwarmServiceCreateDialog(snapshot.Spec) { Owner = this };
                if (dialog.ShowDialog() == true && dialog.CreateOptions != null)
                    await node.ApplySwarmServiceEditAsync(snapshot, dialog.CreateOptions);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Swarm service 편집 정보를 불러오지 못했습니다:\n{ex.GetBaseException().Message}",
                    "Edit Swarm Service",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                if (sender is Button completedButton) completedButton.IsEnabled = node.CanControlSwarmService;
            }
        }
    }
}

