using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockerDiagram.Common;
using DockerDiagram.Models;

namespace DockerDiagram.Infrastructure
{
    public class DockerContextService
    {
        private static readonly char[] separator = new[] { '\r', '\n' };

        public static async Task<List<DockerContextInfo>> ListContextsAsync()
        {
            var result = await RunDockerAsync("context", "ls", "--format", "{{json .}}");
            var contexts = new List<DockerContextInfo>();

            foreach (var line in result.Stdout.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                contexts.Add(new DockerContextInfo
                {
                    Name = GetString(root, "Name"),
                    Description = GetString(root, "Description"),
                    DockerEndpoint = GetString(root, "DockerEndpoint"),
                    Error = GetString(root, "Error"),
                    IsCurrent = IsCurrentValue(GetString(root, "Current"))
                });
            }

            return contexts;
        }

        public static async Task UseContextAsync(string name)
        {
            await RunDockerAsync("context", "use", name);
        }

        public static async Task CreateContextAsync(string name, string dockerEndpoint)
        {
            await RunDockerAsync("context", "create", name, "--docker", $"host={dockerEndpoint}");
        }

        public static async Task RemoveContextAsync(string name)
        {
            await RunDockerAsync("context", "rm", name, "-f");
        }

        public static async Task<DockerContextInfo> InspectContextAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Docker context 이름이 필요합니다.", nameof(name));

            DockerCliResult result = await RunDockerAsync("context", "inspect", name);
            return ParseContextInspect(result.Stdout, name);
        }

        internal static DockerContextInfo ParseContextInspect(string json, string fallbackName = "")
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    throw new InvalidOperationException("Docker context inspect 결과가 비어 있습니다.");
                root = root[0];
            }

            var context = new DockerContextInfo
            {
                Name = GetString(root, "Name")
            };
            if (string.IsNullOrWhiteSpace(context.Name))
                context.Name = fallbackName;

            if (root.TryGetProperty("Endpoints", out JsonElement endpoints) &&
                endpoints.TryGetProperty("docker", out JsonElement dockerEndpoint))
            {
                context.DockerEndpoint = GetString(dockerEndpoint, "Host");
                context.TlsSkipVerify = dockerEndpoint.TryGetProperty("SkipTLSVerify", out JsonElement skipTls) &&
                                            skipTls.ValueKind == JsonValueKind.True;
            }

            if (root.TryGetProperty("Storage", out JsonElement storage))
                context.TlsPath = GetString(storage, "TLSPath");

            if (root.TryGetProperty("TLSMaterial", out JsonElement tlsMaterial) &&
                tlsMaterial.TryGetProperty("docker", out JsonElement dockerTls) &&
                dockerTls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement file in dockerTls.EnumerateArray())
                {
                    string? fileName = file.GetString();
                    if (!string.IsNullOrWhiteSpace(fileName))
                        context.TlsFiles.Add(fileName);
                }
            }

            context.TlsEnabled = context.TlsFiles.Count > 0 ||
                                 Uri.TryCreate(context.DockerEndpoint, UriKind.Absolute, out Uri? endpointUri) &&
                                 endpointUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            return context;
        }

        internal static string? ResolveTlsFilePath(DockerContextInfo context, string fileName)
        {
            if (!context.TlsFiles.Any(file => string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase)))
                return null;

            string listedFile = context.TlsFiles.First(file =>
                string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase));
            if (Path.IsPathRooted(listedFile) && File.Exists(listedFile))
                return listedFile;

            string[] candidates =
            {
                Path.Combine(context.TlsPath, listedFile),
                Path.Combine(context.TlsPath, "docker", listedFile)
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static async Task<DockerCliResult> RunDockerAsync(params string[] arguments)
        {
            using var timeout = new CancellationTokenSource(DockerConnectionTiming.ContextQueryTimeout);
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("docker 프로세스를 시작할 수 없습니다.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception killException)
                {
                    AppLog.Warning("Docker context CLI 프로세스 종료 실패", killException);
                }

                throw new TimeoutException("Docker context 명령이 30초 안에 완료되지 않아 중단했습니다.");
            }

            var result = new DockerCliResult(await stdoutTask, await stderrTask, process.ExitCode);
            if (result.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
                throw new InvalidOperationException(message.Trim());
            }

            return result;
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) ? value.ToString() : "";
        }

        private static bool IsCurrentValue(string value)
        {
            return value == "*" ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private record DockerCliResult(string Stdout, string Stderr, int ExitCode);
    }
}
