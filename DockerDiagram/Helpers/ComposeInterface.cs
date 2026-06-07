using DockerDiagram.Models;

namespace DockerDiagram.Helpers
{
    public interface IComposeService
    {
        Task<ComposeCommandResult> UpAsync(string composeFilePath, ConnectionProfile profile);
    }

    public class ComposeCommandResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;

        public string CombinedOutput =>
            string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }
}
