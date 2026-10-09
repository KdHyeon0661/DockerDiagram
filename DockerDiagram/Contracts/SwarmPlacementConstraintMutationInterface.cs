using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    public interface ISwarmPlacementConstraintMutationService
    {
        Task<SwarmServicePlacementSnapshot> LoadSwarmServicePlacementAsync(
            string serviceId,
            CancellationToken cancellationToken = default);

        Task<SwarmServiceMutationResult> UpdateSwarmServicePlacementAsync(
            SwarmServicePlacementUpdateOptions options,
            CancellationToken cancellationToken = default);
    }
}
