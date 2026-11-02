using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.Infrastructure
{
    public sealed partial class DockerCliSwarmStackService : ISwarmStackDeploymentService
    {
        private static readonly Regex ComposeVariablePattern = MyRegex();

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

            SwarmStackPreflightReport preflight = SwarmStackPreflightAnalyzer.Analyze(options.Yaml);
            if (preflight.BlockingIssues.Count > 0)
            {
                throw new ArgumentException(
                    string.Join(Environment.NewLine, preflight.BlockingIssues.Select(issue => issue.Message)),
                    nameof(options));
            }

            string? ownedTemporaryDirectory = null;
            var ownedTemporaryFiles = new List<string>();
            string inputPath;
            string workingDirectory;

            if (!string.IsNullOrWhiteSpace(options.SourcePath))
            {
                string sourcePath = Path.GetFullPath(options.SourcePath.Trim());
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException(
                        "Stack 원본 YAML을 찾을 수 없습니다. Stack 설정에서 YAML 파일을 다시 선택해 주세요.",
                        sourcePath);
                }

                workingDirectory = Path.GetDirectoryName(sourcePath)
                    ?? throw new InvalidOperationException("Stack 원본 YAML 폴더를 확인할 수 없습니다.");
                string sourceYaml = await File.ReadAllTextAsync(sourcePath, cancellationToken);
                if (string.Equals(sourceYaml, options.Yaml, StringComparison.Ordinal))
                {
                    // 수정하지 않은 YAML은 원본 파일을 그대로 사용한다. Docker Compose와 마찬가지로
                    // 상대경로가 원본 YAML 폴더를 기준으로 해석되고 읽기 전용 폴더에서도 동작한다.
                    inputPath = sourcePath;
                }
                else
                {
                    // 앱에서 편집한 YAML도 상대경로 기준이 바뀌지 않도록 원본과 같은 폴더에
                    // 충돌하지 않는 임시 파일을 만들고 실행 후 제거한다.
                    inputPath = CreateTemporaryPath(workingDirectory, "input");
                    ownedTemporaryFiles.Add(inputPath);
                }
            }
            else
            {
                // 직접 작성한 Draft에는 기준 폴더가 없으므로 앱 임시 폴더를 사용한다.
                ownedTemporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "DockerDiagram",
                    "swarm-stack",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ownedTemporaryDirectory);
                workingDirectory = ownedTemporaryDirectory;
                inputPath = Path.Combine(ownedTemporaryDirectory, "stack.yml");
                ownedTemporaryFiles.Add(inputPath);
            }

            try
            {
                if (ownedTemporaryFiles.Contains(inputPath, StringComparer.OrdinalIgnoreCase))
                {
                    await WriteTemporaryYamlAsync(
                        inputPath,
                        options.Yaml,
                        hasRememberedSource: !string.IsNullOrWhiteSpace(options.SourcePath),
                        cancellationToken);
                }

                string composePath = inputPath;
                if (RequiresComposeInterpolation(options.Yaml))
                {
                    string envFilePath = Path.Combine(workingDirectory, ".env");
                    SwarmStackCommandResult renderResult = await _runner.RunAsync(
                        SwarmStackCommandBuilder.BuildComposeConfigArguments(
                            inputPath,
                            File.Exists(envFilePath) ? envFilePath : null),
                        profile,
                        workingDirectory,
                        cancellationToken);

                    if (!renderResult.Success)
                    {
                        return new SwarmStackCommandResult(
                            false,
                            renderResult.ExitCode,
                            renderResult.StandardOutput,
                            "Stack YAML/.env 해석에 실패했습니다." + Environment.NewLine + renderResult.StandardError);
                    }

                    string renderedYaml = RemoveComposeProjectName(renderResult.StandardOutput);
                    if (string.IsNullOrWhiteSpace(renderedYaml))
                    {
                        return new SwarmStackCommandResult(
                            false,
                            renderResult.ExitCode,
                            string.Empty,
                            "docker compose config가 배포할 YAML을 반환하지 않았습니다.");
                    }

                    composePath = CreateTemporaryPath(workingDirectory, "resolved");
                    ownedTemporaryFiles.Add(composePath);
                    await WriteTemporaryYamlAsync(
                        composePath,
                        renderedYaml,
                        hasRememberedSource: !string.IsNullOrWhiteSpace(options.SourcePath),
                        cancellationToken);
                }

                IReadOnlyList<string> arguments = SwarmStackCommandBuilder.BuildDeployArguments(options, composePath);
                return await _runner.RunAsync(arguments, profile, workingDirectory, cancellationToken);
            }
            finally
            {
                foreach (string temporaryFile in ownedTemporaryFiles)
                {
                    try { File.Delete(temporaryFile); }
                    catch (Exception ex) { AppLog.Warning($"Swarm stack 임시 파일 정리 실패: {temporaryFile}", ex); }
                }

                if (ownedTemporaryDirectory != null)
                {
                    try { Directory.Delete(ownedTemporaryDirectory, recursive: true); }
                    catch (Exception ex) { AppLog.Warning($"Swarm stack 임시 폴더 정리 실패: {ownedTemporaryDirectory}", ex); }
                }
            }
        }

        internal static bool RequiresComposeInterpolation(string yaml) =>
            !string.IsNullOrWhiteSpace(yaml) && ComposeVariablePattern.IsMatch(yaml);

        internal static string RemoveComposeProjectName(string renderedYaml)
        {
            if (string.IsNullOrEmpty(renderedYaml)) return string.Empty;

            string newline = renderedYaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string[] lines = renderedYaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            bool removed = false;
            var kept = new List<string>(lines.Length);
            foreach (string line in lines)
            {
                if (!removed && line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    continue;
                }
                kept.Add(line);
            }
            return string.Join(newline, kept);
        }

        private static string CreateTemporaryPath(string directory, string purpose) =>
            Path.Combine(directory, $".dockerdiagram-stack-{purpose}-{Guid.NewGuid():N}.yml");

        private static async Task WriteTemporaryYamlAsync(
            string path,
            string yaml,
            bool hasRememberedSource,
            CancellationToken cancellationToken)
        {
            try
            {
                await File.WriteAllTextAsync(path, yaml, new UTF8Encoding(false), cancellationToken);
            }
            catch (Exception ex) when (hasRememberedSource && ex is UnauthorizedAccessException or IOException)
            {
                throw new InvalidOperationException(
                    "Stack YAML을 배포하려면 원본 YAML 폴더에 임시 파일을 만들 수 있어야 합니다. " +
                    "폴더 쓰기 권한을 확인하거나 원본 YAML을 수정한 뒤 다시 선택해 주세요.",
                    ex);
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

        [GeneratedRegex(@"(?<!\$)\$(?:[A-Za-z_][A-Za-z0-9_]*|\{[^}\r\n]+\})", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
        private static partial Regex MyRegex();
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
                catch (Exception killException)
                {
                    AppLog.Warning("취소된 Swarm stack 프로세스 종료 실패", killException);
                }
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

