using Docker.DotNet;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;

namespace DockerDiagram.Infrastructure
{
    public partial class DockerApiService : ISwarmServiceMutationService
    {
        public async Task<SwarmServiceMutationResult> CreateSwarmServiceAsync(
            SwarmServiceCreateOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureSwarmManagerForMutationAsync(cancellationToken);

            JObject serviceSpec = SwarmServiceSpecMapper.BuildForCreate(options);
            string responseBody;
            try
            {
                responseBody = await MakeRawDockerApiRequestAsync(
                    HttpMethod.Post,
                    "services/create",
                    serviceSpec,
                    cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmMutationError("create", ex);
            }

            SwarmServiceCreateApiResponse response = SwarmServiceMutationResponseParser.ParseCreate(responseBody);
            var warnings = response.Warnings.ToList();
            ulong version = await TryReadServiceVersionAfterMutationAsync(response.ServiceId, warnings);
            return SwarmServiceMutationResult.Create(response.ServiceId, version, warnings);
        }

        public async Task<SwarmServiceMutationResult> UpdateSwarmServiceAsync(
            SwarmServiceUpdateOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            cancellationToken.ThrowIfCancellationRequested();
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
                throw TranslateSwarmMutationError("inspect", ex);
            }

            ulong actualVersion = ReadServiceVersion(current, serviceId);
            ulong expectedVersion = options.Version!.Value;
            if (actualVersion != expectedVersion)
                throw new SwarmServiceVersionConflictException(serviceId, expectedVersion, actualVersion);

            JObject currentSpec = current["Spec"] as JObject
                ?? throw new InvalidOperationException("현재 Swarm service spec 정보를 찾을 수 없습니다.");
            JObject desiredSpec = SwarmServiceSpecMapper.MergeForUpdate(currentSpec, options.Spec);

            string responseBody;
            try
            {
                responseBody = await MakeRawDockerApiRequestAsync(
                    HttpMethod.Post,
                    $"services/{Uri.EscapeDataString(serviceId)}/update?version={actualVersion}",
                    desiredSpec,
                    cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw TranslateSwarmMutationError("update", ex);
            }

            var warnings = SwarmServiceMutationResponseParser.ParseUpdateWarnings(responseBody).ToList();
            ulong updatedVersion = await TryReadServiceVersionAfterMutationAsync(serviceId, warnings);
            return SwarmServiceMutationResult.Create(serviceId, updatedVersion, warnings);
        }

        private async Task EnsureSwarmManagerForMutationAsync(CancellationToken cancellationToken)
        {
            SwarmClusterState state = await GetSwarmStateCoreAsync(cancellationToken);
            if (state.IsManager) return;

            string detail = state.Membership == SwarmMembershipState.Worker
                ? "현재 연결은 Worker입니다. service 변경은 Manager에서만 수행할 수 있습니다."
                : $"현재 Docker Engine은 활성 Swarm Manager가 아닙니다. 상태: {state.Membership}.";
            throw new InvalidOperationException(detail);
        }

        private async Task<ulong> TryReadServiceVersionAfterMutationAsync(
            string serviceId,
            ICollection<string> warnings)
        {
            try
            {
                JObject current = await MakeRawDockerRequestAsync<JObject>(
                    HttpMethod.Get,
                    $"services/{Uri.EscapeDataString(serviceId)}",
                    cancellationToken: CancellationToken.None);
                return ReadServiceVersion(current, serviceId);
            }
            catch (Exception ex)
            {
                warnings.Add(
                    "Service 변경은 Docker Engine에 접수되었지만 최신 version 확인에 실패했습니다. " +
                    $"리소스를 새로고침해 주세요. ({ex.GetBaseException().Message})");
                return 0;
            }
        }

        private static ulong ReadServiceVersion(JObject service, string serviceId) =>
            service["Version"]?["Index"]?.Value<ulong>()
            ?? throw new InvalidOperationException($"Swarm service '{serviceId}'의 version 정보를 찾을 수 없습니다.");

        private static SwarmServiceMutationException TranslateSwarmMutationError(
            string operation,
            DockerApiException exception)
        {
            int statusCode = (int)exception.StatusCode;
            string engineMessage = ReadDockerErrorMessage(exception.ResponseBody, exception.Message);
            bool versionConflict = statusCode == (int)HttpStatusCode.Conflict ||
                                   engineMessage.Contains("update out of sequence", StringComparison.OrdinalIgnoreCase);

            string summary = versionConflict
                ? "Service가 다른 작업에서 변경되었거나 이름이 충돌합니다. 최신 상태를 새로고침한 뒤 다시 시도해 주세요."
                : exception.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "Docker Engine이 ServiceSpec을 거부했습니다.",
                    HttpStatusCode.NotFound => "대상 Swarm service를 찾을 수 없습니다.",
                    HttpStatusCode.ServiceUnavailable => "현재 Docker Engine에서 Swarm Manager API를 사용할 수 없습니다.",
                    _ => "Docker Engine의 Swarm service 작업이 실패했습니다."
                };

            string message = string.IsNullOrWhiteSpace(engineMessage)
                ? summary
                : $"{summary}\nDocker: {engineMessage}";
            return new SwarmServiceMutationException(operation, statusCode, message, exception);
        }

        private static string ReadDockerErrorMessage(string? responseBody, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                try
                {
                    string? message = JObject.Parse(responseBody)["message"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(message)) return message.Trim();
                }
                catch
                {
                    return responseBody.Trim();
                }
            }

            return fallback?.Trim() ?? string.Empty;
        }
    }
}
