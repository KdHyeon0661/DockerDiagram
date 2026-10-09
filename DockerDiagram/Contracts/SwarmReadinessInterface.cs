using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    public interface IDockerCliProbe
    {
        Task<DockerCliProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
    }

    public interface ISwarmReadinessAuditService
    {
        Task<SwarmReadinessReport> AuditAsync(
            IDockerService service,
            CancellationToken cancellationToken = default);
    }
}
