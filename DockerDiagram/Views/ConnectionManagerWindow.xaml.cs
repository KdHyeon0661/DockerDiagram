using DockerDiagram.Infrastructure;
using DockerDiagram.Contracts;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Views
{
    public partial class ConnectionManagerWindow : Window
    {
        private readonly MainViewModel _mainVm;
        private readonly IDialogService _dialogService;
        private readonly DockerContextService _dockerContextService = new();
        private readonly ObservableCollection<DockerContextInfo> _dockerContexts = new();

        public ConnectionManagerWindow(MainViewModel mainVm, IDialogService dialogService)
        {
            InitializeComponent();
            _mainVm = mainVm;
            _dialogService = dialogService;
            ConnectionGrid.ItemsSource = _mainVm.Workspaces;
            ConnectionGrid.SelectedItem = _mainVm.SheetManager.ActiveWorkspace;
            DockerContextGrid.ItemsSource = _dockerContexts;
            SyncNameBox();
            Loaded += async (_, _) => await RefreshDockerContextsAsync();
        }

        private ConnectionWorkspaceViewModel? SelectedWorkspace => ConnectionGrid.SelectedItem as ConnectionWorkspaceViewModel;
        private DockerContextInfo? SelectedContext => DockerContextGrid.SelectedItem as DockerContextInfo;

        private void ConnectionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncNameBox();
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            var workspace = SelectedWorkspace;
            if (workspace == null) return;

            string newName = txtConnectionName.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName)) return;

            _mainVm.SheetManager.RenameWorkspace(workspace, newName);
            ConnectionGrid.Items.Refresh();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var workspace = SelectedWorkspace;
            if (workspace == null) return;

            if (workspace.Profile.Type == EndpointType.Local)
            {
                _dialogService.ShowMessage("Local PC 연결은 삭제할 수 없습니다.");
                return;
            }

            if (!_dialogService.ShowConfirm($"'{workspace.DisplayName}' 연결을 삭제하시겠습니까?\n이 연결 안의 시트도 목록에서 제거됩니다.", "Connection Delete"))
                return;

            if (_mainVm.SheetManager.RemoveWorkspace(workspace))
            {
                ConnectionGrid.Items.Refresh();
                ConnectionGrid.SelectedItem = _mainVm.SheetManager.ActiveWorkspace;
                SyncNameBox();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void RefreshContexts_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDockerContextsAsync();
        }

        private async void UseContext_Click(object sender, RoutedEventArgs e)
        {
            var context = SelectedContext;
            if (context == null)
            {
                _dialogService.ShowInfo("사용할 Docker context를 선택해 주세요.", "Docker Context");
                return;
            }

            try
            {
                await _dockerContextService.UseContextAsync(context.Name);
                await RefreshDockerContextsAsync();
                _dialogService.ShowInfo($"현재 Docker context를 '{context.Name}'(으)로 변경했습니다.", "Docker Context");
            }
            catch (System.Exception ex)
            {
                _dialogService.ShowError($"Docker context 변경 실패:\n{ex.Message}", "Docker Context");
            }
        }

        private async void CreateContext_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new DockerContextCreateDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;

            try
            {
                await _dockerContextService.CreateContextAsync(dialog.ContextName, dialog.DockerEndpoint);
                await RefreshDockerContextsAsync();
                _dialogService.ShowInfo($"Docker context '{dialog.ContextName}'을(를) 생성했습니다.", "Docker Context");
            }
            catch (System.Exception ex)
            {
                _dialogService.ShowError($"Docker context 생성 실패:\n{ex.Message}", "Docker Context");
            }
        }

        private async void RemoveContext_Click(object sender, RoutedEventArgs e)
        {
            var context = SelectedContext;
            if (context == null)
            {
                _dialogService.ShowInfo("삭제할 Docker context를 선택해 주세요.", "Docker Context");
                return;
            }

            if (context.IsDefault)
            {
                _dialogService.ShowInfo("default context는 삭제할 수 없습니다.", "Docker Context");
                return;
            }

            if (context.IsCurrent)
            {
                _dialogService.ShowInfo("현재 사용 중인 context는 삭제할 수 없습니다. 다른 context로 Use 한 뒤 삭제해 주세요.", "Docker Context");
                return;
            }

            if (!_dialogService.ShowConfirm($"Docker context '{context.Name}'을(를) 삭제하시겠습니까?", "Docker Context"))
            {
                return;
            }

            try
            {
                await _dockerContextService.RemoveContextAsync(context.Name);
                await RefreshDockerContextsAsync();
                _dialogService.ShowInfo($"Docker context '{context.Name}'을(를) 삭제했습니다.", "Docker Context");
            }
            catch (System.Exception ex)
            {
                _dialogService.ShowError($"Docker context 삭제 실패:\n{ex.Message}", "Docker Context");
            }
        }

        private async void OpenContextWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var context = SelectedContext;
            if (context == null)
            {
                _dialogService.ShowInfo("워크스페이스로 열 Docker context를 선택해 주세요.", "Docker Context");
                return;
            }

            if (string.IsNullOrWhiteSpace(context.DockerEndpoint))
            {
                _dialogService.ShowInfo("선택한 context에 Docker endpoint 정보가 없습니다.", "Docker Context");
                return;
            }

            ConnectionProfile? profile = null;
            IDockerService? service = null;
            bool connectionHandled = false;

            try
            {
                DockerContextStatusText.Text = $"Opening workspace for {context.Name}...";
                profile = await CreateProfileFromContextAsync(context);
                service = _mainVm.DockerServiceFactory.Create(profile);

                if (!await service.PingAsync())
                {
                    _dialogService.ShowError($"'{context.Name}' context의 Docker 엔진에 연결할 수 없습니다.", "Docker Context");
                    return;
                }

                _mainVm.SheetManager.AddWorkspace(profile, service, activate: true, createInitialSheet: true);
                connectionHandled = true;
                ConnectionGrid.Items.Refresh();
                DockerContextStatusText.Text = $"Workspace opened: {context.Name}";
                _dialogService.ShowInfo($"'{context.Name}' context를 앱 워크스페이스로 열었습니다.", "Docker Context");
            }
            catch (Exception ex)
            {
                DockerContextStatusText.Text = "Open workspace failed.";
                _dialogService.ShowError($"Docker context 워크스페이스 열기 실패:\n{ex.Message}", "Docker Context");
            }
            finally
            {
                if (!connectionHandled && profile != null)
                {
                    CleanupFailedContextService(profile, service);
                }
            }
        }

        private async System.Threading.Tasks.Task RefreshDockerContextsAsync()
        {
            try
            {
                DockerContextStatusText.Text = "Loading Docker contexts...";
                var contexts = await _dockerContextService.ListContextsAsync();

                _dockerContexts.Clear();
                foreach (var context in contexts)
                {
                    _dockerContexts.Add(context);
                }

                DockerContextStatusText.Text = $"{_dockerContexts.Count} Docker context(s) loaded.";
            }
            catch (System.Exception ex)
            {
                _dockerContexts.Clear();
                DockerContextStatusText.Text = "Docker context list failed.";
                _dialogService.ShowError($"Docker context 목록 조회 실패:\n{ex.Message}", "Docker Context");
            }
        }

        private async System.Threading.Tasks.Task<ConnectionProfile> CreateProfileFromContextAsync(DockerContextInfo context)
        {
            var endpoint = context.DockerEndpoint.Trim();
            var endpointUri = new Uri(endpoint);

            if (endpointUri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                var keyPath = _dialogService.ShowOpenFileDialog(
                    "SSH Key Files (*.pem;*.ppk)|*.pem;*.ppk|All Files (*.*)|*.*",
                    $"SSH key for Docker context '{context.Name}'");

                if (string.IsNullOrWhiteSpace(keyPath))
                {
                    throw new InvalidOperationException("SSH key file selection was cancelled.");
                }

                var username = string.IsNullOrWhiteSpace(endpointUri.UserInfo) ? "root" : endpointUri.UserInfo;
                var host = endpointUri.Host;
                var sshPort = endpointUri.Port > 0 ? endpointUri.Port : 22;
                var remoteSocketPath = endpointUri.AbsolutePath.Length > 1
                    ? Uri.UnescapeDataString(endpointUri.AbsolutePath)
                    : SshTunnelManager.DefaultRemoteDockerSocketPath;
                var localPort = await SshTunnelManager.GetOrStartTunnelAsync(
                    host, sshPort, username, keyPath, remoteSocketPath, _dialogService);

                return new ConnectionProfile
                {
                    Name = $"Context: {context.Name}",
                    Type = EndpointType.SshRemote,
                    HostIp = host,
                    SshUsername = username,
                    SshPort = sshPort,
                    LocalTunnelPort = localPort,
                    SshKeyFilePath = keyPath,
                    RemoteDockerSocketPath = remoteSocketPath
                };
            }

            if (endpointUri.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase) ||
                endpointUri.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase) ||
                endpointUri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
                endpointUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                endpointUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                return new ConnectionProfile
                {
                    Name = $"Context: {context.Name}",
                    Type = EndpointType.DockerContext,
                    DockerEndpoint = endpoint
                };
            }

            throw new NotSupportedException($"지원하지 않는 Docker context endpoint입니다: {endpointUri.Scheme}");
        }

        private void CleanupFailedContextService(ConnectionProfile profile, IDockerService? service)
        {
            if (profile.Type == EndpointType.SshRemote && !string.IsNullOrWhiteSpace(profile.HostIp))
            {
                SshTunnelManager.ReleaseTunnel(
                    profile.HostIp,
                    profile.SshPort,
                    profile.SshUsername ?? "root",
                    profile.RemoteDockerSocketPath);
            }

            if (service != null && !_mainVm.DockerServiceFactory.Release(service))
            {
                service.Dispose();
            }
        }

        private void SyncNameBox()
        {
            txtConnectionName.Text = SelectedWorkspace?.DisplayName ?? "";
        }
    }
}
