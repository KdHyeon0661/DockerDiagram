using System.Windows;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 사용자에게 알림, 경고, 확인 메시지 등을 띄우는 팝업(메시지 박스) 서비스입니다.
    /// MVVM 패턴에서 뷰모델(ViewModel)이 화면(UI) 요소에 직접 의존하지 않도록 분리해 주는 역할을 합니다.
    /// </summary>
    public class DialogService : IDialogService
    {
        public void ShowMessage(string message) // 제목 없는 간단한 텍스트 위주의 알림 창을 띄웁니다.
        {
            MessageBox.Show(message);
        }

        public bool ShowConfirm(string message, string title) // 사용자에게 예/아니오(Yes/No)를 묻는 확인 창을 띄우고 결과를 true/false로 반환합니다.
        {
            var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        public void ShowInfo(string message, string title) // 제목과 정보(i) 아이콘이 포함된 깔끔한 안내 메시지 창을 띄웁니다.
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public MessageBoxResult ShowYesNoCancel(string message, string title) // 앱 종료 시 저장 확인 등 예/아니오/취소 3가지 선택지가 필요한 창을 띄웁니다.
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        }
        public void ShowError(string message, string title) // 제목과 오류(X) 아이콘이 포함된 에러 메시지 창을 띄웁니다.
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

    }
}