using Docker.DotNet.Models;
using DockerDiagram.Models;

namespace DockerDiagram.Helpers
{
    public interface IDockerService
    {
        // 1. 조회 및 연결 확인
        Task<bool> PingAsync();
        Task<List<DockerContainer>> GetContainersAsync();
        Task<List<DockerImage>> GetImagesAsync();
        Task<List<DockerContainer>> GetVolumesAsync();
        Task<List<DockerContainer>> GetNetworksAsync();

        // 2. 상세 조회
        Task<ContainerInspectResponse> InspectContainerAsync(string containerId);
        Task<VolumeResponse> InspectVolumeAsync(string name);
        Task<NetworkResponse> InspectNetworkAsync(string networkId);
        Task<List<string>> GetContainersUsingVolumeAsync(string volumeName);

        // 3. 컨테이너 제어
        Task StartContainerAsync(string id);
        Task StopContainerAsync(string id);
        Task PauseContainerAsync(string id);
        Task UnpauseContainerAsync(string id);
        Task RestartContainerAsync(string id);
        Task RemoveContainerAsync(string id);

        // 4. 생성 및 관리
        Task PullImageAsync(string image, string tag);
        Task<string> CreateAndStartContainerAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes, string restartPolicy, long memoryMb, double cpuCount);
        Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath);
        Task CopyToContainerAsync(string containerId, string hostPath, string containerPath);
        Task CreateVolumeAsync(string name, string driver);
        Task<string> CreateNetworkAsync(string name, string driver);

        // 5. 삭제
        Task DeleteImageAsync(string imageId, bool force = false);
        Task RemoveVolumeAsync(string name);
        Task RemoveNetworkAsync(string id);

        // 6. 터미널 및 명령
        void OpenTerminal(string containerId);
        Task ExecuteCommandAsync(string containerId, string command);

        // 7. 네트워크 연결 관리 (ConnectorViewModel에서 사용)
        Task ConnectNetworkAsync(string networkId, string containerId, string? staticIp = null);
        Task DisconnectNetworkAsync(string networkId, string containerId);
    }

    public interface IDialogService
    {
        void ShowMessage(string message);

        bool ShowConfirm(string message, string title);
    }
}