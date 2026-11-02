using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DockerDiagram.Common;

namespace DockerDiagram.ViewModels
{
    public sealed partial class ContainerOperationsViewModel
    {
        public void OnTransferPathChanged()
        {
            if (string.IsNullOrWhiteSpace(_node.HostFilePath) || string.IsNullOrWhiteSpace(_node.ContainerFilePath))
                TransferStatus = "호스트 경로와 컨테이너 경로를 모두 입력하세요.";
            else if (Directory.Exists(_node.HostFilePath))
                TransferStatus = "업로드 또는 다운로드할 수 있습니다.";
            else if (File.Exists(_node.HostFilePath))
                TransferStatus = "이 파일을 업로드할 수 있습니다. 다운로드 대상은 폴더를 선택하세요.";
            else
                TransferStatus = "업로드 경로는 존재해야 합니다. 다운로드 시 호스트 폴더는 자동으로 생성됩니다.";

            RaiseTransferCommandStates();
        }

        private bool HasTransferPaths() =>
            IsConnectedContainer &&
            !IsTransferBusy &&
            !string.IsNullOrWhiteSpace(_node.HostFilePath) &&
            !string.IsNullOrWhiteSpace(_node.ContainerFilePath);

        private bool CanCopyToContainer() =>
            HasTransferPaths() &&
            (File.Exists(_node.HostFilePath) || Directory.Exists(_node.HostFilePath));

        private bool CanCopyFromContainer() =>
            HasTransferPaths() && !File.Exists(_node.HostFilePath);

        private void RaiseTransferCommandStates()
        {
            if (CopyToContainerCommand is AsyncRelayCommand copyTo)
                copyTo.RaiseCanExecuteChanged();
            if (CopyFromContainerCommand is AsyncRelayCommand copyFrom)
                copyFrom.RaiseCanExecuteChanged();
        }

        private void BrowseHostFile()
        {
            string? path = _dialogService.ShowOpenFileDialog("All files (*.*)|*.*", "호스트 파일 선택");
            if (!string.IsNullOrWhiteSpace(path))
                _node.HostFilePath = path;
        }

        private void BrowseHostFolder()
        {
            string? initialDirectory = Directory.Exists(_node.HostFilePath)
                ? _node.HostFilePath
                : Path.GetDirectoryName(_node.HostFilePath);
            string? path = _dialogService.ShowOpenFolderDialog("호스트 폴더 선택", initialDirectory);
            if (!string.IsNullOrWhiteSpace(path))
                _node.HostFilePath = path;
        }

        private void SetTransferBusy(bool isBusy, string status)
        {
            IsTransferBusy = isBusy;
            TransferStatus = status;
            RaiseTransferCommandStates();
            RaiseDetailTransferCommandStates();
        }

        private bool CanDetailUpload() =>
            IsConnectedContainer && !IsTransferBusy &&
            !string.IsNullOrWhiteSpace(UploadContainerPath) &&
            (File.Exists(UploadHostPath) || Directory.Exists(UploadHostPath));

        private bool CanDetailDownload() =>
            IsConnectedContainer && !IsTransferBusy &&
            !string.IsNullOrWhiteSpace(DownloadContainerPath) &&
            !string.IsNullOrWhiteSpace(DownloadHostFolder) &&
            !File.Exists(DownloadHostFolder);

        private void RaiseDetailTransferCommandStates()
        {
            DetailUploadCommand?.RaiseCanExecuteChanged();
            DetailDownloadCommand?.RaiseCanExecuteChanged();
            CancelTransferCommand?.RaiseCanExecuteChanged();
        }

        private void BrowseUploadFile()
        {
            string? path = _dialogService.ShowOpenFileDialog("All files (*.*)|*.*", "업로드할 파일 선택");
            if (!string.IsNullOrWhiteSpace(path)) UploadHostPath = path;
        }

        private void BrowseUploadFolder()
        {
            string? path = _dialogService.ShowOpenFolderDialog("업로드할 폴더 선택", Directory.Exists(UploadHostPath) ? UploadHostPath : null);
            if (!string.IsNullOrWhiteSpace(path)) UploadHostPath = path;
        }

        private void BrowseDownloadFolder()
        {
            string? path = _dialogService.ShowOpenFolderDialog("다운로드 대상 폴더 선택", Directory.Exists(DownloadHostFolder) ? DownloadHostFolder : null);
            if (!string.IsNullOrWhiteSpace(path)) DownloadHostFolder = path;
        }

        private async Task DetailUploadAsync()
        {
            _transferCts?.Dispose();
            _transferCts = new CancellationTokenSource();
            SetTransferBusy(true, "컨테이너로 복사하는 중...");
            try
            {
                await _containerService.CopyToContainerAsync(
                    _node.ContainerId,
                    UploadHostPath,
                    UploadContainerPath,
                    _transferCts.Token);
                TransferStatus = "업로드가 완료되었습니다.";
            }
            catch (OperationCanceledException)
            {
                TransferStatus = "업로드를 취소했습니다.";
            }
            catch (Exception ex)
            {
                TransferStatus = $"업로드 실패: {ex.GetBaseException().Message}";
                _dialogService.ShowError(TransferStatus, "파일 업로드");
            }
            finally
            {
                _transferCts?.Dispose();
                _transferCts = null;
                SetTransferBusy(false, TransferStatus);
            }
        }

        private async Task DetailDownloadAsync()
        {
            if (Directory.Exists(DownloadHostFolder) && Directory.EnumerateFileSystemEntries(DownloadHostFolder).Any() &&
                !_dialogService.ShowConfirm(
                    "대상 폴더가 비어 있지 않습니다. 같은 이름의 파일은 덮어쓸 수 있습니다. 계속하시겠습니까?",
                    "다운로드 대상 확인"))
            {
                return;
            }

            _transferCts?.Dispose();
            _transferCts = new CancellationTokenSource();
            SetTransferBusy(true, "컨테이너에서 복사하는 중...");
            try
            {
                Directory.CreateDirectory(DownloadHostFolder);
                await _containerService.CopyFromContainerAsync(
                    _node.ContainerId,
                    DownloadContainerPath,
                    DownloadHostFolder,
                    _transferCts.Token);
                TransferStatus = "다운로드가 완료되었습니다.";
            }
            catch (OperationCanceledException)
            {
                TransferStatus = "다운로드를 취소했습니다.";
            }
            catch (Exception ex)
            {
                TransferStatus = $"다운로드 실패: {ex.GetBaseException().Message}";
                _dialogService.ShowError(TransferStatus, "파일 다운로드");
            }
            finally
            {
                _transferCts?.Dispose();
                _transferCts = null;
                SetTransferBusy(false, TransferStatus);
            }
        }

        private void CancelTransfer() => _transferCts?.Cancel();

        private async Task CopyToContainerAsync()
        {
            if (string.IsNullOrWhiteSpace(_node.HostFilePath) ||
                string.IsNullOrWhiteSpace(_node.ContainerFilePath))
            {
                return;
            }

            SetTransferBusy(true, "컨테이너로 복사하는 중...");
            try
            {
                await _containerService.CopyToContainerAsync(
                    _node.ContainerId,
                    _node.HostFilePath,
                    _node.ContainerFilePath);
                TransferStatus = "업로드가 완료되었습니다.";
                _dialogService.ShowInfo("컨테이너로 파일 복사가 완료되었습니다.", "업로드 성공");
            }
            catch (Exception ex)
            {
                TransferStatus = $"업로드 실패: {ex.Message}";
                _dialogService.ShowMessage($"업로드 실패: {ex.Message}");
            }
            finally
            {
                SetTransferBusy(false, TransferStatus);
            }
        }

        private async Task CopyFromContainerAsync()
        {
            if (string.IsNullOrWhiteSpace(_node.HostFilePath) ||
                string.IsNullOrWhiteSpace(_node.ContainerFilePath))
            {
                return;
            }

            SetTransferBusy(true, "컨테이너에서 복사하는 중...");
            try
            {
                Directory.CreateDirectory(_node.HostFilePath);
                await _containerService.CopyFromContainerAsync(
                    _node.ContainerId,
                    _node.ContainerFilePath,
                    _node.HostFilePath);
                TransferStatus = "다운로드가 완료되었습니다.";
                _dialogService.ShowInfo("컨테이너에서 파일 다운로드가 완료되었습니다.", "다운로드 성공");
            }
            catch (Exception ex)
            {
                TransferStatus = $"다운로드 실패: {ex.Message}";
                _dialogService.ShowMessage($"다운로드 실패: {ex.Message}");
            }
            finally
            {
                SetTransferBusy(false, TransferStatus);
            }
        }
    }
}
