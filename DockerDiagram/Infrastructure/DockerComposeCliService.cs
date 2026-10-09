using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DockerDiagram.Infrastructure;

public class DockerComposeCliService : IComposeService
{
    public async Task<ComposeCommandResult> UpAsync(string composeFilePath, ConnectionProfile profile)
    {
        ProcessStartInfo startInfo = CreateStartInfo(composeFilePath, profile);
        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrEmpty(eventArgs.Data)) outputBuilder.AppendLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrEmpty(eventArgs.Data)) errorBuilder.AppendLine(eventArgs.Data);
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

    internal static ProcessStartInfo CreateStartInfo(
        string composeFilePath,
        ConnectionProfile profile)
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
        DockerCliTargetEnvironment.Apply(startInfo, profile);
        return startInfo;
    }
}
