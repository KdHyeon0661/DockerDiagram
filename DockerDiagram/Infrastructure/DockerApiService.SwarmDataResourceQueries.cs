using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace DockerDiagram.Infrastructure
{
    public partial class DockerApiService : ISwarmDataResourceQueryService
    {
        public async Task<IReadOnlyList<SwarmDataResourceSnapshot>> GetSwarmDataResourcesAsync(
            SwarmDataResourceKind kind,
            CancellationToken cancellationToken = default)
        {
            JArray resources = await MakeRawDockerRequestAsync<JArray>(
                HttpMethod.Get,
                ResourceCollection(kind),
                cancellationToken: cancellationToken);

            return resources
                .OfType<JObject>()
                .Select(resource =>
                {
                    string id = resource["ID"]?.Value<string>()?.Trim() ?? string.Empty;
                    string name = resource["Spec"]?["Name"]?.Value<string>()?.Trim() ?? id;
                    ulong version = resource["Version"]?["Index"]?.Value<ulong>() ?? 0;
                    DateTimeOffset? updatedAt = DateTimeOffset.TryParse(
                        resource["UpdatedAt"]?.Value<string>(),
                        out DateTimeOffset parsed)
                        ? parsed
                        : null;
                    var labels = resource["Spec"]?["Labels"] is JObject labelsObject
                        ? labelsObject.Properties().ToDictionary(
                            property => property.Name,
                            property => property.Value.Value<string>() ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    return new SwarmDataResourceSnapshot(id, name, kind, version, updatedAt, labels);
                })
                .Where(resource => !string.IsNullOrWhiteSpace(resource.Id))
                .OrderBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
