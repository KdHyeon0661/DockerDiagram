using System;
using System.Threading.Tasks;

using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    public enum DialogChoice
    {
        Yes,
        No,
        Cancel
    }

    /// <summary>
    /// 뷰모델(ViewModel)에서 직접 창을 띄우지 않도록 UI 의존성을 분리하는 다이얼로그 인터페이스입니다.
    /// 이 인터페이스를 통해 화면 없는 단위 테스트(Unit Test)가 가능해지고, 추후 커스텀 팝업으로의 교체가 쉬워집니다.
    /// </summary>
    public interface IDialogService
    {
        void ShowMessage(string message); // 일반적인 텍스트 메시지 창을 띄웁니다.
        bool ShowConfirm(string message, string title); // 사용자에게 예/아니오를 묻고 결과를 true/false로 반환합니다.
        void ShowInfo(string message, string title); // 정보(i) 아이콘이 포함된 안내 메시지 창을 띄웁니다.
        DialogChoice ShowYesNoCancel(string message, string title); // 예/아니오/취소 3가지 선택을 묻는 창을 띄웁니다.
        void ShowError(string message, string title); // 제목과 오류(X) 아이콘이 포함된 에러 메시지 창을 띄웁니다.
        bool ShowHostKeyConfirm(string host, string fingerprintText);
        string? ShowOpenFileDialog(string filter, string title, string? fileName = null);
        string? ShowOpenFolderDialog(string title, string? initialDirectory = null);
        string? ShowSaveFileDialog(string filter, string defaultExt, string defaultFileName, string title);
        bool TryShowPruneOptionsDialog(out DockerPruneOptions options);
        bool TryShowVolumeOptionsDialog(VolumeCreateOptions initialOptions, out VolumeCreateOptions options);
        void ShowContainerDetail(object dataContext);
        bool TryShowContainerRenameDialog(object ownerContext, string currentName, out string newName);
        void ShowContainerExecDialog(
            object ownerContext,
            string containerName,
            string containerId,
            Func<string, Task<ExecCommandResult>> executeCommand);
        bool TryShowContainerCommitDialog(
            object ownerContext,
            string containerName,
            out string repository,
            out string imageTag,
            out string message,
            out string author,
            out bool pause);
        void ShowRawInspectDialog(object ownerContext, string inspectTitle, string json);
        void ShowVolumeDetail(object dataContext);
        bool TryShowMountDialog(out string mountPath, out string owner);
        bool TryShowArrangeDialog(out int columns);
        bool TryShowComposeLayoutDialog(ComposeLayoutOptions initialOptions, out ComposeLayoutOptions options);
        bool TryShowImageTagDialog(string sourceImage, string repository, string tag, out string newRepository, out string newTag, out bool force);
        bool TryShowImagePushDialog(string repository, string tag, out string newRepository, out string newTag, out string username, out string password, out string serverAddress);
        bool TryShowKubernetesPortForwardDialog(string kind, string target, int defaultLocalPort, int defaultRemotePort, out int localPort, out int remotePort);
        void SetClipboardText(string text);
        void SetBusyCursor(bool isBusy);
        Task InvokeOnUiThreadAsync(Action action);
        Task<T> InvokeOnUiThreadAsync<T>(Func<T> action);
        void BeginInvokeOnUiThread(Action action);
    }
}
