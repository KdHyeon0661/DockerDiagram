using System.Windows;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 뷰모델(ViewModel)에서 직접 창을 띄우지 않도록 UI 의존성을 분리하는 다이얼로그 인터페이스입니다.
    /// 이 인터페이스를 통해 화면 없는 단위 테스트(Unit Test)가 가능해지고, 추후 커스텀 팝업으로의 교체가 쉬워집니다.
    /// </summary>
    public interface IDialogService
    {
        void ShowMessage(string message); // 일반적인 텍스트 메시지 창을 띄웁니다.
        bool ShowConfirm(string message, string title); // 사용자에게 예/아니오를 묻고 결과를 true/false로 반환합니다.
        void ShowInfo(string message, string title); // 정보(i) 아이콘이 포함된 안내 메시지 창을 띄웁니다.
        MessageBoxResult ShowYesNoCancel(string message, string title); // 예/아니오/취소 3가지 선택을 묻는 창을 띄웁니다.
        void ShowError(string message, string title); // 제목과 오류(X) 아이콘이 포함된 에러 메시지 창을 띄웁니다.
        bool ShowHostKeyConfirm(string host, string fingerprintText);
        string? ShowOpenFileDialog(string filter, string title);
        string? ShowSaveFileDialog(string filter, string defaultExt, string defaultFileName, string title);
    }
}