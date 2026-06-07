using System.Diagnostics;
using System.IO;
using System.Text;
using DockerDiagram.Models;

namespace DockerDiagram.Helpers
{
    public class DockerComposeCliService : IComposeService
    {
        public async Task<ComposeCommandResult> UpAsync(string composeFilePath, ConnectionProfile profile)
        {
            if (string.IsNullOrWhiteSpace(composeFilePath) || !File.Exists(composeFilePath))
                throw new FileNotFoundException("Compose 파일을 찾을 수 없습니다.", composeFilePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"compose -f \"{composeFilePath}\" up -d",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(composeFilePath)
            };

            ApplyDockerHost(startInfo, profile);

            using var process = new Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) errorBuilder.AppendLine(e.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException("docker compose 프로세스를 시작할 수 없습니다.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            return new ComposeCommandResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                StandardOutput = outputBuilder.ToString(),
                StandardError = errorBuilder.ToString()
            };
        }

        private static void ApplyDockerHost(ProcessStartInfo startInfo, ConnectionProfile profile)
        {
            if (profile.Type == EndpointType.SshRemote && profile.LocalTunnelPort > 0)
            {
                startInfo.Environment["DOCKER_HOST"] = $"tcp://127.0.0.1:{profile.LocalTunnelPort}";
                return;
            }

            if (profile.Type == EndpointType.DockerContext && !string.IsNullOrWhiteSpace(profile.DockerEndpoint))
            {
                startInfo.Environment["DOCKER_HOST"] = profile.DockerEndpoint;
            }
        }
    }
}
