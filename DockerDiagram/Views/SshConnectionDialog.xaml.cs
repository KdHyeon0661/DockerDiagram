using System;
using System.Windows;
using Microsoft.Win32;
using DockerDiagram.Helpers;
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

        /// <summary>
        /// 원격 서버 연결 대화상자를 초기화하며, 통신 성공 시 새로운 시트를 추가할 메인 뷰모델과 커스텀 알림을 띄울 다이얼로그 서비스를 주입받습니다.
        /// </summary>
        public SshConnectionDialog(MainViewModel mainVm, IDialogService dialogService)
        {
            InitializeComponent();
            _mainVm = mainVm;
            _dialogService = dialogService;
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
                int localPort = await SshTunnelManager.GetOrStartTunnelAsync(ip, sshPort, user, keyPath, _dialogService);

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