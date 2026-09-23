using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Microsoft.Win32;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class SwarmTargetConnectionDialog : Window
    {
        private readonly IDockerServiceFactory _serviceFactory;
        private readonly IDialogService _dialogService;
        private bool _isConnecting;

        public SwarmTargetConnectionDialog(IDockerServiceFactory serviceFactory, IDialogService dialogService)
        {
            InitializeComponent();
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            Closing += (_, e) => e.Cancel = _isConnecting;
        }

        public SwarmTargetConnectionSession? ConnectionSession { get; private set; }

        private void BrowseKey_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "SSH 프라이빗 키 파일 선택",
                Filter = "OpenSSH Key Files (*.pem;*.key)|*.pem;*.key|All Files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) == true)
                KeyPathTextBox.Text = dialog.FileName;
        }

        private async void ValidateConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnecting) return;
            if (!int.TryParse(SshPortTextBox.Text.Trim(), out int sshPort))
            {
                _dialogService.ShowError("SSH 포트는 숫자로 입력해 주세요.", "대상 PC 연결");
                return;
            }

            string socketPath;
            try
            {
                socketPath = SshTunnelManager.NormalizeRemoteDockerSocketPath(RemoteSocketTextBox.Text);
            }
            catch (ArgumentException ex)
            {
                _dialogService.ShowError(ex.Message, "대상 PC 연결");
                return;
            }

            string host = HostTextBox.Text.Trim();
            var options = new SwarmTargetConnectionOptions
            {
                Host = host,
                SshPort = sshPort,
                Username = UsernameTextBox.Text.Trim(),
                SshKeyFilePath = KeyPathTextBox.Text.Trim(),
                RemoteDockerSocketPath = socketPath,
                Role = ManagerRoleRadio.IsChecked == true ? SwarmJoinRole.Manager : SwarmJoinRole.Worker,
                AdvertiseAddress = AdvertiseAddressTextBox.Text.Trim()
            };

            try
            {
                options.Validate();
                if (!File.Exists(options.SshKeyFilePath))
                    throw new FileNotFoundException("SSH 키 파일을 찾을 수 없습니다.", options.SshKeyFilePath);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(ex.Message, "대상 PC 연결");
                return;
            }

            _isConnecting = true;
            ValidateButton.IsEnabled = false;
            StatusText.Text = "SSH 터널 연결 중...";
            IDockerService? dockerService = null;
            bool tunnelAcquired = false;
            bool ownershipTransferred = false;

            try
            {
                int localPort = await SshTunnelManager.GetOrStartTunnelAsync(
                    options.Host,
                    options.SshPort,
                    options.Username,
                    options.SshKeyFilePath,
                    socketPath,
                    _dialogService);
                tunnelAcquired = true;

                var profile = new ConnectionProfile
                {
                    Name = $"Swarm target · {options.Host}",
                    Type = EndpointType.SshRemote,
                    RuntimeKind = RuntimeKind.DockerEngine,
                    HostIp = options.Host,
                    SshUsername = options.Username,
                    SshPort = options.SshPort,
                    LocalTunnelPort = localPort,
                    SshKeyFilePath = options.SshKeyFilePath,
                    RemoteDockerSocketPath = socketPath
                };

                StatusText.Text = "Docker Engine 응답 확인 중...";
                dockerService = _serviceFactory.Create(profile);
                await VerifyDockerConnectionAsync(dockerService);

                StatusText.Text = "Swarm 미가입 상태 확인 중...";
                SwarmClusterState state = await dockerService.GetSwarmStateAsync();
                if (state.Membership != SwarmMembershipState.Inactive)
                    throw new InvalidOperationException(BuildMembershipError(state));

                ConnectionSession = new SwarmTargetConnectionSession(
                    options,
                    profile,
                    dockerService,
                    state,
                    _serviceFactory);

                _dialogService.ShowInfo(
                    $"대상 PC 연결을 검증했습니다.\n\n{options.Role} · {options.Host}\nAdvertise Address: {options.AdvertiseAddress}\nSwarm 상태: 미가입\n\n아직 Join은 실행하지 않았습니다.",
                    "대상 PC 검증 완료");
                _isConnecting = false;
                DialogResult = true;
                ownershipTransferred = true;
            }
            catch (Exception ex)
            {
                string tunnelError = tunnelAcquired
                    ? SshTunnelManager.GetRecentTunnelError(options.Host, options.SshPort, options.Username, socketPath)
                    : string.Empty;
                string detail = string.IsNullOrWhiteSpace(tunnelError)
                    ? ex.GetBaseException().Message
                    : $"{ex.GetBaseException().Message}\n\nSSH: {tunnelError.Trim()}";
                _dialogService.ShowError($"대상 PC 검증 실패:\n{detail}", "대상 PC 연결");
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    ConnectionSession = null;
                    try
                    {
                        if (dockerService != null && !_serviceFactory.Release(dockerService))
                            dockerService.Dispose();
                    }
                    finally
                    {
                        if (tunnelAcquired)
                            SshTunnelManager.ReleaseTunnel(options.Host, options.SshPort, options.Username, socketPath);
                    }
                }

                _isConnecting = false;
                ValidateButton.IsEnabled = true;
                StatusText.Text = string.Empty;
            }
        }

        private static async Task VerifyDockerConnectionAsync(IDockerService dockerService)
        {
            if (dockerService is DockerApiService dockerApiService)
            {
                await dockerApiService.VerifyConnectionAsync();
                return;
            }

            if (!await dockerService.PingAsync())
                throw new InvalidOperationException("Docker Engine이 Ping에 응답하지 않았습니다.");
        }

        private static string BuildMembershipError(SwarmClusterState state) => state.Membership switch
        {
            SwarmMembershipState.Manager => "대상 PC가 이미 Swarm Manager로 가입되어 있습니다.",
            SwarmMembershipState.Worker => "대상 PC가 이미 Swarm Worker로 가입되어 있습니다.",
            SwarmMembershipState.Pending => "대상 PC가 다른 Swarm 참가를 처리 중입니다.",
            SwarmMembershipState.Locked => "대상 PC의 Swarm이 잠겨 있습니다.",
            SwarmMembershipState.Error => $"대상 PC가 Swarm 오류를 보고했습니다: {state.ErrorMessage}",
            _ => "대상 PC의 Swarm 미가입 상태를 확인할 수 없습니다."
        };
    }
}
