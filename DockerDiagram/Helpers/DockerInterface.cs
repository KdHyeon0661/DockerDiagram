using System;
using System.Collections.Generic;
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
        Task<List<DockerContainer>> GetContainersAsync(); // 현재 도커 엔진에 있는 모든 컨테이너 목록을 가져옵니다.
        Task<ContainerInspectResponse> InspectContainerAsync(string containerId); // 특정 컨테이너의 상세 정보를 조회합니다.
        Task StartContainerAsync(string id); // 중지된 컨테이너를 시작합니다.
        Task StopContainerAsync(string id); // 실행 중인 컨테이너를 중지합니다.
        Task PauseContainerAsync(string id); // 컨테이너의 프로세스를 일시 정지(Pause)합니다.
        Task UnpauseContainerAsync(string id); // 일시 정지된 컨테이너를 다시 재개(Unpause)합니다.
        Task RestartContainerAsync(string id); // 컨테이너를 재시작합니다.
        Task RemoveContainerAsync(string id); // 컨테이너를 삭제합니다.

        // 새로운 컨테이너를 생성하고 즉시 실행합니다.
        Task<string> CreateAndStartContainerAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes, string restartPolicy, long memoryMb, double cpuCount, string command = "", bool tty = false);

        Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath); // 컨테이너 내부의 파일을 호스트 PC로 복사합니다.
        Task CopyToContainerAsync(string containerId, string hostPath, string containerPath); // 호스트 PC의 파일을 컨테이너 내부로 복사합니다.
        void OpenTerminal(string containerId); // 컨테이너 내부로 진입하는 터미널(쉘) 창을 엽니다.
        Task ExecuteCommandAsync(string containerId, string command); // 컨테이너 내부에서 특정 명령어를 1회성으로 실행합니다.
        Task<ContainerStats> GetContainerStatsAsync(string containerId); // 컨테이너의 실시간 자원 사용량(CPU, 메모리)을 조회합니다.
        Task<string> GetContainerLogsAsync(string containerId, int tailCount = 500); // 컨테이너의 실행 로그를 최근 500줄 가져옵니다.
    }

    /// <summary>
    /// 데이터를 영구적으로 저장하기 위한 도커 볼륨을 관리하는 인터페이스입니다.
    /// </summary>
    public interface IVolumeService
    {
        Task<List<DockerVolume>> GetVolumesAsync(); // 모든 볼륨 목록을 가져옵니다.
        Task<VolumeResponse> InspectVolumeAsync(string name); // 특정 볼륨의 상세 정보를 조회합니다.
        Task CreateVolumeAsync(string name, string driver); // 새로운 도커 볼륨을 생성합니다.
        Task RemoveVolumeAsync(string name); // 도커 볼륨을 삭제합니다.
        Task<List<string>> GetContainersUsingVolumeAsync(string volumeName); // 특정 볼륨을 마운트하여 사용 중인 컨테이너 ID 목록을 가져옵니다.
    }

    /// <summary>
    /// 컨테이너 간 통신을 위한 도커 네트워크(다이어그램의 그룹)를 관리하는 인터페이스입니다.
    /// </summary>
    public interface INetworkService
    {
        Task<List<DockerNetworkGroup>> GetNetworksAsync(); // DockerGroup -> DockerNetworkGroup으로 변경!
        Task<NetworkResponse> InspectNetworkAsync(string networkId);
        Task<string> CreateNetworkAsync(string name, string driver);
        Task RemoveNetworkAsync(string id);
        Task ConnectNetworkAsync(string networkId, string containerId, string? staticIp = null);
        Task DisconnectNetworkAsync(string networkId, string containerId);
    }

    /// <summary>
    /// 도커 엔진의 기본 상태를 확인하는 시스템 인터페이스입니다.
    /// </summary>
    public interface ISystemService
    {
        Task<bool> PingAsync(); // 도커 엔진이 정상적으로 연결되어 응답하는지(Ping) 확인합니다.
    }

    /// <summary>
    /// 도커 이미지를 관리하는 인터페이스입니다.
    /// </summary>
    public interface IImageService
    {
        Task<List<DockerImage>> GetImagesAsync(); // 로컬에 저장된 도커 이미지 목록을 가져옵니다.
        Task PullImageAsync(string image, string tag); // 레지스트리에서 새로운 이미지를 다운로드(Pull)합니다.
        Task DeleteImageAsync(string imageId, bool force = false); // 로컬에 저장된 이미지를 삭제합니다.
    }
}