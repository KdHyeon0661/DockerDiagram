namespace DockerDiagram.Models
{
    public sealed record SwarmDataResourceSnapshot(
        string Id,
        string Name,
        SwarmDataResourceKind Kind,
        ulong Version,
        DateTimeOffset? UpdatedAt,
        IReadOnlyDictionary<string, string> Labels);

    public sealed record SwarmIdentityMatch<T>(T? Resource, bool ReboundByName)
        where T : class;

    public static class SwarmRuntimeIdentityMatcher
    {
        public static SwarmIdentityMatch<T> Match<T>(
            IEnumerable<T> resources,
            string currentId,
            string currentName,
            Func<T, string> idSelector,
            Func<T, string> nameSelector)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(resources);
            ArgumentNullException.ThrowIfNull(idSelector);
            ArgumentNullException.ThrowIfNull(nameSelector);

            List<T> snapshot = resources.ToList();
            if (!string.IsNullOrWhiteSpace(currentId))
            {
                T? idMatch = snapshot.FirstOrDefault(resource =>
                    string.Equals(idSelector(resource), currentId, StringComparison.OrdinalIgnoreCase));
                if (idMatch != null) return new SwarmIdentityMatch<T>(idMatch, false);
            }

            if (string.IsNullOrWhiteSpace(currentName))
                return new SwarmIdentityMatch<T>(null, false);

            List<T> nameMatches = snapshot
                .Where(resource => string.Equals(
                    nameSelector(resource),
                    currentName,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();

            return nameMatches.Count == 1
                ? new SwarmIdentityMatch<T>(nameMatches[0], true)
                : new SwarmIdentityMatch<T>(null, false);
        }
    }
}
