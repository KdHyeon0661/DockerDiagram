using System.Text.RegularExpressions;

namespace DockerDiagram.Models
{
    public enum SwarmStackDeployState
    {
        Draft,
        Deploying,
        Deployed,
        Missing,
        Error
    }

    public sealed class SwarmStackDeploymentOptions
    {
        public string StackName { get; init; } = string.Empty;
        public string Yaml { get; init; } = string.Empty;
        public string SourcePath { get; init; } = string.Empty;
        public bool Prune { get; init; } = true;
        public bool WithRegistryAuth { get; init; }
        public string ResolveImage { get; init; } = "always";

        public void Validate()
        {
            SwarmStackNamePolicy.Validate(StackName);
            if (string.IsNullOrWhiteSpace(Yaml))
                throw new ArgumentException("Stack YAML이 비어 있습니다.", nameof(Yaml));
            if (ResolveImage is not "always" and not "changed" and not "never")
                throw new ArgumentException("resolve-image 값은 always, changed, never 중 하나여야 합니다.", nameof(ResolveImage));
        }
    }

    public sealed record SwarmStackCommandResult(
        bool Success,
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public string CombinedOutput => string.Join(
            Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public sealed record SwarmStackRuntimeSnapshot(
        string StackName,
        IReadOnlyList<DockerContainer> Services,
        ulong DesiredTasks,
        ulong RunningTasks)
    {
        public string Summary => $"{Services.Count} services · {RunningTasks}/{DesiredTasks} running";
    }

    public static class SwarmStackNamePolicy
    {
        private static readonly Regex ValidName = new(
            "^[a-z0-9][a-z0-9_.-]{0,62}$",
            RegexOptions.CultureInvariant);

        public static void Validate(string value)
        {
            string name = value?.Trim() ?? string.Empty;
            if (!ValidName.IsMatch(name))
            {
                throw new ArgumentException(
                    "Stack 이름은 소문자 또는 숫자로 시작하고 소문자, 숫자, 점, 밑줄, 하이픈만 포함해야 합니다. 최대 63자입니다.",
                    nameof(value));
            }
        }

        public static string Suggest(string title, int fallbackOrdinal = 1)
        {
            string normalized = Regex.Replace(
                    (title ?? string.Empty).Trim().ToLowerInvariant(),
                    "[^a-z0-9_.-]+",
                    "-")
                .Trim('-', '.', '_');
            if (normalized.Length == 0 || !char.IsLetterOrDigit(normalized[0]))
                normalized = $"stack-{Math.Max(1, fallbackOrdinal)}";
            if (normalized.Length > 63) normalized = normalized[..63].TrimEnd('-', '.', '_');
            return normalized;
        }
    }

    public static class SwarmStackServiceSelector
    {
        public static IReadOnlyList<DockerContainer> Select(
            IEnumerable<DockerContainer> services,
            string stackName) =>
            services
                .Where(service => service.IsSwarmService)
                .Where(service => string.Equals(
                    service.ComposeProjectName,
                    stackName,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
