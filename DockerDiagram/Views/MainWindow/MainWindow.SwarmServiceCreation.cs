using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        private async Task ShowSwarmServiceDialogAndCreateAsync(
            MainViewModel viewModel,
            double x,
            double y)
        {
            if (viewModel.ActiveSheet?.RuntimeKind != RuntimeKind.DockerSwarm)
            {
                _dialogService.ShowError("Swarm 서비스는 Docker Swarm 시트에서만 생성할 수 있습니다.", "Create Swarm Service");
                return;
            }

            if (viewModel.ActiveSheet.IsRuntimeUnavailable ||
                viewModel.ActiveSheet.DockerService is not ISwarmServiceMutationService)
            {
                _dialogService.ShowError(
                    "서비스를 생성할 수 있는 활성 Swarm Manager 연결이 없습니다.",
                    "Create Swarm Service");
                return;
            }

            IReadOnlyList<SwarmDataResourceSnapshot> secrets =
                viewModel.Explorer.GetSwarmDataResources(SwarmDataResourceKind.Secret);
            IReadOnlyList<SwarmDataResourceSnapshot> configs =
                viewModel.Explorer.GetSwarmDataResources(SwarmDataResourceKind.Config);
            if (viewModel.ActiveSheet.DockerService is ISwarmDataResourceQueryService dataResourceQuery)
            {
                try
                {
                    Task<IReadOnlyList<SwarmDataResourceSnapshot>> secretsTask =
                        dataResourceQuery.GetSwarmDataResourcesAsync(SwarmDataResourceKind.Secret);
                    Task<IReadOnlyList<SwarmDataResourceSnapshot>> configsTask =
                        dataResourceQuery.GetSwarmDataResourcesAsync(SwarmDataResourceKind.Config);
                    await Task.WhenAll(secretsTask, configsTask);
                    secrets = await secretsTask;
                    configs = await configsTask;
                }
                catch (Exception ex)
                {
                    _dialogService.ShowInfo(
                        $"Secret/Config 목록을 최신화하지 못해 마지막 동기화 목록을 사용합니다.\n{ex.GetBaseException().Message}",
                        "Create Swarm Service");
                }
            }

            var dialog = new Views.SwarmServiceCreateDialog(secrets, configs)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true && dialog.CreateOptions != null)
                await viewModel.CreateSwarmServiceNodeAsync(dialog.CreateOptions, x, y);
        }
    }
}
