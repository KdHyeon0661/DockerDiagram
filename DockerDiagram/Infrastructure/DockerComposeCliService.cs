using System.Diagnostics;
using System.IO;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.Infrastructure
{
    public class DockerComposeCliService : IComposeService
    {
        private static readonly TimeSpan CommandTimeout = ContainerCreationTiming.ComposeCommandTimeout;

        public async Task<ComposeCommandResult> UpAsync(
            string composeFilePath,
            ConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            ProcessStartInfo startInfo = CreateStartInfo(composeFilePath, profile);
            using var process = new Process { StartInfo = startInfo };
            using var timeout = new CancellationTokenSource(CommandTimeout);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);

            if (!process.Start())
                throw new InvalidOperationException("docker compose 프로세스를 시작할 수 없습니다.");

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(operation.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(operation.Token);

            try
            {
                await process.WaitForExitAsync(operation.Token);
                await Task.WhenAll(standardOutput, standardError);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception killException)
                {
                    AppLog.Warning("취소된 docker compose 프로세스 종료 실패", killException);
                }

                if (cancellationToken.IsCancellationRequested)
                    throw;

                throw new TimeoutException(
                    $"docker compose up이 {CommandTimeout.TotalMinutes:0}분 안에 완료되지 않아 중단했습니다.");
            }

            return new ComposeCommandResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                StandardOutput = await standardOutput,
                StandardError = await standardError
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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(composeFilePath)
            };
            startInfo.ArgumentList.Add("compose");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(composeFilePath);
            startInfo.ArgumentList.Add("up");
            startInfo.ArgumentList.Add("-d");
            DockerCliTargetEnvironment.Apply(startInfo, profile);
            return startInfo;
        }
    }
}
