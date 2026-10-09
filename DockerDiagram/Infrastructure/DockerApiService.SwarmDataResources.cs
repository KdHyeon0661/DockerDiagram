using Docker.DotNet;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;

namespace DockerDiagram.Infrastructure
{
    public partial class DockerApiService : ISwarmDataResourceMutationService
    {
        public async Task<SwarmDataResourceMutationResult> CreateSwarmDataResourceAsync(
            SwarmDataResourceCreateOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            string collection = ResourceCollection(options.Kind);
            var body = new JObject
            {
                ["Name"] = options.Name.Trim(),
                ["Labels"] = JObject.FromObject(options.Labels),
                ["Data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(options.Data))
            };

            string response;
            try
            {
                response = await MakeRawDockerApiRequestAsync(
                    HttpMethod.Post,
                    $"{collection}/create",
                    body,
                    cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmMutationError($"create {options.Kind.ToString().ToLowerInvariant()}", ex);
            }

            string id = JObject.Parse(response)["ID"]?.Value<string>()?.Trim() ?? string.Empty;
            if (id.Length == 0)
                throw new InvalidOperationException($"Docker Engine의 {options.Kind} 생성 응답에 ID가 없습니다.");
            return new SwarmDataResourceMutationResult(id, options.Kind, options.Name.Trim());
        }

        public async Task RemoveSwarmDataResourceAsync(
            SwarmDataResourceKind kind,
            string resourceId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
                throw new ArgumentException("Swarm resource ID가 비어 있습니다.", nameof(resourceId));
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            try
            {
                await MakeRawDockerApiRequestAsync(
                    HttpMethod.Delete,
                    $"{ResourceCollection(kind)}/{Uri.EscapeDataString(resourceId.Trim())}",
                    cancellationToken: cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmMutationError($"remove {kind.ToString().ToLowerInvariant()}", ex);
            }
        }

        public async Task<SwarmServiceMutationResult> UpdateSwarmServiceTopologyAsync(
            SwarmServiceTopologyUpdateOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            string serviceId = options.Service.ServiceId.Trim();
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
                throw TranslateSwarmMutationError("inspect", ex);
            }

            ulong actualVersion = ReadServiceVersion(current, serviceId);
            ulong expectedVersion = options.Service.Version!.Value;
            if (actualVersion != expectedVersion)
                throw new SwarmServiceVersionConflictException(serviceId, expectedVersion, actualVersion);

            JObject currentSpec = current["Spec"] as JObject
                ?? throw new InvalidOperationException("현재 Swarm service spec 정보를 찾을 수 없습니다.");
            JObject desired = SwarmServiceSpecMapper.MergeForUpdate(currentSpec, options.Service.Spec);
            JObject task = GetOrCreate(desired, "TaskTemplate");
            JObject container = GetOrCreate(task, "ContainerSpec");
            if (options.ApplySecrets) container["Secrets"] = BuildReferences(options.Secrets, "Secret");
            if (options.ApplyConfigs) container["Configs"] = BuildReferences(options.Configs, "Config");

            string response;
            try
            {
                response = await MakeRawDockerApiRequestAsync(
                    HttpMethod.Post,
                    $"services/{Uri.EscapeDataString(serviceId)}/update?version={actualVersion}",
                    desired,
                    cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmMutationError("update topology", ex);
            }

            var warnings = SwarmServiceMutationResponseParser.ParseUpdateWarnings(response).ToList();
            ulong version = await TryReadServiceVersionAfterMutationAsync(serviceId, warnings);
            return SwarmServiceMutationResult.Create(serviceId, version, warnings);
        }

        private static string ResourceCollection(SwarmDataResourceKind kind) => kind switch
        {
            SwarmDataResourceKind.Secret => "secrets",
            SwarmDataResourceKind.Config => "configs",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        private static JObject GetOrCreate(JObject parent, string propertyName)
        {
            if (parent[propertyName] is JObject current) return current;
            var created = new JObject();
            parent[propertyName] = created;
            return created;
        }

        private static JArray BuildReferences(
            IEnumerable<SwarmServiceResourceReferenceOptions> references,
            string prefix) =>
            new(references.Select(reference => new JObject
            {
                [$"{prefix}ID"] = reference.ResourceId.Trim(),
                [$"{prefix}Name"] = reference.ResourceName.Trim(),
                ["File"] = new JObject
                {
                    ["Name"] = reference.FileName.Trim(),
                    ["UID"] = reference.Uid,
                    ["GID"] = reference.Gid,
                    ["Mode"] = reference.Mode
                }
            }));
    }
}

