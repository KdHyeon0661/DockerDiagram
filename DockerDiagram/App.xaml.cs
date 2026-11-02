using System.Runtime.Versioning;
using System.Windows;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using DockerDiagram.Views;

namespace DockerDiagram
{
    [SupportedOSPlatform("windows")]
    public partial class App : Application, IDisposable
    {
        private DockerServiceFactory? _dockerServiceFactory;
        private MainWindow? _mainWindow;
        private bool _disposed;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            VolumeUndoBackupStore.CleanupOrphanBackups();

            DialogService dialogService = new DialogService();
            AsyncRelayCommand.UnhandledExceptionHandler = exception =>
                dialogService.BeginInvokeOnUiThread(() =>
                    dialogService.ShowError(
                        $"작업을 완료하지 못했습니다.\n\n{exception.GetBaseException().Message}\n\n" +
                        $"진단 로그: {AppLog.LogFilePath}",
                        "작업 실패"));

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

            _mainWindow = new MainWindow(mainViewModel, dialogService);
            _mainWindow.Show();
        }

        // 앱이 종료될 때 호출됨
        protected override void OnExit(ExitEventArgs e)
        {
            VolumeUndoBackupStore.CleanupActiveBackups();
            Dispose();
            base.OnExit(e);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _mainWindow?.Dispose();
            _mainWindow = null;
            _dockerServiceFactory?.Dispose();
            _dockerServiceFactory = null;
            SshTunnelManager.CloseAllTunnels();
            AsyncRelayCommand.UnhandledExceptionHandler = null;
            GC.SuppressFinalize(this);
        }
    }
}
