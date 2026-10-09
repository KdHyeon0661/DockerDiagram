using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    public interface ISwarmDataResourceMutationService
    {
        Task<SwarmDataResourceMutationResult> CreateSwarmDataResourceAsync(
            SwarmDataResourceCreateOptions options,
            CancellationToken cancellationToken = default);

        Task RemoveSwarmDataResourceAsync(
            SwarmDataResourceKind kind,
            string resourceId,
            CancellationToken cancellationToken = default);

        Task<SwarmServiceMutationResult> UpdateSwarmServiceTopologyAsync(
            SwarmServiceTopologyUpdateOptions options,
            CancellationToken cancellationToken = default);
    }
}
