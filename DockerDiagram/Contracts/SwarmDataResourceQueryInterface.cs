using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    public interface ISwarmDataResourceQueryService
    {
        Task<IReadOnlyList<SwarmDataResourceSnapshot>> GetSwarmDataResourcesAsync(
            SwarmDataResourceKind kind,
            CancellationToken cancellationToken = default);
    }
}
