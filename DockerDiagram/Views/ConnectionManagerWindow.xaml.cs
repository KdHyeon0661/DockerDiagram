using System.Windows;
using System.Windows.Controls;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Views
{
    public partial class ConnectionManagerWindow : Window
    {
        private readonly MainViewModel _mainVm;
        private readonly IDialogService _dialogService;

        public ConnectionManagerWindow(MainViewModel mainVm, IDialogService dialogService)
        {
            InitializeComponent();
            _mainVm = mainVm;
            _dialogService = dialogService;
            ConnectionGrid.ItemsSource = _mainVm.Workspaces;
            ConnectionGrid.SelectedItem = _mainVm.SheetManager.ActiveWorkspace;
            SyncNameBox();
        }

        private ConnectionWorkspaceViewModel? SelectedWorkspace => ConnectionGrid.SelectedItem as ConnectionWorkspaceViewModel;

        private void ConnectionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncNameBox();
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            var workspace = SelectedWorkspace;
            if (workspace == null) return;

            string newName = txtConnectionName.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName)) return;

            _mainVm.SheetManager.RenameWorkspace(workspace, newName);
            ConnectionGrid.Items.Refresh();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var workspace = SelectedWorkspace;
            if (workspace == null) return;

            if (workspace.Profile.Type == EndpointType.Local)
            {
                _dialogService.ShowMessage("Local PC 연결은 삭제할 수 없습니다.");
                return;
            }

            if (!_dialogService.ShowConfirm($"'{workspace.DisplayName}' 연결을 삭제하시겠습니까?\n이 연결 안의 시트도 목록에서 제거됩니다.", "Connection Delete"))
                return;

            if (_mainVm.SheetManager.RemoveWorkspace(workspace))
            {
                ConnectionGrid.Items.Refresh();
                ConnectionGrid.SelectedItem = _mainVm.SheetManager.ActiveWorkspace;
                SyncNameBox();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SyncNameBox()
        {
            txtConnectionName.Text = SelectedWorkspace?.DisplayName ?? "";
        }
    }
}
