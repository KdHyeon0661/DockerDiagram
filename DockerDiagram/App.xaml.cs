using DockerDiagram.Helpers;
using DockerDiagram.ViewModels;
using System.Runtime.Versioning;
using System.Windows;

namespace DockerDiagram
{
    [SupportedOSPlatform("windows")]
    public partial class App : Application
    {
        // 1. 멤버 변수로 선언 (OnExit에서 접근하기 위해)
        private DockerApiService? _dockerService;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 2. 서비스 생성
            _dockerService = new DockerApiService();
            IDialogService dialogService = new DialogService();

            // 3. MainViewModel 생성
            // (참고: MainViewModel 생성자도 IDockerService 하나만 받도록 수정해야 합니다)
            var mainViewModel = new MainViewModel(_dockerService, dialogService);

            // 4. MainWindow 생성
            var mainWindow = new MainWindow(
                mainViewModel,
                _dockerService, // ISystemService
                dialogService
            );

            mainWindow.Show();
        }

        // 5. 앱이 종료될 때 호출됨 (여기가 Dispose의 제자리입니다)
        protected override void OnExit(ExitEventArgs e)
        {
            // 도커 클라이언트 연결 안전하게 종료
            _dockerService?.Dispose();

            base.OnExit(e);
        }
    }
}