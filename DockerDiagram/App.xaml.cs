using DockerDiagram.Helpers;
using DockerDiagram.ViewModels;
using System.Runtime.Versioning;
using System.Windows;

namespace DockerDiagram
{
    [SupportedOSPlatform("windows")]
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 통합 서비스 생성
            // (DockerApiService가 IContainerService, IVolumeService 등을 모두 구현하고 있어야 합니다)
            var dockerApiService = new DockerApiService();

            // 2. 다이얼로그 서비스 생성
            IDialogService dialogService = new DialogService();

            // 3. MainViewModel 생성 (순서 중요: Container, Volume, Network, Image, System, Dialog)
            var mainViewModel = new MainViewModel(
                dockerApiService, // IContainerService
                dockerApiService, // IVolumeService
                dockerApiService, // INetworkService
                dockerApiService, // IImageService
                dockerApiService, // ISystemService
                dialogService     // IDialogService
            );

            // 4. MainWindow 생성 (ViewModel, SystemService, DialogService)
            var mainWindow = new MainWindow(
                mainViewModel,
                dockerApiService, // ISystemService (도커 상태 체크용)
                dialogService
            );

            // 5. 화면 띄우기
            mainWindow.Show();
        }
    }
}