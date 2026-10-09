using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        private async Task ShowSwarmDataResourceDialogAsync(
            MainViewModel viewModel,
            RuntimeResourceKind resourceKind,
            double x,
            double y)
        {
            if (viewModel.ActiveSheet?.RuntimeKind != RuntimeKind.DockerSwarm ||
                viewModel.ActiveSheet.IsRuntimeUnavailable ||
                viewModel.ActiveSheet.DockerService is not ISwarmDataResourceMutationService)
            {
                _dialogService.ShowError("Secret/Config를 생성할 수 있는 활성 Swarm Manager 연결이 없습니다.", "Docker Swarm");
                return;
            }

            SwarmDataResourceKind kind = resourceKind == RuntimeResourceKind.SwarmSecret
                ? SwarmDataResourceKind.Secret
                : SwarmDataResourceKind.Config;
            var dialog = new Views.SwarmDataResourceDialog(kind) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.CreateOptions != null)
                await viewModel.CreateSwarmDataResourceNodeAsync(dialog.CreateOptions, x, y);
        }
    }
}
