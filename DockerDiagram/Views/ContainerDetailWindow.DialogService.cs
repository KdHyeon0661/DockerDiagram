using DockerDiagram.Contracts;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow
    {
        private readonly IDialogService _dialogService = new Views.DialogService();
    }
}
