using System.Windows;

namespace DockerDiagram.Helpers
{
    public class DialogService : IDialogService
    {
        public void ShowMessage(string message)
        {
            System.Windows.MessageBox.Show(message);
        }
        public bool ShowConfirm(string message, string title)
        {
            var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }
    }
}
