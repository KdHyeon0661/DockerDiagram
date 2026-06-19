using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DockerDiagram.Models;

namespace DockerDiagram.Helpers
{
    internal static class RegistryImageMetadataReader
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private const string ManifestAccept =
            "application/vnd.oci.image.index.v1+json, " +
            "application/vnd.oci.image.manifest.v1+json, " +
            "application/vnd.docker.distribution.manifest.list.v2+json, " +
            "application/vnd.docker.distribution.manifest.v2+json";

        public static async Task<ContainerImageMetadata?> ReadAsync(
            string imageReference,
            string? username,
            string? password,
            string? serverAddress,
            CancellationToken cancellationToken)
        {
            var reference = ParseReference(imageReference, serverAddress);
            using JsonDocument manifest = await GetJsonAsync(
                reference,
                $"/v2/{reference.Repository}/manifests/{reference.Tag}",
                ManifestAccept,
                username,
                password,
                cancellationToken);

            JsonElement manifestRoot = manifest.RootElement;
            if (manifestRoot.TryGetProperty("manifests", out JsonElement manifests))
            {
                string? digest = SelectManifestDigest(manifests);
                if (string.IsNullOrWhiteSpace(digest))
                    return null;

                using JsonDocument platformManifest = await GetJsonAsync(
                    reference,
                    $"/v2/{reference.Repository}/manifests/{digest}",
                    ManifestAccept,
                    username,
                    password,
                    cancellationToken);
                manifestRoot = platformManifest.RootElement.Clone();
            }

            if (!manifestRoot.TryGetProperty("config", out JsonElement configDescriptor) ||
                !configDescriptor.TryGetProperty("digest", out JsonElement digestElement))
            {
                return null;
            }

            string? configDigest = digestElement.GetString();
            if (string.IsNullOrWhiteSpace(configDigest))
                return null;

            using JsonDocument configDocument = await GetJsonAsync(
                reference,
                $"/v2/{reference.Repository}/blobs/{configDigest}",
                "application/vnd.oci.image.config.v1+json, application/vnd.docker.container.image.v1+json",
                username,
                password,
                cancellationToken);

            if (!configDocument.RootElement.TryGetProperty("config", out JsonElement config))
                return null;

            return new ContainerImageMetadata
            {
                ImageReference = imageReference,
                Source = reference.Registry,
                Environment = ReadStringArray(config, "Env"),
                ExposedPorts = ReadObjectKeys(config, "ExposedPorts"),
                Volumes = ReadObjectKeys(config, "Volumes"),
                Entrypoint = ReadStringArray(config, "Entrypoint"),
                Command = ReadStringArray(config, "Cmd")
            };
        }

        private static async Task<JsonDocument> GetJsonAsync(
            RegistryReference reference,
            string path,
            string accept,
            string? username,
            string? password,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage initialResponse = await SendAsync(
                reference.BaseUri,
                path,
                accept,
                null,
                username,
                password,
                cancellationToken);

            if (initialResponse.StatusCode != HttpStatusCode.Unauthorized)
            {
                initialResponse.EnsureSuccessStatusCode();
                return await ReadDocumentAsync(initialResponse, cancellationToken);
            }

            string challenge = initialResponse.Headers.WwwAuthenticate.ToString();
            AuthenticationHeaderValue? authorization =
                await CreateAuthorizationAsync(
                    challenge,
                    reference,
                    username,
                    password,
                    cancellationToken);

            if (authorization == null)
                throw new HttpRequestException("Registry 인증이 필요합니다.");

            using HttpResponseMessage authenticatedResponse = await SendAsync(
                reference.BaseUri,
                path,
                accept,
                authorization,
                username,
                password,
                cancellationToken);
            authenticatedResponse.EnsureSuccessStatusCode();
            return await ReadDocumentAsync(authenticatedResponse, cancellationToken);
        }

        private static async Task<HttpResponseMessage> SendAsync(
            Uri baseUri,
            string path,
            string accept,
            AuthenticationHeaderValue? authorization,
            string? username,
            string? password,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            request.Headers.TryAddWithoutValidation("Accept", accept);

            if (authorization != null)
            {
                request.Headers.Authorization = authorization;
            }
            else if (!string.IsNullOrWhiteSpace(username))
            {
                string credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{username}:{password ?? string.Empty}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            return await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }

        private static async Task<AuthenticationHeaderValue?> CreateAuthorizationAsync(
            string challenge,
            RegistryReference reference,
            string? username,
            string? password,
            CancellationToken cancellationToken)
        {
            if (challenge.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(username))
                    return null;

                string credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{username}:{password ?? string.Empty}"));
                return new AuthenticationHeaderValue("Basic", credentials);
            }

            if (!challenge.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
                return null;

            var parameters = Regex.Matches(challenge, @"(\w+)=""([^""]*)""")
                .ToDictionary(
                    match => match.Groups[1].Value,
                    match => match.Groups[2].Value,
                    StringComparer.OrdinalIgnoreCase);

            if (!parameters.TryGetValue("realm", out string? realm))
                return null;

            string service = parameters.TryGetValue("service", out string? serviceValue)
                ? serviceValue
                : reference.Registry;
            string scope = parameters.TryGetValue("scope", out string? scopeValue)
                ? scopeValue
                : $"repository:{reference.Repository}:pull";

            string tokenUrl =
                $"{realm}{(realm.Contains('?') ? '&' : '?')}service={Uri.EscapeDataString(service)}" +
                $"&scope={Uri.EscapeDataString(scope)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
            if (!string.IsNullOrWhiteSpace(username))
            {
                string credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{username}:{password ?? string.Empty}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using JsonDocument tokenDocument = await ReadDocumentAsync(response, cancellationToken);
            string? token = tokenDocument.RootElement.TryGetProperty("token", out JsonElement tokenValue)
                ? tokenValue.GetString()
                : tokenDocument.RootElement.TryGetProperty("access_token", out JsonElement accessTokenValue)
                    ? accessTokenValue.GetString()
                    : null;

            return string.IsNullOrWhiteSpace(token)
                ? null
                : new AuthenticationHeaderValue("Bearer", token);
        }

        private static async Task<JsonDocument> ReadDocumentAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        private static string? SelectManifestDigest(JsonElement manifests)
        {
            JsonElement? fallback = null;
            foreach (JsonElement manifest in manifests.EnumerateArray())
            {
                fallback ??= manifest;
                if (!manifest.TryGetProperty("platform", out JsonElement platform))
                    continue;

                string os = platform.TryGetProperty("os", out JsonElement osValue)
                    ? osValue.GetString() ?? string.Empty
                    : string.Empty;
                string architecture = platform.TryGetProperty("architecture", out JsonElement archValue)
                    ? archValue.GetString() ?? string.Empty
                    : string.Empty;
                if (os.Equals("linux", StringComparison.OrdinalIgnoreCase) &&
                    architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase))
                {
                    return manifest.GetProperty("digest").GetString();
                }
            }

            return fallback?.GetProperty("digest").GetString();
        }

        private static List<string> ReadStringArray(JsonElement config, string propertyName)
        {
            if (!config.TryGetProperty(propertyName, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        private static List<string> ReadObjectKeys(JsonElement config, string propertyName)
        {
            if (!config.TryGetProperty(propertyName, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return value.EnumerateObject().Select(property => property.Name).ToList();
        }

        private static RegistryReference ParseReference(
            string imageReference,
            string? serverAddress)
        {
            string repositoryWithRegistry;
            string tag;
            int digestSeparator = imageReference.IndexOf('@');
            if (digestSeparator > 0 && digestSeparator < imageReference.Length - 1)
            {
                repositoryWithRegistry = imageReference[..digestSeparator];
                tag = imageReference[(digestSeparator + 1)..];
            }
            else
            {
                (repositoryWithRegistry, tag) = DockerImageReferenceParser.Split(imageReference);
            }
            string normalized = repositoryWithRegistry.Trim().Trim('/');
            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

            string registry;
            string repository;
            if (parts.Length > 1 && LooksLikeRegistry(parts[0]))
            {
                registry = parts[0];
                repository = string.Join('/', parts.Skip(1));
            }
            else
            {
                registry = "registry-1.docker.io";
                repository = parts.Length == 1 ? $"library/{parts[0]}" : normalized;
            }

            if (registry.Equals("docker.io", StringComparison.OrdinalIgnoreCase) ||
                registry.Equals("index.docker.io", StringComparison.OrdinalIgnoreCase))
            {
                registry = "registry-1.docker.io";
            }

            Uri baseUri;
            if (!string.IsNullOrWhiteSpace(serverAddress) &&
                Uri.TryCreate(serverAddress, UriKind.Absolute, out Uri? configuredUri))
            {
                baseUri = new Uri($"{configuredUri.Scheme}://{configuredUri.Authority}/");
                registry = configuredUri.Authority;
            }
            else
            {
                string scheme = registry.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                                registry.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    ? "http"
                    : "https";
                baseUri = new Uri($"{scheme}://{registry}/");
            }

            return new RegistryReference(registry, repository, tag, baseUri);
        }

        private static bool LooksLikeRegistry(string value) =>
            value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('.') ||
            value.Contains(':');

        private sealed record RegistryReference(
            string Registry,
            string Repository,
            string Tag,
            Uri BaseUri);
    }
}
