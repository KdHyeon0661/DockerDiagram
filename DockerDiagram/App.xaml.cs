using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Windows;

namespace DockerDiagram
{
    [SupportedOSPlatform("windows")]
    public partial class App : Application
    {
        public static List<IDockerService> ActiveDockerServices { get; } = new List<IDockerService>();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            IDialogService dialogService = new DialogService();

            // 1. 앱 시작 시 무조건 쓸 "기본 로컬 신분증" 발급
            var localProfile = new ConnectionProfile
            {
                Name = "Local PC",
                Type = EndpointType.Local
            };

            // 2. 기본 로컬 도커 서비스 생성 (아까 바꾼 생성자 적용!)
            var defaultDockerService = new DockerApiService(localProfile);

            // 3. 종료 시 안전하게 닫기 위해 리스트에 등록
            ActiveDockerServices.Add(defaultDockerService);

            // 4. MainViewModel 생성
            // (참고: 다음 단계에서 MainViewModel 내부 구조도 뜯어고칠 예정입니다)
            var mainViewModel = new MainViewModel(defaultDockerService, dialogService);

            // 5. MainWindow 생성
            var mainWindow = new MainWindow(
                mainViewModel,
                defaultDockerService, // ISystemService 역할 (도커 데몬 켜져있는지 확인용)
                dialogService
            );

            mainWindow.Show();
        }

        // 앱이 종료될 때 호출됨
        protected override void OnExit(ExitEventArgs e)
        {
            foreach (var service in ActiveDockerServices)
            {
                service?.Dispose();
            }
            ActiveDockerServices.Clear();

            base.OnExit(e);
        }
    }
}