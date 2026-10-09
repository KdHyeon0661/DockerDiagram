using Docker.DotNet;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace DockerDiagram.Infrastructure
{
    public partial class DockerApiService : ISwarmPlacementConstraintMutationService
    {
        public async Task<SwarmServicePlacementSnapshot> LoadSwarmServicePlacementAsync(
            string serviceId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serviceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(serviceId));
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            JObject service;
            try
            {
                service = await MakeRawDockerRequestAsync<JObject>(
                    HttpMethod.Get,
                    $"services/{Uri.EscapeDataString(serviceId.Trim())}",
                    cancellationToken: cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmMutationError("inspect placement", ex);
            }

            return ReadPlacementSnapshot(service, serviceId.Trim());
        }

        public async Task<SwarmServiceMutationResult> UpdateSwarmServicePlacementAsync(
            SwarmServicePlacementUpdateOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            string serviceId = options.ServiceId.Trim();
            JObject current;
            try
            {
                current = await MakeRawDockerRequestAsync<JObject>(
                    HttpMethod.Get,
                    $"services/{Uri.EscapeDataString(serviceId)}",
                    cancellationToken: cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmMutationError("inspect placement", ex);
            }

            ulong actualVersion = ReadServiceVersion(current, serviceId);
            if (actualVersion != options.Version)
                throw new SwarmServiceVersionConflictException(serviceId, options.Version, actualVersion);

            JObject desiredSpec = (JObject)(current["Spec"] as JObject
                ?? throw new InvalidOperationException("현재 Swarm service spec 정보를 찾을 수 없습니다."))
                .DeepClone();
            JObject taskTemplate = GetOrCreatePlacementObject(desiredSpec, "TaskTemplate");
            JObject placement = GetOrCreatePlacementObject(taskTemplate, "Placement");
            placement["Constraints"] = new JArray(options.Constraints.Select(value => value.Trim()));

            try
            {
                await MakeRawDockerApiRequestAsync(
                    HttpMethod.Post,
                    $"services/{Uri.EscapeDataString(serviceId)}/update?version={actualVersion}",
                    desiredSpec,
                    cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmMutationError("update placement", ex);
            }

            var warnings = new List<string>();
            ulong updatedVersion = await TryReadServiceVersionAfterMutationAsync(serviceId, warnings);
            return SwarmServiceMutationResult.Create(serviceId, updatedVersion, warnings);
        }

        private static SwarmServicePlacementSnapshot ReadPlacementSnapshot(JObject service, string serviceId)
        {
            JArray? constraints = service["Spec"]?["TaskTemplate"]?["Placement"]?["Constraints"] as JArray;
            return new SwarmServicePlacementSnapshot
            {
                ServiceId = service["ID"]?.Value<string>() ?? serviceId,
                ServiceName = service["Spec"]?["Name"]?.Value<string>() ?? serviceId,
                Version = ReadServiceVersion(service, serviceId),
                Constraints = constraints?.Values<string>()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .ToArray()
                    ?? Array.Empty<string>()
            };
        }

        private static JObject GetOrCreatePlacementObject(JObject parent, string propertyName)
        {
            if (parent[propertyName] is JObject existing) return existing;
            var created = new JObject();
            parent[propertyName] = created;
            return created;
        }
    }
}
