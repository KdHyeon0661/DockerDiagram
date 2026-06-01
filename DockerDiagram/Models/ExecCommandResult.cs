namespace DockerDiagram.Models
{
    public class ExecCommandResult
    {
        public long ExitCode { get; set; }
        public string Stdout { get; set; } = string.Empty;
        public string Stderr { get; set; } = string.Empty;
        public bool IsSuccess => ExitCode == 0;
    }
}
