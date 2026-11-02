using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DockerDiagram.Common;
using DockerDiagram.Models;
using Newtonsoft.Json;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// Public HTTP transport for Docker Engine endpoints that Docker.DotNet does not expose.
    /// It deliberately avoids reflection over Docker.DotNet implementation details.
    /// </summary>
    internal sealed class DockerRawApiClient : IDisposable
    {
        private static readonly TimeSpan ConnectTimeout = DockerConnectionTiming.RawApiConnectTimeout;
        private readonly HttpClient _httpClient;
        private readonly SocketsHttpHandler _handler;
        private readonly X509Certificate2? _clientCertificate;

        public DockerRawApiClient(Uri endpoint, ConnectionProfile profile, TimeSpan requestTimeout)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(profile);

            SocketsHttpHandler? pendingHandler = null;
            HttpClient? pendingHttpClient = null;
            X509Certificate2? pendingClientCertificate = null;

            try
            {
                pendingHandler = new SocketsHttpHandler
                {
                    ConnectTimeout = ConnectTimeout,
                    PooledConnectionIdleTimeout = DockerConnectionTiming.HttpConnectionIdleTimeout,
                    PooledConnectionLifetime = DockerConnectionTiming.HttpConnectionLifetime
                };

                Uri baseAddress;
                if (endpoint.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase))
                {
                    string pipeName = GetNamedPipeName(endpoint);
                    pendingHandler.ConnectCallback = async (_, cancellationToken) =>
                    {
                        var stream = new NamedPipeClientStream(
                            ".",
                            pipeName,
                            PipeDirection.InOut,
                            PipeOptions.Asynchronous);
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeout.CancelAfter(ConnectTimeout);
                            await stream.ConnectAsync(timeout.Token).ConfigureAwait(false);
                            return stream;
                        }
                        catch
                        {
                            stream.Dispose();
                            throw;
                        }
                    };
                    baseAddress = new Uri("http://docker/");
                }
                else if (endpoint.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase))
                {
                    string socketPath = Uri.UnescapeDataString(endpoint.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(socketPath) || !Path.IsPathRooted(socketPath))
                        throw new NotSupportedException($"지원하지 않는 Docker Unix socket 경로입니다: {endpoint}");

                    pendingHandler.ConnectCallback = async (_, cancellationToken) =>
                    {
                        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        try
                        {
                            await socket.ConnectAsync(
                                new UnixDomainSocketEndPoint(socketPath),
                                cancellationToken).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    };
                    baseAddress = new Uri("http://docker/");
                }
                else
                {
                    if (!endpoint.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase) &&
                        !endpoint.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                        !endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException($"지원하지 않는 Docker endpoint 형식입니다: {endpoint.Scheme}");
                    }

                    bool useTls = endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                                  profile.DockerTlsEnabled;
                    string scheme = useTls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
                    baseAddress = new UriBuilder(endpoint)
                    {
                        Scheme = scheme,
                        Port = endpoint.IsDefaultPort ? -1 : endpoint.Port,
                        Path = string.Empty,
                        Query = string.Empty
                    }.Uri;

                    // HTTPS may rely only on the operating system trust store. A client
                    // certificate is required only for contexts explicitly configured
                    // for Docker mutual TLS.
                    if (useTls && profile.DockerTlsEnabled)
                    {
                        if (string.IsNullOrWhiteSpace(profile.DockerTlsCertPath) ||
                            string.IsNullOrWhiteSpace(profile.DockerTlsKeyPath) ||
                            !File.Exists(profile.DockerTlsCertPath) ||
                            !File.Exists(profile.DockerTlsKeyPath))
                        {
                            throw new InvalidOperationException(
                                $"Docker context '{profile.DockerContextName ?? profile.Name}'의 TLS 클라이언트 인증서를 찾을 수 없습니다.");
                        }

                        pendingClientCertificate = X509Certificate2.CreateFromPemFile(
                            profile.DockerTlsCertPath,
                            profile.DockerTlsKeyPath);
                        pendingHandler.SslOptions.ClientCertificates =
                        [
                            pendingClientCertificate
                        ];
                        pendingHandler.SslOptions.RemoteCertificateValidationCallback =
                            (_, certificate, chain, errors) => DockerApiService.ValidateDockerServerCertificate(
                                profile,
                                certificate,
                                chain,
                                errors);
                    }
                }

                pendingHttpClient = new HttpClient(pendingHandler, disposeHandler: false);
                pendingHttpClient.BaseAddress = baseAddress;
                pendingHttpClient.Timeout = requestTimeout;

                _handler = pendingHandler;
                pendingHandler = null;
                _clientCertificate = pendingClientCertificate;
                pendingClientCertificate = null;
                _httpClient = pendingHttpClient;
                pendingHttpClient = null;
            }
            finally
            {
                pendingHttpClient?.Dispose();
                pendingHandler?.Dispose();
                pendingClientCertificate?.Dispose();
            }
        }
        public async Task<string> SendAsync(
            HttpMethod method,
            string path,
            object? body,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Docker API 경로가 비어 있습니다.", nameof(path));
            if (Uri.TryCreate(path, UriKind.Absolute, out _))
                throw new ArgumentException("Docker API 경로는 상대 경로여야 합니다.", nameof(path));

            using var request = new HttpRequestMessage(
                method,
                new Uri(path.TrimStart('/'), UriKind.Relative));
            if (body != null)
            {
                request.Content = new StringContent(
                    JsonConvert.SerializeObject(body),
                    Encoding.UTF8,
                    "application/json");
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string detail = TryGetDockerErrorMessage(responseBody);
                throw new HttpRequestException(
                    $"Docker API {method} /{path.TrimStart('/')} 요청 실패 " +
                    $"({(int)response.StatusCode} {response.ReasonPhrase}): {detail}",
                    inner: null,
                    response.StatusCode);
            }

            return responseBody;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _handler.Dispose();
            _clientCertificate?.Dispose();
        }

        private static string GetNamedPipeName(Uri endpoint)
        {
            string path = Uri.UnescapeDataString(endpoint.AbsolutePath)
                .Replace('\\', '/')
                .Trim('/');
            if (path.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase))
                path = path["pipe/".Length..];

            if (string.IsNullOrWhiteSpace(path) || path.Contains('/'))
                throw new NotSupportedException($"지원하지 않는 Docker named pipe 경로입니다: {endpoint}");
            return path;
        }

        private static string TryGetDockerErrorMessage(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return "응답 본문이 없습니다.";

            try
            {
                var error = JsonConvert.DeserializeAnonymousType(
                    responseBody,
                    new { message = string.Empty });
                return string.IsNullOrWhiteSpace(error?.message)
                    ? responseBody.Trim()
                    : error.message.Trim();
            }
            catch (JsonException)
            {
                return responseBody.Trim();
            }
        }
    }
}
