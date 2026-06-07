using System;
using System.Collections.Generic;
using System.Threading; // ★ 누락되었던 네임스페이스 추가
using System.Threading.Tasks;
using Docker.DotNet.Models;
using DockerDiagram.Models;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 도커 엔진과의 통신을 담당하는 최상위 서비스 인터페이스입니다.
    /// 하위 서비스(컨테이너, 볼륨, 네트워크, 이미지, 시스템)들을 하나로 묶어 제공하며, 자원 해제(IDisposable)를 지원합니다.
    /// </summary>
    public interface IDockerService : IContainerService, IVolumeService, INetworkService, IImageService, ISystemService, IDisposable
    {
    }

    /// <summary>
    /// 컨테이너의 생성, 실행, 중지, 삭제 등 전체 생명주기를 관리하는 인터페이스입니다.
    /// </summary>
    public interface IContainerService
    {
        Task<List<DockerContainer>> GetContainersAsync();
        Task<ContainerInspectResponse> InspectContainerAsync(string containerId);
        Task StartContainerAsync(string id);
        Task StopContainerAsync(string id);
        Task PauseContainerAsync(string id);
        Task UnpauseContainerAsync(string id);
        Task RestartContainerAsync(string id);
        Task KillContainerAsync(string id, string signal = "SIGKILL");
        Task RenameContainerAsync(string id, string newName);
        Task RemoveContainerAsync(string id);
        Task<string> CommitContainerAsync(string containerId, string repository, string tag, string comment, string author, bool pause);
        Task ExportContainerAsync(string containerId, string tarFilePath);

        Task<string> CreateAndStartContainerAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes, string restartPolicy, long memoryMb, double cpuCount, string command = "", bool tty = false);
        Task UpdateContainerResourcesAsync(string containerId, double cpuCount, long memoryMb);

        Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath);
        Task CopyToContainerAsync(string containerId, string hostPath, string containerPath);
        void OpenTerminal(string containerId);
        Task ExecuteCommandAsync(string containerId, string command);
        Task<ExecCommandResult> ExecuteCommandWithOutputAsync(string containerId, string command);
        Task<ContainerStats> GetContainerStatsAsync(string containerId);
        Task<string> GetContainerLogsAsync(string containerId, int tailCount = 500);
        Task<SystemInfoResponse> GetSystemInfoAsync();
        Task StreamContainerLogsAsync(string containerId, Action<string> onLogLineReceived, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 데이터를 영구적으로 저장하기 위한 도커 볼륨을 관리하는 인터페이스입니다.
    /// </summary>
    public interface IVolumeService
    {
        Task<List<DockerVolume>> GetVolumesAsync();
        Task<VolumeResponse> InspectVolumeAsync(string name);
        Task CreateVolumeAsync(string name, string driver);
        Task CreateVolumeAsync(VolumeCreateOptions options);
        Task RemoveVolumeAsync(string name, bool force = false);
        Task<List<string>> GetContainersUsingVolumeAsync(string volumeName);
        Task<List<VolumeUsageInfo>> GetVolumeUsageDetailsAsync(string volumeName);
        Task BackupVolumeAsync(string volumeName, string hostTarFilePath);
        Task RestoreVolumeAsync(string volumeName, string hostTarFilePath);
    }

    /// <summary>
    /// 컨테이너 간 통신을 위한 도커 네트워크(다이어그램의 그룹)를 관리하는 인터페이스입니다.
    /// </summary>
    public interface INetworkService
    {
        Task<List<DockerNetworkGroup>> GetNetworksAsync();
        Task<NetworkResponse> InspectNetworkAsync(string networkId);
        Task<string> CreateNetworkAsync(string name, string driver);
        Task<string> CreateNetworkAsync(NetworkCreateOptions options);
        Task RemoveNetworkAsync(string id);
        Task ConnectNetworkAsync(string networkId, string containerId, ContainerNetworkOptions? options = null);
        Task DisconnectNetworkAsync(string networkId, string containerId);
    }

    /// <summary>
    /// 도커 엔진의 기본 상태를 확인하는 시스템 인터페이스입니다.
    /// </summary>
    public interface ISystemService
    {
        Task<bool> PingAsync();
        Task MonitorDockerEventsAsync(IProgress<Message> progress, CancellationToken cancellationToken);
        Task<SystemDiskUsage> GetSystemDiskUsageAsync();
        Task<DockerPruneResult> PruneAsync(DockerPruneOptions options);
    }

    /// <summary>
    /// 도커 이미지를 관리하는 인터페이스입니다.
    /// </summary>
    public interface IImageService
    {
        Task<List<DockerImage>> GetImagesAsync();
        Task PullImageAsync(string image, string tag, string? username = null, string? password = null, string? serverAddress = null);
        Task DeleteImageAsync(string imageId, bool force = false);
        Task ImportImageFromTarAsync(string tarFilePath, string repository, string tag, string message);
        Task TagImageAsync(string sourceImage, string repository, string tag, bool force = true);
        Task PushImageAsync(string repository, string tag, string? username = null, string? password = null, string? serverAddress = null);
        Task SaveImageAsync(string image, string tarFilePath);
        Task LoadImageFromTarAsync(string tarFilePath);
        Task BuildImageAsync(string targetImageName, string buildContextPath, string dockerfilePath, IProgress<JSONMessage>? progress = null);
        Task<List<ImageSearchResponse>> SearchImagesAsync(string term, int limit = 20);
        Task PullImageWithProgressAsync(string image, string tag, IProgress<JSONMessage> progress, string? username = null, string? password = null, string? serverAddress = null);
    }
}
