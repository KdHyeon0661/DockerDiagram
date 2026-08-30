using DockerDiagram.Infrastructure;
using DockerDiagram.Contracts;
using System;
using System.Windows;
using Microsoft.Win32;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 사용자로부터 원격 서버의 SSH 접속 정보(IP, 계정, 키 파일 등)를 입력받아, 도커 데몬에 안전하게 연결하기 위한 UI 팝업 창(View) 클래스입니다.
    /// 내부적으로 백그라운드 SSH 터널링(포트 포워딩)을 구축하고, 통신 성공 시 메인 화면에 원격 서버 전용 탭(Sheet)을 새롭게 추가하는 핵심 진입점 역할을 합니다.
    /// </summary>
    public partial class SshConnectionDialog : Window
    {
        private readonly MainViewModel _mainVm;
        private readonly IDialogService _dialogService;
        private readonly RuntimeKind _runtimeKind;

        /// <summary>
        /// 원격 서버 연결 대화상자를 초기화하며, 통신 성공 시 새로운 시트를 추가할 메인 뷰모델과 커스텀 알림을 띄울 다이얼로그 서비스를 주입받습니다.
        /// </summary>
        public SshConnectionDialog(MainViewModel mainVm, IDialogService dialogService, RuntimeKind runtimeKind = RuntimeKind.DockerEngine)
        {
            InitializeComponent();
            _mainVm = mainVm;
            _dialogService = dialogService;
            _runtimeKind = runtimeKind;

            if (_runtimeKind == RuntimeKind.DockerSwarm)
            {
                Title = "Connect to Swarm Manager";
                txtDialogTitle.Text = "Swarm Manager 연결 (SSH)";
                txtName.Text = "Swarm Cluster";
            }
        }

        // 1. SSH 키 파일(.pem, .ppk) 찾아보기
        /// <summary>
        /// 사용자의 로컬 PC 탐색기를 열어 SSH 접속에 필요한 프라이빗 인증 키 파일(.pem, .ppk 등)의 경로를 선택합니다.
        /// </summary>
        private void BtnBrowseKey_Click(object sender, RoutedEventArgs e)
        {
            var openDlg = new OpenFileDialog
            {
                Filter = "SSH Key Files (*.pem;*.ppk)|*.pem;*.ppk|All Files (*.*)|*.*",
                Title = "SSH 프라이빗 키 파일 선택"
            };

            if (openDlg.ShowDialog() == true)
            {
                txtKeyPath.Text = openDlg.FileName;
            }
        }

        // 2. 연결 및 시트 추가 버튼 클릭
        /// <summary>
        /// '연결 및 시트 추가' 버튼 클릭 시 호출됩니다. 
        /// 입력된 접속 정보를 바탕으로 로컬 PC에 SSH 터널을 뚫고, 원격 도커 엔진에 Ping을 보내 생존 여부를 최종 확인한 후,
        /// 성공 시 원격 서버의 데이터를 실시간으로 동기화할 새로운 시트(SheetViewModel)를 메인 뷰모델에 추가합니다.
        /// </summary>
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            // 기초 유효성 검사
            if (string.IsNullOrWhiteSpace(txtIp.Text) || string.IsNullOrWhiteSpace(txtUser.Text) || string.IsNullOrWhiteSpace(txtKeyPath.Text))
            {
                _dialogService.ShowMessage("모든 접속 정보를 입력해 주세요.");
                return;
            }

            if (!int.TryParse(txtPort.Text, out int sshPort)) sshPort = 22;

            string socketPath;
            try
            {
                socketPath = SshTunnelManager.NormalizeRemoteDockerSocketPath(txtRemoteSocketPath.Text);
            }
            catch (ArgumentException ex)
            {
                _dialogService.ShowMessage(ex.Message);
                return;
            }

            // UI 상태 변경 (로딩 표시)
            btnConnect.IsEnabled = false;
            txtStatus.Visibility = Visibility.Visible;
            txtStatus.Text = "SSH 터널 연결 중...";

            string ip = txtIp.Text.Trim();
            string user = txtUser.Text.Trim();
            string keyPath = txtKeyPath.Text.Trim();
            string profileName = txtName.Text.Trim();
            IDockerService? remoteDockerService = null;
            bool tunnelAcquired = false;
            bool connectionHandled = false;
            string failureStage = "SSH 인증 및 터널 생성";

            try
            {
                // 기존 터널이 있으면 로컬 포트를 재사용합니다.
                int localPort = await SshTunnelManager.GetOrStartTunnelAsync(ip, sshPort, user, keyPath, socketPath, _dialogService);
                tunnelAcquired = true;

                // 생성된 터널을 사용하는 연결 프로필
                var remoteProfile = new ConnectionProfile
                {
                    Name = profileName,
                    Type = EndpointType.SshRemote,
                    RuntimeKind = _runtimeKind,
                    HostIp = ip,
                    SshUsername = user,
                    SshPort = sshPort,
                    LocalTunnelPort = localPort,

                    // 다음 실행에서 재연결할 수 있도록 개인 키 경로를 저장합니다.
                    SshKeyFilePath = keyPath,
                    RemoteDockerSocketPath = socketPath
                };

                // 원격 연결 전용 Docker 서비스
                txtStatus.Text = "Docker 소켓 및 권한 확인 중...";
                failureStage = "원격 Docker 소켓 및 권한 확인";
                remoteDockerService = _mainVm.DockerServiceFactory.Create(remoteProfile);

                try
                {
                    if (remoteDockerService is DockerApiService dockerApiService)
                    {
                        await dockerApiService.VerifyConnectionAsync();
                    }
                    else if (!await remoteDockerService.PingAsync())
                    {
                        throw new InvalidOperationException("Docker Engine이 Ping에 응답하지 않았습니다.");
                    }
                }
                catch (Exception pingException)
                {
                    // SSH가 원격 Unix 소켓을 열면서 남긴 오류가 도착할 시간을 잠시 줍니다.
                    await System.Threading.Tasks.Task.Delay(150);
                    string tunnelError = SshTunnelManager.GetRecentTunnelError(ip, sshPort, user, socketPath);
                    failureStage = ClassifyDockerFailureStage(tunnelError);
                    throw new InvalidOperationException(BuildDockerFailureMessage(socketPath, tunnelError, pingException), pingException);
                }

                txtStatus.Text = "Docker Engine 응답 확인 완료";
                failureStage = "Docker Engine 응답 확인";

                if (_runtimeKind == RuntimeKind.DockerSwarm)
                {
                    txtStatus.Text = "Swarm Manager 확인 중...";
                    failureStage = "Swarm Manager 확인";

                    if (remoteDockerService is not ISwarmService swarmService)
                        throw new InvalidOperationException("현재 연결은 Swarm API를 제공하지 않습니다.");

                    var nodes = await swarmService.GetSwarmNodesAsync();
                    if (nodes.Count == 0)
                        throw new InvalidOperationException("Manager에서 Swarm 노드를 조회할 수 없습니다.");
                }

                // 연결된 원격 호스트의 워크스페이스를 추가합니다.
                var workspace = _mainVm.SheetManager.AddWorkspace(
                    remoteProfile,
                    remoteDockerService,
                    activate: true,
                    createInitialSheet: true);
                _mainVm.SheetManager.EnterWorkspace(workspace);
                connectionHandled = true;

                _dialogService.ShowInfo(
                    _runtimeKind == RuntimeKind.DockerSwarm
                        ? $"'{profileName}' Swarm Manager에 연결되었습니다."
                        : $"'{profileName}' 서버에 연결되었습니다.",
                    "연결 성공");
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"연결 실패 단계: {failureStage}\n\n{ex.Message}");
            }
            finally
            {
                if (!connectionHandled)
                {
                    if (remoteDockerService != null && !_mainVm.DockerServiceFactory.Release(remoteDockerService))
                    {
                        remoteDockerService.Dispose();
                    }

                    if (tunnelAcquired)
                        SshTunnelManager.ReleaseTunnel(ip, sshPort, user, socketPath);
                }

                btnConnect.IsEnabled = true;
                txtStatus.Visibility = Visibility.Collapsed;
            }
        }

        private static string ClassifyDockerFailureStage(string tunnelError)
        {
            if (ContainsIgnoreCase(tunnelError, "Permission denied"))
                return "원격 Docker 소켓 권한 확인";

            if (ContainsIgnoreCase(tunnelError, "No such file") ||
                ContainsIgnoreCase(tunnelError, "connect failed"))
                return "원격 Docker 소켓 경로 확인";

            return "Docker Engine 응답 확인";
        }

        private static string BuildDockerFailureMessage(string socketPath, string tunnelError, Exception exception)
        {
            if (ContainsIgnoreCase(tunnelError, "Permission denied"))
            {
                return $"SSH 연결은 성공했지만 계정에 Docker 소켓 접근 권한이 없습니다.\n" +
                       $"소켓: {socketPath}\n" +
                       "원격 계정을 docker 그룹에 추가한 뒤 다시 로그인하거나, 해당 소켓의 소유권/권한을 확인해 주세요.";
            }

            if (ContainsIgnoreCase(tunnelError, "No such file"))
            {
                return $"SSH 연결은 성공했지만 원격 Docker 소켓을 찾을 수 없습니다.\n" +
                       $"입력 경로: {socketPath}\n" +
                       "Docker Engine 실행 여부를 확인하고, Rootless Docker라면 /run/user/사용자ID/docker.sock 경로를 사용해 보세요.";
            }

            if (ContainsIgnoreCase(tunnelError, "connect failed"))
            {
                return $"SSH 연결은 성공했지만 원격 Docker 소켓에 연결하지 못했습니다.\n" +
                       $"소켓: {socketPath}\n" +
                       $"SSH 상세: {tunnelError.Trim()}";
            }

            return $"SSH 터널과 소켓 경로는 설정되었지만 Docker Engine이 API 요청에 응답하지 않았습니다.\n" +
                   $"소켓: {socketPath}\n" +
                   $"상세: {exception.GetBaseException().Message}\n" +
                   "원격 Docker 서비스가 실행 중인지 확인해 주세요.";
        }

        private static bool ContainsIgnoreCase(string value, string expected)
            => value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
}
