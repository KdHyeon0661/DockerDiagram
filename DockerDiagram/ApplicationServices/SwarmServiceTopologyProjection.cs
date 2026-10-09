using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.ApplicationServices
{
    public sealed record SwarmServiceDataReference(
        SwarmDataResourceKind Kind,
        string ResourceId,
        string ResourceName,
        string FileName,
        string Uid,
        string Gid,
        uint Mode);

    public sealed record SwarmServiceTopologyProjection(
        IReadOnlyList<SwarmPublishedPortOptions> PublishedPorts,
        IReadOnlyList<SwarmMountOptions> Mounts,
        IReadOnlyList<SwarmNetworkAttachmentOptions> Networks,
        IReadOnlyList<SwarmServiceDataReference> DataReferences);

    /// <summary>
    /// Projects the parts of a live ServiceSpec that the diagram can represent.
    /// Secret/config payloads are deliberately never read; only immutable IDs and target metadata are used.
    /// </summary>
    public static class SwarmServiceTopologyProjectionReader
    {
        public static SwarmServiceTopologyProjection Read(SwarmServiceEditSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            JObject root = JObject.Parse(snapshot.RawJson);
            JToken? container = root["Spec"]?["TaskTemplate"]?["ContainerSpec"];
            var references = new List<SwarmServiceDataReference>();
            ReadReferences(container?["Secrets"], SwarmDataResourceKind.Secret, references);
            ReadReferences(container?["Configs"], SwarmDataResourceKind.Config, references);

            return new SwarmServiceTopologyProjection(
                snapshot.Spec.PublishedPorts,
                snapshot.Spec.Mounts,
                snapshot.Spec.Networks,
                references);
        }

        private static void ReadReferences(
            JToken? token,
            SwarmDataResourceKind kind,
            ICollection<SwarmServiceDataReference> destination)
        {
            if (token is not JArray items) return;
            string prefix = kind == SwarmDataResourceKind.Secret ? "Secret" : "Config";
            foreach (JObject item in items.OfType<JObject>())
            {
                string id = item[$"{prefix}ID"]?.Value<string>()?.Trim() ?? string.Empty;
                string name = item[$"{prefix}Name"]?.Value<string>()?.Trim() ?? string.Empty;
                JObject? file = item["File"] as JObject;
                if (id.Length == 0 || file == null) continue;

                string effectiveName = name.Length == 0 ? id : name;
                string fileName = file["Name"]?.Value<string>()?.Trim() ?? effectiveName;
                string uid = file["UID"]?.Value<string>()?.Trim() ?? "0";
                string gid = file["GID"]?.Value<string>()?.Trim() ?? "0";
                uint mode = file["Mode"]?.Value<uint?>() ?? 292U;
                destination.Add(new SwarmServiceDataReference(
                    kind,
                    id,
                    effectiveName,
                    fileName.Length == 0 ? effectiveName : fileName,
                    uid.Length == 0 ? "0" : uid,
                    gid.Length == 0 ? "0" : gid,
                    mode));
            }
        }
    }
}
