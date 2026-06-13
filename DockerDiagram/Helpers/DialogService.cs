using System.Windows;

using DockerDiagram.Models;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// ViewModel에서 사용하는 메시지, 파일 선택 및 모달 창을 제공합니다.
    /// </summary>
    public class DialogService : IDialogService
    {
        public void ShowMessage(string message)
        {
            MessageBox.Show(message);
        }

        public bool ShowConfirm(string message, string title)
        {
            var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        public void ShowInfo(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public MessageBoxResult ShowYesNoCancel(string message, string title)
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        }
        public void ShowError(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public bool ShowHostKeyConfirm(string host, string fingerprintText)
        {
            string msg = $"처음 접속하는 서버({host})입니다.\n\n서버의 고유 지문(Fingerprint):\n{fingerprintText}\n\n이 서버를 신뢰하고 계속 연결하시겠습니까?";

            var result = MessageBox.Show(msg, "새로운 호스트 키 검증 (보안)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        }

        public string? ShowOpenFileDialog(string filter, string title)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = filter,
                Title = title
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }
        public string? ShowSaveFileDialog(string filter, string defaultExt, string defaultFileName, string title)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = filter,
                DefaultExt = defaultExt,
                FileName = defaultFileName,
                Title = title
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        public bool TryShowPruneOptionsDialog(out DockerPruneOptions options)
        {
            var dlg = new DockerDiagram.Views.PruneDialog
            {
                Owner = Application.Current.MainWindow
            };

            if (dlg.ShowDialog() == true)
            {
                options = dlg.PruneOptions;
                return true;
            }

            options = new DockerPruneOptions();
            return false;
        }

        public bool TryShowVolumeOptionsDialog(VolumeCreateOptions initialOptions, out VolumeCreateOptions options)
        {
            var dlg = new DockerDiagram.Views.VolumeDialog(this, initialOptions)
            {
                Owner = Application.Current.MainWindow
            };

            if (dlg.ShowDialog() == true)
            {
                options = dlg.CreateOptions;
                return true;
            }

            options = initialOptions;
            return false;
        }

        public void ShowContainerDetail(object dataContext)
        {
            var window = new DockerDiagram.ContainerDetailWindow
            {
                DataContext = dataContext,
                Owner = Application.Current.MainWindow
            };
            window.Show();
        }

        public bool TryShowMountDialog(out string mountPath, out string owner)
        {
            var dlg = new DockerDiagram.Views.MountDialog(this)
            {
                Owner = Application.Current.MainWindow
            };

            if (dlg.ShowDialog() == true)
            {
                mountPath = dlg.MountPath;
                owner = dlg.VolumeOwner;
                return true;
            }

            mountPath = string.Empty;
            owner = string.Empty;
            return false;
        }

        public bool TryShowArrangeDialog(out int columns)
        {
            var dlg = new DockerDiagram.Views.ArrangeDialog(this)
            {
                Owner = Application.Current.MainWindow
            };

            if (dlg.ShowDialog() == true)
            {
                columns = dlg.Columns;
                return true;
            }

            columns = 0;
            return false;
        }

        public bool TryShowImageTagDialog(
            string sourceImage,
            string repository,
            string tag,
            out string newRepository,
            out string newTag,
            out bool force)
        {
            var window = new DockerDiagram.Views.ImageTagWindow(sourceImage, repository, tag)
            {
                Owner = Application.Current.MainWindow
            };

            if (window.ShowDialog() == true)
            {
                newRepository = window.Repository;
                newTag = window.ImageTag;
                force = window.Force;
                return true;
            }

            newRepository = repository;
            newTag = tag;
            force = false;
            return false;
        }

        public bool TryShowImagePushDialog(
            string repository,
            string tag,
            out string newRepository,
            out string newTag,
            out string username,
            out string password,
            out string serverAddress)
        {
            var window = new DockerDiagram.Views.ImagePushWindow(repository, tag)
            {
                Owner = Application.Current.MainWindow
            };

            if (window.ShowDialog() == true)
            {
                newRepository = window.Repository;
                newTag = window.ImageTag;
                username = window.Username;
                password = window.Password;
                serverAddress = window.ServerAddress;
                return true;
            }

            newRepository = repository;
            newTag = tag;
            username = string.Empty;
            password = string.Empty;
            serverAddress = string.Empty;
            return false;
        }
    }
}
