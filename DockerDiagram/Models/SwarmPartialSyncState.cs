namespace DockerDiagram.Models
{
    public static class SwarmPartialSyncState
    {
        private const string Prefix = "Partial sync:";

        public static IReadOnlyList<string> Parse(string status)
        {
            if (string.IsNullOrWhiteSpace(status) ||
                !status.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }

            return status[Prefix.Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string Format(IEnumerable<string> failures)
        {
            List<string> distinct = failures
                .Where(failure => !string.IsNullOrWhiteSpace(failure))
                .Select(failure => failure.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return distinct.Count == 0
                ? $"Last updated: {DateTime.Now:HH:mm:ss}"
                : $"{Prefix} {string.Join(", ", distinct)}";
        }
    }
}
