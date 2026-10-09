using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    public interface ISwarmTaskDiagnosticService
    {
        Task<SwarmTaskDiagnosticReport> GetSwarmTaskDiagnosticsAsync(
            string serviceId,
            CancellationToken cancellationToken = default);

        Task<string> GetSwarmServiceLogsAsync(
            string serviceId,
            int tailCount = 500,
            CancellationToken cancellationToken = default);

        Task<string> GetSwarmTaskContainerLogsAsync(
            string serviceId,
            string taskId,
            int tailCount = 500,
            CancellationToken cancellationToken = default);
    }
}
