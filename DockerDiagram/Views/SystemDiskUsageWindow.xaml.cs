using DockerDiagram.Contracts;
using DockerDiagram.Common;
using DockerDiagram.Models;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class SystemDiskUsageWindow : Window
    {
        private readonly IDockerService _dockerService;
        private readonly IDialogService _dialogService;
        private readonly SystemDiskUsageViewModel _viewModel;

        public SystemDiskUsageWindow(IDockerService dockerService, IDialogService dialogService, string profileName)
        {
            InitializeComponent();
            _dockerService = dockerService;
            _dialogService = dialogService;
            _viewModel = new SystemDiskUsageViewModel { ProfileName = profileName };
            DataContext = _viewModel;
            Loaded += async (_, _) => await RefreshAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            try
            {
                IsEnabled = false;
                _viewModel.Usage = await _dockerService.GetSystemDiskUsageAsync();
            }
            catch (System.Exception ex)
            {
                _dialogService.ShowError($"Disk usage 조회 실패: {ex.Message}", "Docker Disk Usage");
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }

    public class SystemDiskUsageViewModel : ViewModelBase
    {
        private SystemDiskUsage _usage = new();
        private string _profileName = "Docker";

        public SystemDiskUsage Usage
        {
            get => _usage;
            set => SetProperty(ref _usage, value);
        }

        public string ProfileName
        {
            get => _profileName;
            set => SetProperty(ref _profileName, value);
        }
    }
}
