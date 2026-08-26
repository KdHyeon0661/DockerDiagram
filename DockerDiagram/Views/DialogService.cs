using DockerDiagram.Contracts;
using System.Windows;
using System.Windows.Input;
using System;
using System.Threading.Tasks;

using DockerDiagram.Models;
using System.Collections.Generic;

namespace DockerDiagram.Views
{
    /// <summary>
    /// ViewModel에서 사용하는 메시지, 파일 선택 및 모달 창을 제공합니다.
    /// </summary>
    public class DialogService : IDialogService
    {
        private readonly Dictionary<object, DockerDiagram.ContainerDetailWindow> _containerDetailWindows =
            new(ReferenceEqualityComparer.Instance);

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

        public DialogChoice ShowYesNoCancel(string message, string title)
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
            {
                MessageBoxResult.Yes => DialogChoice.Yes,
                MessageBoxResult.No => DialogChoice.No,
                _ => DialogChoice.Cancel
            };
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

        public string? ShowOpenFileDialog(string filter, string title, string? fileName = null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = filter,
                Title = title,
                FileName = fileName ?? string.Empty
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        public string? ShowOpenFolderDialog(string title, string? initialDirectory = null)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = title,
                InitialDirectory = System.IO.Directory.Exists(initialDirectory) ? initialDirectory : string.Empty
            };
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
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
            if (_containerDetailWindows.TryGetValue(dataContext, out var existingWindow))
            {
                if (existingWindow.WindowState == WindowState.Minimized)
                    existingWindow.WindowState = WindowState.Normal;

                existingWindow.Activate();
                return;
            }

            var window = new DockerDiagram.ContainerDetailWindow
            {
                DataContext = dataContext,
                Owner = Application.Current.MainWindow
            };
            _containerDetailWindows[dataContext] = window;
            window.Closed += (_, _) =>
            {
                if (_containerDetailWindows.TryGetValue(dataContext, out var registeredWindow) &&
                    ReferenceEquals(registeredWindow, window))
                {
                    _containerDetailWindows.Remove(dataContext);
                }
            };

            try
            {
                window.Show();
            }
            catch
            {
                _containerDetailWindows.Remove(dataContext);
                throw;
            }
        }
        public bool TryShowContainerRenameDialog(object ownerContext, string currentName, out string newName)
        {
            var window = new DockerDiagram.Views.ContainerRenameWindow(currentName)
            {
                Owner = ResolveOwner(ownerContext)
            };

            if (window.ShowDialog() == true)
            {
                newName = window.NewName;
                return true;
            }

            newName = currentName;
            return false;
        }

        public void ShowContainerExecDialog(
            object ownerContext,
            string containerName,
            string containerId,
            Func<string, Task<ExecCommandResult>> executeCommand)
        {
            var window = new DockerDiagram.Views.ContainerExecWindow(
                containerName,
                containerId,
                executeCommand)
            {
                Owner = ResolveOwner(ownerContext)
            };
            window.ShowDialog();
        }

        public bool TryShowContainerCommitDialog(
            object ownerContext,
            string containerName,
            out string repository,
            out string imageTag,
            out string message,
            out string author,
            out bool pause)
        {
            var window = new DockerDiagram.Views.ContainerCommitWindow(containerName)
            {
                Owner = ResolveOwner(ownerContext)
            };

            if (window.ShowDialog() == true)
            {
                repository = window.Repository;
                imageTag = window.ImageTag;
                message = window.Message;
                author = window.Author;
                pause = window.Pause;
                return true;
            }

            repository = string.Empty;
            imageTag = string.Empty;
            message = string.Empty;
            author = string.Empty;
            pause = false;
            return false;
        }

        public void ShowRawInspectDialog(object ownerContext, string inspectTitle, string json)
        {
            var window = new DockerDiagram.Views.RawInspectWindow(inspectTitle, json)
            {
                Owner = ResolveOwner(ownerContext)
            };
            window.ShowDialog();
        }

        private Window? ResolveOwner(object ownerContext)
        {
            if (_containerDetailWindows.TryGetValue(ownerContext, out var detailWindow))
                return detailWindow;

            return Application.Current?.MainWindow;
        }

        public void ShowVolumeDetail(object dataContext)
        {
            var window = new DockerDiagram.Views.VolumeDetailWindow
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

        public bool TryShowComposeLayoutDialog(ComposeLayoutOptions initialOptions, out ComposeLayoutOptions options)
        {
            var dialog = new DockerDiagram.Views.ComposeLayoutDialog(initialOptions)
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                options = dialog.Options;
                return true;
            }

            options = initialOptions;
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

        public bool TryShowKubernetesPortForwardDialog(
            string kind,
            string target,
            int defaultLocalPort,
            int defaultRemotePort,
            out int localPort,
            out int remotePort)
        {
            var window = new DockerDiagram.Views.KubernetesPortForwardDialog(kind, target, defaultLocalPort, defaultRemotePort)
            {
                Owner = Application.Current.MainWindow
            };

            if (window.ShowDialog() == true)
            {
                localPort = window.LocalPort;
                remotePort = window.RemotePort;
                return true;
            }

            localPort = defaultLocalPort;
            remotePort = defaultRemotePort;
            return false;
        }
        public void SetClipboardText(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            RunOnUiThread(() => Clipboard.SetText(text));
        }

        public void SetBusyCursor(bool isBusy)
        {
            RunOnUiThread(() => Mouse.OverrideCursor = isBusy ? Cursors.Wait : null);
        }

        public async Task InvokeOnUiThreadAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            await dispatcher.InvokeAsync(action).Task;
        }

        public async Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) return action();
            return await dispatcher.InvokeAsync(action).Task;
        }

        public void BeginInvokeOnUiThread(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action);
        }

        private static void RunOnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }
    }
}
