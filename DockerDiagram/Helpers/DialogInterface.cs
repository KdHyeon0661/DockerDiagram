using System.Windows;

namespace DockerDiagram.Helpers
{
    public interface IDialogService
    {
        void ShowMessage(string message);
        bool ShowConfirm(string message, string title);
        void ShowInfo(string message, string title);
        MessageBoxResult ShowYesNoCancel(string message, string title);
    }
}
