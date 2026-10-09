using Docker.DotNet;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;

namespace DockerDiagram.Infrastructure
{
    public partial class DockerApiService : ISwarmNodeMutationService
    {
        public async Task<SwarmNodeEditSnapshot> InspectSwarmNodeAsync(
            string nodeId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("Swarm node ID가 비어 있습니다.", nameof(nodeId));
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            JObject node;
            try
            {
                node = await MakeRawDockerRequestAsync<JObject>(
                    HttpMethod.Get,
                    $"nodes/{Uri.EscapeDataString(nodeId.Trim())}",
                    cancellationToken: cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmNodeMutationError("inspect", ex);
            }

            SwarmClusterState cluster = await GetSwarmStateCoreAsync(cancellationToken);
            return ReadNodeSnapshot(node, cluster.NodeId);
        }

        public async Task<SwarmNodeMutationResult> UpdateSwarmNodeAsync(
            SwarmNodeUpdateOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            string nodeId = options.NodeId.Trim();
            JObject current;
            JArray nodes;
            SwarmClusterState cluster;
            try
            {
                current = await MakeRawDockerRequestAsync<JObject>(
                    HttpMethod.Get,
                    $"nodes/{Uri.EscapeDataString(nodeId)}",
                    cancellationToken: cancellationToken);
                nodes = await MakeRawDockerRequestAsync<JArray>(
                    HttpMethod.Get,
                    "nodes",
                    cancellationToken: cancellationToken);
                cluster = await GetSwarmStateCoreAsync(cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmNodeMutationError("inspect", ex);
            }

            SwarmNodeEditSnapshot snapshot = ReadNodeSnapshot(current, cluster.NodeId);
            if (snapshot.Version != options.Version)
                throw new SwarmNodeVersionConflictException(nodeId, options.Version, snapshot.Version);

            JObject[] managers = nodes.OfType<JObject>()
                .Where(node => string.Equals(
                    node["Spec"]?["Role"]?.Value<string>(),
                    "manager",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            bool IsReachableManager(JObject node)
            {
                bool ready = string.Equals(
                    node["Status"]?["State"]?.Value<string>(),
                    "ready",
                    StringComparison.OrdinalIgnoreCase);
                bool leader = node["ManagerStatus"]?["Leader"]?.Value<bool?>() ?? false;
                bool reachable = string.Equals(
                    node["ManagerStatus"]?["Reachability"]?.Value<string>(),
                    "reachable",
                    StringComparison.OrdinalIgnoreCase);
                return ready && (leader || reachable);
            }
            int reachableManagerCount = managers.Count(IsReachableManager);
            bool targetManagerReachable = managers.Any(node =>
                string.Equals(node["ID"]?.Value<string>(), nodeId, StringComparison.OrdinalIgnoreCase) &&
                IsReachableManager(node));
            SwarmNodeSafetyPolicy.ValidateUpdate(
                snapshot,
                options,
                new SwarmManagerQuorumState(managers.Length, reachableManagerCount, targetManagerReachable));

            JObject currentSpec = current["Spec"] as JObject
                ?? throw new InvalidOperationException($"Swarm node '{nodeId}'의 spec 정보를 찾을 수 없습니다.");
            JObject desiredSpec = (JObject)currentSpec.DeepClone();
            desiredSpec["Role"] = options.Role.Trim().ToLowerInvariant();
            desiredSpec["Availability"] = options.Availability.Trim().ToLowerInvariant();
            desiredSpec["Labels"] = JObject.FromObject(options.Labels);

            try
            {
                await MakeRawDockerApiRequestAsync(
                    HttpMethod.Post,
                    $"nodes/{Uri.EscapeDataString(nodeId)}/update?version={snapshot.Version}",
                    desiredSpec,
                    cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmNodeMutationError("update", ex);
            }

            var warnings = new List<string>();
            ulong newVersion = 0;
            try
            {
                JObject updated = await MakeRawDockerRequestAsync<JObject>(
                    HttpMethod.Get,
                    $"nodes/{Uri.EscapeDataString(nodeId)}",
                    cancellationToken: CancellationToken.None);
                newVersion = ReadNodeVersion(updated, nodeId);
            }
            catch (Exception ex)
            {
                warnings.Add(
                    "Node 변경은 Docker Engine에 접수되었지만 최신 version 확인에 실패했습니다. " +
                    $"리소스를 새로고침해 주세요. ({ex.GetBaseException().Message})");
            }
            return SwarmNodeMutationResult.Create(nodeId, newVersion, warnings);
        }

        public async Task RemoveSwarmNodeAsync(
            string nodeId,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("Swarm node ID가 비어 있습니다.", nameof(nodeId));
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            SwarmNodeEditSnapshot snapshot = await InspectSwarmNodeAsync(nodeId, cancellationToken);
            SwarmNodeSafetyPolicy.ValidateRemoval(snapshot, force);
            try
            {
                await MakeRawDockerApiRequestAsync(
                    HttpMethod.Delete,
                    $"nodes/{Uri.EscapeDataString(nodeId.Trim())}?force={force.ToString().ToLowerInvariant()}",
                    cancellationToken: cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmNodeMutationError("remove", ex);
            }
        }

        private static SwarmNodeEditSnapshot ReadNodeSnapshot(JObject node, string localNodeId)
        {
            string nodeId = node["ID"]?.Value<string>()?.Trim() ?? string.Empty;
            if (nodeId.Length == 0)
                throw new InvalidOperationException("Docker Engine의 node 응답에 ID가 없습니다.");
            JObject? spec = node["Spec"] as JObject;
            JObject? description = node["Description"] as JObject;
            JObject? status = node["Status"] as JObject;
            JObject? manager = node["ManagerStatus"] as JObject;
            var labels = (spec?["Labels"] as JObject)?.Properties().ToDictionary(
                property => property.Name,
                property => property.Value.Value<string>() ?? string.Empty,
                StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            bool isLeader = manager?["Leader"]?.Value<bool?>() ?? false;
            string reachability = manager?["Reachability"]?.Value<string>() ?? string.Empty;

            return new SwarmNodeEditSnapshot
            {
                NodeId = nodeId,
                Version = ReadNodeVersion(node, nodeId),
                Hostname = description?["Hostname"]?.Value<string>() ?? nodeId,
                Role = spec?["Role"]?.Value<string>() ?? "worker",
                Availability = spec?["Availability"]?.Value<string>() ?? "active",
                Status = status?["State"]?.Value<string>() ?? string.Empty,
                ManagerStatus = isLeader ? "leader" : reachability,
                IsLeader = isLeader,
                IsLocalNode = nodeId.Equals(localNodeId, StringComparison.OrdinalIgnoreCase),
                Labels = labels
            };
        }

        private static ulong ReadNodeVersion(JObject node, string nodeId) =>
            node["Version"]?["Index"]?.Value<ulong>()
            ?? throw new InvalidOperationException($"Swarm node '{nodeId}'의 version 정보를 찾을 수 없습니다.");

        private static SwarmNodeMutationException TranslateSwarmNodeMutationError(
            string operation,
            DockerApiException exception)
        {
            int statusCode = (int)exception.StatusCode;
            string engineMessage = ReadDockerErrorMessage(exception.ResponseBody, exception.Message);
            string summary = exception.StatusCode switch
            {
                HttpStatusCode.BadRequest => "Docker Engine이 Swarm node 작업을 거부했습니다.",
                HttpStatusCode.NotFound => "대상 Swarm node를 찾을 수 없습니다.",
                HttpStatusCode.Conflict => "Swarm node가 다른 작업에서 변경됐거나 현재 상태에서 작업할 수 없습니다.",
                HttpStatusCode.ServiceUnavailable => "현재 Docker Engine에서 Swarm Manager API를 사용할 수 없습니다.",
                _ => "Docker Engine의 Swarm node 작업이 실패했습니다."
            };
            string message = string.IsNullOrWhiteSpace(engineMessage)
                ? summary
                : $"{summary}\nDocker: {engineMessage}";
            return new SwarmNodeMutationException(operation, statusCode, message, exception);
        }
    }
}
