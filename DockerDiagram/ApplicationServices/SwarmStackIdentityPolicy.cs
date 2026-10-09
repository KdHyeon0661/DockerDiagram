using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static class SwarmStackIdentityPolicy
    {
        public static bool IsUnique(
            string proposedName,
            IEnumerable<string> existingNames)
        {
            if (string.IsNullOrWhiteSpace(proposedName)) return false;
            return !existingNames.Any(existing => string.Equals(
                existing?.Trim(),
                proposedName.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }

        public static string SuggestUnique(
            string preferredName,
            IEnumerable<string> existingNames,
            int fallbackOrdinal = 1)
        {
            var reserved = new HashSet<string>(
                existingNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()),
                StringComparer.OrdinalIgnoreCase);
            string baseName = SwarmStackNamePolicy.Suggest(preferredName, fallbackOrdinal);
            if (!reserved.Contains(baseName)) return baseName;

            for (int suffix = 2; suffix < int.MaxValue; suffix++)
            {
                string suffixText = $"-{suffix}";
                int maxBaseLength = 63 - suffixText.Length;
                string trimmedBase = baseName.Length <= maxBaseLength
                    ? baseName
                    : baseName[..maxBaseLength].TrimEnd('-', '.', '_');
                string candidate = trimmedBase + suffixText;
                if (!reserved.Contains(candidate)) return candidate;
            }

            throw new InvalidOperationException("사용 가능한 Stack 이름을 만들 수 없습니다.");
        }

        public static string DuplicateMessage(string stackName) =>
            $"같은 Swarm 연결에 Stack 이름 '{stackName}'이 이미 있습니다. 다른 이름을 사용해 주세요.";
    }
}


