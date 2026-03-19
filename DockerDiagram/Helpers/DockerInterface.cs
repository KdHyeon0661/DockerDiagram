using Docker.DotNet.Models;
using DockerDiagram.Models;

namespace DockerDiagram.Helpers
{
    public interface IDockerService : IContainerService, IVolumeService, INetworkService, IImageService, ISystemService, IDisposable
    {
    }

    // 1. 컨테이너 전용 인터페이스
    public interface IContainerService
    {
        Task<List<DockerContainer>> GetContainersAsync();
        Task<ContainerInspectResponse> InspectContainerAsync(string containerId);

        Task StartContainerAsync(string id);
        Task StopContainerAsync(string id);
        Task PauseContainerAsync(string id);
        Task UnpauseContainerAsync(string id);
        Task RestartContainerAsync(string id);
        Task RemoveContainerAsync(string id);

        Task<string> CreateAndStartContainerAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes, 
            string restartPolicy, long memoryMb, double cpuCount, string command = "", bool tty = false);

        Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath);
        Task CopyToContainerAsync(string containerId, string hostPath, string containerPath);
        void OpenTerminal(string containerId);
        Task ExecuteCommandAsync(string containerId, string command);
        Task<ContainerStats> GetContainerStatsAsync(string containerId);
        Task<string> GetContainerLogsAsync(string containerId, int tailCount = 500);
    }

    // 2. 볼륨 전용 인터페이스
    public interface IVolumeService
    {
        Task<List<DockerVolume>> GetVolumesAsync();
        Task<VolumeResponse> InspectVolumeAsync(string name);
        Task CreateVolumeAsync(string name, string driver);
        Task RemoveVolumeAsync(string name);
        Task<List<string>> GetContainersUsingVolumeAsync(string volumeName);
    }

    // 3. 네트워크(그룹) 전용 인터페이스
    public interface INetworkService
    {
        Task<List<DockerGroup>> GetNetworksAsync();
        Task<NetworkResponse> InspectNetworkAsync(string networkId);
        Task<string> CreateNetworkAsync(string name, string driver);
        Task RemoveNetworkAsync(string id);
        Task ConnectNetworkAsync(string networkId, string containerId, string? staticIp = null);
        Task DisconnectNetworkAsync(string networkId, string containerId);
    }

    // 4. 시스템 기본
    public interface ISystemService
    {
        Task<bool> PingAsync();
    }

    // 5. 이미지 인터페이스
    public interface IImageService
    {
        Task<List<DockerImage>> GetImagesAsync();
        Task PullImageAsync(string image, string tag);
        Task DeleteImageAsync(string imageId, bool force = false);
    }
}