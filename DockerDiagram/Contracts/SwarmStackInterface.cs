using DockerDiagram.Models;

namespace DockerDiagram.Contracts
{
    public interface ISwarmStackDeploymentService
    {
        Task<SwarmStackCommandResult> DeployAsync(
            SwarmStackDeploymentOptions options,
            ConnectionProfile profile,
            CancellationToken cancellationToken = default);

        Task<SwarmStackCommandResult> RemoveAsync(
            string stackName,
            ConnectionProfile profile,
            CancellationToken cancellationToken = default);
    }

    public interface ISwarmStackCommandRunner
    {
        Task<SwarmStackCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            ConnectionProfile profile,
            string? workingDirectory,
            CancellationToken cancellationToken);
    }
}
