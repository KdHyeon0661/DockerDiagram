using DockerDiagram.Views;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Infrastructure;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Runtime.Versioning;
using System.Windows;

namespace DockerDiagram
{
    [SupportedOSPlatform("windows")]
    public partial class App : Application
    {
        private IDockerServiceFactory? _dockerServiceFactory;

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
            _dockerServiceFactory = new DockerServiceFactory();
            var defaultDockerService = _dockerServiceFactory.Create(localProfile);

            var mainViewModel = new MainViewModel(defaultDockerService, dialogService, _dockerServiceFactory);

            var mainWindow = new MainWindow(mainViewModel, dialogService);
            mainWindow.Show();
        }

        // 앱이 종료될 때 호출됨
        protected override void OnExit(ExitEventArgs e)
        {
            VolumeUndoBackupStore.CleanupActiveBackups();

            _dockerServiceFactory?.Dispose();
            _dockerServiceFactory = null;
            SshTunnelManager.CloseAllTunnels();

            base.OnExit(e);
        }
    }
}
