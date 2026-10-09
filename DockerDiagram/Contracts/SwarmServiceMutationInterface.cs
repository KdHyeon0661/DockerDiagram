using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    /// <summary>Swarm 조회 계약과 분리된 service 변경 capability입니다.</summary>
    public interface ISwarmServiceMutationService
    {
        Task<SwarmServiceMutationResult> CreateSwarmServiceAsync(
            SwarmServiceCreateOptions options,
            CancellationToken cancellationToken = default);

        Task<SwarmServiceMutationResult> UpdateSwarmServiceAsync(
            SwarmServiceUpdateOptions options,
            CancellationToken cancellationToken = default);
    }
}
