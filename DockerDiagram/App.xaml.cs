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
        public static List<IDockerService> ActiveDockerServices { get; } = [];

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            VolumeUndoBackupStore.CleanupOrphanBackups();

            IDialogService dialogService = new DialogService();

            // 기본 로컬 Docker 연결 프로필
            var localProfile = new ConnectionProfile
            {
                Name = "Local PC",
                Type = EndpointType.Local
            };

            // 기본 로컬 Docker 서비스 생성
            var defaultDockerService = new DockerApiService(localProfile);

            // 종료 시 해제할 서비스로 등록
            ActiveDockerServices.Add(defaultDockerService);

            var mainViewModel = new MainViewModel(defaultDockerService, dialogService);

            var mainWindow = new MainWindow(
                mainViewModel,
                defaultDockerService,
                dialogService
            );

            mainWindow.Show();
        }

        // 앱이 종료될 때 호출됨
        protected override void OnExit(ExitEventArgs e)
        {
            VolumeUndoBackupStore.CleanupActiveBackups();

            foreach (var service in ActiveDockerServices)
            {
                service?.Dispose();
            }
            ActiveDockerServices.Clear();

            base.OnExit(e);
        }
    }
}
