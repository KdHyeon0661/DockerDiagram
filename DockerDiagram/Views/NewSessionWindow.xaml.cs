using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class NewSessionWindow : Window
    {
        private readonly MainViewModel _mainVm;
        private readonly IDialogService _dialogService;

        public NewSessionWindow(MainViewModel mainVm, IDialogService dialogService)
        {
            InitializeComponent();
            _mainVm = mainVm;
            _dialogService = dialogService;
        }

        private void OpenLocalDocker_Click(object sender, RoutedEventArgs e) => OpenLocalRuntime(RuntimeKind.DockerEngine);
        private async void OpenLocalSwarm_Click(object sender, RoutedEventArgs e) => await OpenValidatedLocalRuntimeAsync(RuntimeKind.DockerSwarm);
        private async void OpenKubernetes_Click(object sender, RoutedEventArgs e) => await OpenValidatedLocalRuntimeAsync(RuntimeKind.Kubernetes);
        private void OpenRemoteDocker_Click(object sender, RoutedEventArgs e) => OpenSshSession(RuntimeKind.DockerEngine);
        private void OpenRemoteSwarm_Click(object sender, RoutedEventArgs e) => OpenSshSession(RuntimeKind.DockerSwarm);

        private void OpenLocalRuntime(RuntimeKind runtimeKind)
        {
            var sourceWorkspace = FindLocalDockerWorkspace();
            if (sourceWorkspace == null)
            {
                _dialogService.ShowError("Local Docker workspace is not available.", "New Session");
                return;
            }

            var workspace = _mainVm.SheetManager.CreateRuntimeWorkspace(sourceWorkspace, runtimeKind, activate: true);
            _mainVm.SheetManager.EnterWorkspace(workspace);
            DialogResult = true;
        }

        private async Task OpenValidatedLocalRuntimeAsync(RuntimeKind runtimeKind)
        {
            var sourceWorkspace = FindLocalDockerWorkspace();
            if (sourceWorkspace == null)
            {
                _dialogService.ShowError("Local Docker workspace is not available.", "New Session");
                return;
            }

            IsEnabled = false;
            StatusText.Text = runtimeKind == RuntimeKind.DockerSwarm
                ? "Checking local Swarm manager..."
                : "Checking the current Kubernetes context...";

            try
            {
                if (runtimeKind == RuntimeKind.DockerSwarm)
                {
                    if (sourceWorkspace.DockerService is not ISwarmService swarmService)
                        throw new InvalidOperationException("The current Docker connection does not provide Swarm APIs.");

                    var nodes = await swarmService.GetSwarmNodesAsync();
                    if (nodes.Count == 0)
                        throw new InvalidOperationException("No Swarm nodes were returned by the manager.");
                }
                else if (runtimeKind == RuntimeKind.Kubernetes)
                {
                    if (sourceWorkspace.DockerService is not IKubernetesService kubernetesService)
                        throw new InvalidOperationException("The current connection does not provide Kubernetes APIs.");

                    await kubernetesService.GetKubernetesNodesAsync();
                }

                OpenLocalRuntime(runtimeKind);
            }
            catch (Exception ex)
            {
                string message = runtimeKind == RuntimeKind.DockerSwarm
                    ? "A Swarm session requires an active manager. Run 'docker swarm init' locally or connect to a remote manager."
                    : "The current kubeconfig context could not be opened. Check kubectl and the active context.";
                _dialogService.ShowError($"{message}\n\n{ex.GetBaseException().Message}", "New Session");
            }
            finally
            {
                IsEnabled = true;
                StatusText.Text = string.Empty;
            }
        }

        private void OpenSshSession(RuntimeKind runtimeKind)
        {
            var dialog = new SshConnectionDialog(_mainVm, _dialogService, runtimeKind) { Owner = this };
            if (dialog.ShowDialog() == true) DialogResult = true;
        }

        private void ManageConnections_Click(object sender, RoutedEventArgs e)
        {
            var window = new ConnectionManagerWindow(_mainVm, _dialogService) { Owner = this };
            window.ShowDialog();
        }

        private ConnectionWorkspaceViewModel? FindLocalDockerWorkspace()
            => _mainVm.Workspaces.FirstOrDefault(workspace => workspace.Profile.Type == EndpointType.Local && workspace.RuntimeKind == RuntimeKind.DockerEngine)
               ?? _mainVm.Workspaces.FirstOrDefault(workspace => workspace.Profile.Type == EndpointType.Local);
    }
}
