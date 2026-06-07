using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DockerDiagram.Helpers
{
    public class DockerContextService
    {
        public async Task<List<DockerContextInfo>> ListContextsAsync()
        {
            var result = await RunDockerAsync("context", "ls", "--format", "{{json .}}");
            var contexts = new List<DockerContextInfo>();

            foreach (var line in result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
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

        public async Task UseContextAsync(string name)
        {
            await RunDockerAsync("context", "use", name);
        }

        public async Task CreateContextAsync(string name, string dockerEndpoint)
        {
            await RunDockerAsync("context", "create", name, "--docker", $"host={dockerEndpoint}");
        }

        public async Task RemoveContextAsync(string name)
        {
            await RunDockerAsync("context", "rm", name, "-f");
        }

        private static async Task<DockerCliResult> RunDockerAsync(params string[] arguments)
        {
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

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

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
