using System.Windows;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        private async void InspectorDelete_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.Inspector.DeleteSelectedAsync();
        }
    }
}
