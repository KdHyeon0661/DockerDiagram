using System.Text;
using System.Text.RegularExpressions;

namespace DockerDiagram.Models
{
    public enum SwarmDataResourceKind { Secret, Config }

    public sealed class SwarmDataResourceCreateOptions
    {
        private static readonly Regex ValidName = new(
            "^[A-Za-z0-9][A-Za-z0-9_.-]*$",
            RegexOptions.CultureInvariant);

        public SwarmDataResourceKind Kind { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Data { get; init; } = string.Empty;
        public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

        public void Validate()
        {
            string name = Name.Trim();
            if (name.Length == 0 || name.Length > 255 || !ValidName.IsMatch(name))
                throw new ArgumentException("이름은 영문자 또는 숫자로 시작하고 영문자, 숫자, 점, 밑줄, 하이픈만 포함해야 합니다.", nameof(Name));
            if (Data.Length == 0)
                throw new ArgumentException($"Swarm {Kind} 내용이 비어 있습니다.", nameof(Data));
            if (Encoding.UTF8.GetByteCount(Data) > 500 * 1024)
                throw new ArgumentException($"Swarm {Kind} 내용은 UTF-8 기준 500 KiB 이하여야 합니다.", nameof(Data));
            if (Labels.Any(label => string.IsNullOrWhiteSpace(label.Key)))
                throw new ArgumentException("Label key가 비어 있습니다.", nameof(Labels));
        }
    }

    public sealed record SwarmDataResourceMutationResult(
        string ResourceId,
        SwarmDataResourceKind Kind,
        string Name);

    public sealed record SwarmResourceTargetOptions(
        string FileName,
        string Uid = "0",
        string Gid = "0",
        uint Mode = 292);

    public sealed record SwarmServiceResourceReferenceOptions(
        string ResourceId,
        string ResourceName,
        string FileName,
        string Uid = "0",
        string Gid = "0",
        uint Mode = 292)
    {
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ResourceId)) throw new ArgumentException("Resource ID가 비어 있습니다.", nameof(ResourceId));
            if (string.IsNullOrWhiteSpace(ResourceName)) throw new ArgumentException("Resource 이름이 비어 있습니다.", nameof(ResourceName));
            if (string.IsNullOrWhiteSpace(FileName)) throw new ArgumentException("Container target 이름이 비어 있습니다.", nameof(FileName));
        }
    }

    public sealed class SwarmServiceTopologyUpdateOptions
    {
        public SwarmServiceUpdateOptions Service { get; init; } = new();
        public bool ApplySecrets { get; init; }
        public IReadOnlyList<SwarmServiceResourceReferenceOptions> Secrets { get; init; } = Array.Empty<SwarmServiceResourceReferenceOptions>();
        public bool ApplyConfigs { get; init; }
        public IReadOnlyList<SwarmServiceResourceReferenceOptions> Configs { get; init; } = Array.Empty<SwarmServiceResourceReferenceOptions>();

        public void Validate()
        {
            Service.Validate();
            foreach (SwarmServiceResourceReferenceOptions reference in Secrets) reference.Validate();
            foreach (SwarmServiceResourceReferenceOptions reference in Configs) reference.Validate();
            if (Secrets.GroupBy(reference => reference.FileName, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new ArgumentException("중복된 Secret target 이름이 있습니다.", nameof(Secrets));
            if (Configs.GroupBy(reference => reference.FileName, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new ArgumentException("중복된 Config target 이름이 있습니다.", nameof(Configs));
        }
    }
}
