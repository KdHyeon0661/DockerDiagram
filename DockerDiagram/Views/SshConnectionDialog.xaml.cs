using System;
using System.Windows;
using Microsoft.Win32;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Views
{
    public partial class SshConnectionDialog : Window
    {
        private readonly MainViewModel _mainVm;
        private readonly IDialogService _dialogService;

        public SshConnectionDialog(MainViewModel mainVm, IDialogService dialogService)
        {
            InitializeComponent();
            _mainVm = mainVm;
            _dialogService = dialogService;
        }

        // 1. SSH 키 파일(.pem, .ppk) 찾아보기
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
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            // 기초 유효성 검사
            if (string.IsNullOrWhiteSpace(txtIp.Text) || string.IsNullOrWhiteSpace(txtUser.Text) || string.IsNullOrWhiteSpace(txtKeyPath.Text))
            {
                _dialogService.ShowMessage("모든 접속 정보를 입력해 주세요.");
                return;
            }

            if (!int.TryParse(txtPort.Text, out int sshPort)) sshPort = 22;

            // UI 상태 변경 (로딩 표시)
            btnConnect.IsEnabled = false;
            txtStatus.Visibility = Visibility.Visible;
            txtStatus.Text = "SSH 터널 연결 중...";

            try
            {
                string ip = txtIp.Text.Trim();
                string user = txtUser.Text.Trim();
                string keyPath = txtKeyPath.Text.Trim();
                string profileName = txtName.Text.Trim();

                // [STEP 1] SSH 터널 뚫기 (이미 뚫려있으면 기존 포트 재사용)
                int localPort = await SshTunnelManager.GetOrStartTunnelAsync(ip, sshPort, user, keyPath);

                // [STEP 2] 해당 터널로 접속하는 신분증(Profile) 생성
                var remoteProfile = new ConnectionProfile
                {
                    Name = profileName,
                    Type = EndpointType.SshRemote,
                    HostIp = ip,
                    SshUsername = user,
                    SshPort = sshPort,
                    LocalTunnelPort = localPort, // 터널 매니저가 할당해준 로컬 포트

                    // =================================================================
                    // ★ [추가] 나중에 앱을 다시 켰을 때 알아서 접속할 수 있게 키 경로 저장!
                    // =================================================================
                    SshKeyFilePath = keyPath
                };

                // [STEP 3] 원격 전용 DockerApiService 생성
                txtStatus.Text = "도커 엔진 확인 중...";
                var remoteDockerService = new DockerApiService(remoteProfile);

                // [STEP 4] 실제로 도커가 살아있는지 최종 확인 (Ping)
                bool isAlive = await remoteDockerService.PingAsync();

                if (isAlive)
                {
                    // [STEP 5] 성공! MainViewModel에 새 원격 시트 추가
                    var newRemoteSheet = new SheetViewModel(profileName, remoteProfile, remoteDockerService, _dialogService);

                    _mainVm.Sheets.Add(newRemoteSheet);
                    _mainVm.ActiveSheet = newRemoteSheet; // 즉시 해당 탭으로 이동

                    // 앱 전역 서비스 리스트에 등록 (나중에 앱 끌 때 한꺼번에 닫기 위함)
                    App.ActiveDockerServices.Add(remoteDockerService);

                    _dialogService.ShowInfo($"'{profileName}' 서버에 연결되었습니다.", "연결 성공");
                    this.DialogResult = true;
                    this.Close();
                }
                else
                {
                    // SSH는 뚫렸는데 도커가 안 켜져 있는 경우
                    SshTunnelManager.ReleaseTunnel(ip, sshPort, user); // 참조 카운트 감소
                    remoteDockerService.Dispose();
                    _dialogService.ShowMessage("SSH 연결은 성공했으나, 원격 서버에서 도커 엔진을 찾을 수 없습니다.");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage($"연결 실패:\n{ex.Message}");
            }
            finally
            {
                btnConnect.IsEnabled = true;
                txtStatus.Visibility = Visibility.Collapsed;
            }
        }
    }
}