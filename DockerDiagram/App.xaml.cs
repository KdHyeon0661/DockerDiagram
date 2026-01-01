using System.Windows;
using DockerDiagram.ViewModels; // MainViewModel 위치
using DockerDiagram.Helpers;    // (혹시 DialogService가 여기 있다면 필요)

namespace DockerDiagram
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---------------------------------------------------------
            // [체크 포인트 1] DockerApiService 생성자가 public인지 확인하세요!
            // ---------------------------------------------------------
            IDockerService dockerService = new DockerApiService();

            // ---------------------------------------------------------
            // [체크 포인트 2] DialogService 클래스가 실제로 존재하는지 확인하세요!
            // (없다면 아래에 코드를 추가해 드립니다)
            // ---------------------------------------------------------
            IDialogService dialogService = new DialogService();

            // 2. 뷰모델 생성
            var mainViewModel = new MainViewModel(dockerService, dialogService);

            // 3. 윈도우 생성
            var mainWindow = new MainWindow(mainViewModel, dockerService, dialogService);

            // 4. 화면 띄우기
            mainWindow.Show();
        }
    }
}