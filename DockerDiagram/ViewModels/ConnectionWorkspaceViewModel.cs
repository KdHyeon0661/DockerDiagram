using System.Collections.ObjectModel;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 하나의 Docker 접속 대상(Local 또는 SSH)과 그 안에 속한 여러 맵/시트를 묶는 상위 탭 모델입니다.
    /// </summary>
    public class ConnectionWorkspaceViewModel(ConnectionProfile profile, IDockerService dockerService) : ViewModelBase
    {
        private string _displayName = string.IsNullOrWhiteSpace(profile.Name) ? "Docker" : profile.Name;
        private SheetViewModel? _activeSheet;

        public ConnectionProfile Profile { get; } = profile;
        public IDockerService DockerService { get; } = dockerService;
        public ObservableCollection<SheetViewModel> Sheets { get; } = [];

        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        public SheetViewModel? ActiveSheet
        {
            get => _activeSheet;
            set => SetProperty(ref _activeSheet, value);
        }

        public string KindLabel => Profile.Type == EndpointType.Local ? "LOCAL" : "SSH";
        public string HostSummary => Profile.Type == EndpointType.Local
            ? "Local Docker"
            : $"{Profile.SshUsername}@{Profile.HostIp}:{Profile.SshPort}";
        public string StatusText { get; set; } = "Ready";
    }
}
