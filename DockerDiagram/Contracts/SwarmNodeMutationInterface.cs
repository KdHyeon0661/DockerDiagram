using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    public interface ISwarmNodeMutationService
    {
        Task<SwarmNodeEditSnapshot> InspectSwarmNodeAsync(
            string nodeId,
            CancellationToken cancellationToken = default);

        Task<SwarmNodeMutationResult> UpdateSwarmNodeAsync(
            SwarmNodeUpdateOptions options,
            CancellationToken cancellationToken = default);

        Task RemoveSwarmNodeAsync(
            string nodeId,
            bool force = false,
            CancellationToken cancellationToken = default);
    }
}
