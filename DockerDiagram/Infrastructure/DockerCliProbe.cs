using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System.Diagnostics;

namespace DockerDiagram.Infrastructure
{
    public sealed class DockerCliProbe : IDockerCliProbe
    {
        public async Task<DockerCliProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--version");

                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                    return new DockerCliProbeResult(false, string.Empty, "Docker CLI process did not start.");

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
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

                string output = (await outputTask).Trim();
                string error = (await errorTask).Trim();
                return process.ExitCode == 0
                    ? new DockerCliProbeResult(true, ParseVersion(output), string.Empty)
                    : new DockerCliProbeResult(false, string.Empty, error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DockerCliProbeResult(false, string.Empty, ex.GetBaseException().Message);
            }
        }

        private static string ParseVersion(string output)
        {
            string prefix = "Docker version ";
            if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return output;
            int comma = output.IndexOf(',', prefix.Length);
            return comma > prefix.Length
                ? output[prefix.Length..comma].Trim()
                : output[prefix.Length..].Trim();
        }
    }
}
