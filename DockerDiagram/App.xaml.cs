using System.Windows;
using DockerDiagram.ViewModels; // MainViewModel 위치
using DockerDiagram.Helpers; // DialogService 위치

namespace DockerDiagram
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // DockerApiService 생성자가 public인지 확인하세요!
            IDockerService dockerService = new DockerApiService();

            // DialogService 클래스가 실제로 존재하는지 확인하세요!
            IDialogService dialogService = new DialogService();

            // 뷰모델 생성
            var mainViewModel = new MainViewModel(dockerService, dialogService);

            // 윈도우 생성
            var mainWindow = new MainWindow(mainViewModel, dockerService, dialogService);

            // 화면 띄우기
            mainWindow.Show();
        }
    }
}