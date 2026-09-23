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
        private void OpenLocalKubernetes_Click(object sender, RoutedEventArgs e) => OpenLocalRuntime(RuntimeKind.Kubernetes);
        private async void OpenLocalSwarm_Click(object sender, RoutedEventArgs e) => await OpenValidatedLocalSwarmAsync();
        private void ConfigureLocalSwarm_Click(object sender, RoutedEventArgs e) => OpenLocalSwarmSetup();
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

        private async Task OpenValidatedLocalSwarmAsync()
        {
            var sourceWorkspace = FindLocalDockerWorkspace();
            if (sourceWorkspace == null)
            {
                _dialogService.ShowError("Local Docker workspace is not available.", "New Session");
                return;
            }

            IsEnabled = false;
            StatusText.Text = "Checking local Swarm manager...";

            try
            {
                if (sourceWorkspace.DockerService is not ISwarmService swarmService)
                    throw new InvalidOperationException("The current Docker connection does not provide Swarm APIs.");

                var state = await swarmService.GetSwarmStateAsync();
                if (!state.IsManager)
                {
                    string detail = state.Membership switch
                    {
                        SwarmMembershipState.Worker => "The local Docker Engine is a Swarm worker. Open the cluster through a manager.",
                        SwarmMembershipState.Inactive => "The local Docker Engine has not joined a Swarm.",
                        SwarmMembershipState.Pending => "The local Docker Engine is still joining the Swarm.",
                        SwarmMembershipState.Locked => "The local Swarm is locked and must be unlocked first.",
                        SwarmMembershipState.Error => $"Docker reported a Swarm error: {state.ErrorMessage}",
                        _ => "The local Docker Engine's Swarm role could not be determined."
                    };
                    throw new InvalidOperationException(detail);
                }

                OpenLocalRuntime(RuntimeKind.DockerSwarm);
            }
            catch (Exception ex)
            {
                const string message = "Swarm 세션에는 활성 Manager가 필요합니다. 'Configure Local Swarm...'에서 초기화하거나 SSH로 기존 Manager에 연결해 주세요.";
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

        private void OpenLocalSwarmSetup()
        {
            var sourceWorkspace = FindLocalDockerWorkspace();
            if (sourceWorkspace?.DockerService is not ISwarmService swarmService)
            {
                _dialogService.ShowError("Local Docker workspace is not available.", "Swarm Setup");
                return;
            }

            var dialog = new SwarmSetupDialog(swarmService, _dialogService, _mainVm.DockerServiceFactory) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.WasInitialized)
            {
                OpenLocalRuntime(RuntimeKind.DockerSwarm);
            }
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
