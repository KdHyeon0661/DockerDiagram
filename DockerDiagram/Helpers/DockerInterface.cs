using Docker.DotNet.Models;
using DockerDiagram.Models;

namespace DockerDiagram.Helpers
{
    // 1. 컨테이너 전용 인터페이스 (DockerContainer 사용)
    public interface IContainerService
    {
        Task<List<DockerContainer>> GetContainersAsync();

        Task<ContainerInspectResponse> InspectContainerAsync(string containerId);

        // 제어 기능 (컨테이너만 가능)
        Task StartContainerAsync(string id);
        Task StopContainerAsync(string id);
        Task PauseContainerAsync(string id);
        Task UnpauseContainerAsync(string id);
        Task RestartContainerAsync(string id);
        Task RemoveContainerAsync(string id);

        // 생성 (이미지 객체 없이 문자열로 받음)
        Task<string> CreateAndStartContainerAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes, string restartPolicy, long memoryMb, double cpuCount);

        // 유틸
        Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath);
        Task CopyToContainerAsync(string containerId, string hostPath, string containerPath);
        void OpenTerminal(string containerId);
        Task ExecuteCommandAsync(string containerId, string command);
        Task<ContainerStats> GetContainerStatsAsync(string containerId);
    }

    // 2. 볼륨 전용 인터페이스 (DockerVolume 사용)
    public interface IVolumeService
    {
        // [모델 일치] 포트/이미지 속성이 없는 DockerVolume 반환
        Task<List<DockerVolume>> GetVolumesAsync();

        Task<VolumeResponse> InspectVolumeAsync(string name);
        Task CreateVolumeAsync(string name, string driver);
        Task RemoveVolumeAsync(string name);

        Task<List<string>> GetContainersUsingVolumeAsync(string volumeName);
    }

    // 3. 네트워크(그룹) 전용 인터페이스 (DockerGroup 사용)
    public interface INetworkService
    {
        // [모델 일치] DockerGroup 반환 (이게 네트워크임)
        Task<List<DockerGroup>> GetNetworksAsync();

        Task<NetworkResponse> InspectNetworkAsync(string networkId);
        Task<string> CreateNetworkAsync(string name, string driver);
        Task RemoveNetworkAsync(string id);

        // 그룹 연결 관리
        Task ConnectNetworkAsync(string networkId, string containerId, string? staticIp = null);
        Task DisconnectNetworkAsync(string networkId, string containerId);
    }

    // 4. 시스템 기본 (Ping)
    public interface ISystemService
    {
        Task<bool> PingAsync();
    }

    // 4. 도커 이미지 인터페이스
    public interface IImageService
    {
        Task<List<DockerImage>> GetImagesAsync();

        Task PullImageAsync(string image, string tag);
        Task DeleteImageAsync(string imageId, bool force = false);
    }
}