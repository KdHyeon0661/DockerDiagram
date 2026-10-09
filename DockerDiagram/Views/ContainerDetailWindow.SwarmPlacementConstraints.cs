using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow
    {
        private async void EditSwarmPlacement_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not NodeViewModel node ||
                !node.CanControlSwarmService ||
                node.ParentSheet?.DockerService is not ISwarmPlacementConstraintMutationService placementService)
            {
                return;
            }

            if (sender is Button button) button.IsEnabled = false;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SwarmServicePlacementSnapshot snapshot = await placementService.LoadSwarmServicePlacementAsync(node.ContainerId);
                Mouse.OverrideCursor = null;
                var dialog = new Views.SwarmPlacementConstraintDialog(snapshot) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.UpdateOptions == null) return;
                if (!_dialogService.ShowConfirm(
                        "Placement constraint를 변경하면 실행 중인 service task가 다른 노드로 재배치될 수 있습니다. 계속하시겠습니까?",
                        "Update Service Placement"))
                {
                    return;
                }

                Mouse.OverrideCursor = Cursors.Wait;
                SwarmServiceMutationResult result = await placementService.UpdateSwarmServicePlacementAsync(dialog.UpdateOptions);
                await node.RefreshSwarmServiceAsync();
                string warnings = result.Warnings.Count == 0
                    ? string.Empty
                    : $"\n\n경고:\n{string.Join("\n", result.Warnings)}";
                _dialogService.ShowInfo($"'{node.Name}' service의 placement constraint를 갱신했습니다.{warnings}", "Service Placement Updated");
            }
            catch (SwarmServiceVersionConflictException ex)
            {
                await node.RefreshSwarmServiceAsync();
                _dialogService.ShowError(
                    $"다른 작업에서 service가 먼저 변경되었습니다. 최신 상태를 불러왔으므로 다시 시도해 주세요.\n\n{ex.Message}",
                    "Service Version Conflict");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Placement constraint 갱신 실패:\n{ex.GetBaseException().Message}",
                    "Service Placement");
            }
            finally
            {
                Mouse.OverrideCursor = null;
                if (sender is Button completedButton) completedButton.IsEnabled = node.CanControlSwarmService;
            }
        }
    }
}
