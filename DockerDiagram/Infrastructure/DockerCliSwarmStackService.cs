using System.IO;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System.Diagnostics;
using System.Text;

namespace DockerDiagram.Infrastructure
{
    public sealed class DockerCliSwarmStackService : ISwarmStackDeploymentService
    {
        private readonly ISwarmStackCommandRunner _runner;

        public DockerCliSwarmStackService(ISwarmStackCommandRunner? runner = null)
        {
            _runner = runner ?? new DockerCliStackCommandRunner();
        }

        public async Task<SwarmStackCommandResult> DeployAsync(
            SwarmStackDeploymentOptions options,
            ConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(profile);
            options.Validate();

            string temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "DockerDiagram",
                "swarm-stack",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            string composePath = Path.Combine(temporaryDirectory, "stack.yml");

            try
            {
                await File.WriteAllTextAsync(composePath, options.Yaml, new UTF8Encoding(false), cancellationToken);
                IReadOnlyList<string> arguments = SwarmStackCommandBuilder.BuildDeployArguments(options, composePath);
                return await _runner.RunAsync(arguments, profile, temporaryDirectory, cancellationToken);
            }
            finally
            {
                try { Directory.Delete(temporaryDirectory, recursive: true); }
                catch (Exception ex) { Debug.WriteLine($"[SwarmStack] Temporary file cleanup failed: {ex.Message}"); }
            }
        }

        public Task<SwarmStackCommandResult> RemoveAsync(
            string stackName,
            ConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return _runner.RunAsync(
                SwarmStackCommandBuilder.BuildRemoveArguments(stackName),
                profile,
                null,
                cancellationToken);
        }
    }

    public sealed class DockerCliStackCommandRunner : ISwarmStackCommandRunner
    {
        public async Task<SwarmStackCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            ConnectionProfile profile,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : workingDirectory
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            DockerCliTargetEnvironment.Apply(startInfo, profile);

            using var process = new Process { StartInfo = startInfo };
            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrEmpty(eventArgs.Data)) standardOutput.AppendLine(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrEmpty(eventArgs.Data)) standardError.AppendLine(eventArgs.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException("Docker CLI 프로세스를 시작할 수 없습니다.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception) { }
                throw;
            }

            return new SwarmStackCommandResult(
                process.ExitCode == 0,
                process.ExitCode,
                standardOutput.ToString().Trim(),
                standardError.ToString().Trim());
        }

    }
}

