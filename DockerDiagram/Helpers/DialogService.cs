using System.Windows;

namespace DockerDiagram.Helpers
{
    public class DialogService : IDialogService
    {
        public void ShowMessage(string message) // 간단한 메시지 박스 표시
        {
            System.Windows.MessageBox.Show(message);
        }
        public bool ShowConfirm(string message, string title) // 예/아니오 확인 대화상자 표시
        {
            var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        public void ShowInfo(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
