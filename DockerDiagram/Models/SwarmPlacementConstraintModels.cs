using System.Text.RegularExpressions;

namespace DockerDiagram.Models
{
    public sealed class SwarmServicePlacementSnapshot
    {
        public string ServiceId { get; init; } = string.Empty;
        public string ServiceName { get; init; } = string.Empty;
        public ulong Version { get; init; }
        public IReadOnlyList<string> Constraints { get; init; } = Array.Empty<string>();
    }

    public sealed class SwarmServicePlacementUpdateOptions
    {
        private static readonly Regex ConstraintPattern = new(
            @"^[A-Za-z0-9_.-]+\s*(==|!=)\s*[^\s].*$",
            RegexOptions.CultureInvariant);

        public string ServiceId { get; init; } = string.Empty;
        public ulong Version { get; init; }
        public IReadOnlyList<string> Constraints { get; init; } = Array.Empty<string>();

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ServiceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(ServiceId));
            foreach (string constraint in Constraints)
            {
                string normalized = constraint?.Trim() ?? string.Empty;
                if (normalized.Length == 0 || !ConstraintPattern.IsMatch(normalized))
                    throw new ArgumentException(
                        $"Placement constraint 형식이 올바르지 않습니다: {constraint}",
                        nameof(Constraints));
            }
            if (Constraints.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Count() != Constraints.Count)
                throw new ArgumentException("중복된 placement constraint가 있습니다.", nameof(Constraints));
        }
    }
}
