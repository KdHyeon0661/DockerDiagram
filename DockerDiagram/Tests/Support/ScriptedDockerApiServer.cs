using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DockerDiagram.Tests;

internal sealed class ScriptedDockerApiServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<Response> _responses;
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly Task _acceptLoop;

    public ScriptedDockerApiServer(params Response[] responses)
    {
        _responses = new ConcurrentQueue<Response>(responses);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        DockerEndpoint = new Uri($"tcp://127.0.0.1:{port}");
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri DockerEndpoint { get; }
    public IReadOnlyList<Request> Requests => _requests.ToArray();

    public static Response Json(string body, int statusCode = 200) =>
        new(statusCode, body, "application/json");

    public static Response Empty(int statusCode = 200) =>
        new(statusCode, string.Empty, "application/json");

    public static Response Bytes(
        byte[] body,
        string contentType = "application/octet-stream",
        int statusCode = 200) =>
        new(statusCode, body, contentType);

    public static Response Archive(byte[] body)
    {
        string pathStat = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "{\"name\":\"archive\",\"size\":0,\"mode\":493,\"mtime\":\"2026-09-20T00:00:00Z\",\"linkTarget\":\"\"}"));
        return new Response(
            200,
            body,
            "application/x-tar",
            new Dictionary<string, string>
            {
                ["X-Docker-Container-Path-Stat"] = pathStat
            });
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }

            using (client)
            {
                await HandleClientAsync(client, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream stream = client.GetStream();
        byte[] received = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        int headerEnd = FindHeaderEnd(received);
        if (headerEnd < 0) return;

        string headerText = Encoding.ASCII.GetString(received, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return;

        var headers = lines
            .Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        int bodyOffset = headerEnd + 4;
        string body = bodyOffset < received.Length
            ? Encoding.UTF8.GetString(received, bodyOffset, received.Length - bodyOffset)
            : string.Empty;
        var request = new Request(requestLine[0], requestLine[1], body, headers);
        _requests.Enqueue(request);

        Response response = _responses.TryDequeue(out Response? scripted)
            ? scripted
            : Json("{\"message\":\"unexpected request\"}", 500);
        await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd = -1;
        int contentLength = 0;
        bool chunked = false;

        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            byte[] bytes = buffer.GetBuffer();

            if (headerEnd < 0)
            {
                headerEnd = FindHeaderEnd(bytes.AsSpan(0, (int)buffer.Length));
                if (headerEnd >= 0)
                {
                    string headers = Encoding.ASCII.GetString(bytes, 0, headerEnd);
                    Match match = Regex.Match(headers, @"(?im)^Content-Length:\s*(\d+)\s*$");
                    if (match.Success) contentLength = int.Parse(match.Groups[1].Value);
                    chunked = Regex.IsMatch(
                        headers,
                        @"(?im)^Transfer-Encoding:\s*.*\bchunked\b.*$");
                }
            }

            if (headerEnd >= 0)
            {
                if (!chunked && buffer.Length >= headerEnd + 4 + contentLength)
                    break;
                if (chunked && HasChunkedTerminator(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), headerEnd + 4))
                    break;
            }
        }

        return buffer.ToArray();
    }

    private static bool HasChunkedTerminator(ReadOnlySpan<byte> bytes, int bodyOffset)
    {
        ReadOnlySpan<byte> terminator = "0\r\n\r\n"u8;
        return bytes.Length >= bodyOffset + terminator.Length && bytes.EndsWith(terminator);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index <= bytes.Length - 4; index++)
        {
            if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
                bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        Response response,
        CancellationToken cancellationToken)
    {
        byte[] body = response.Body;
        string reason = response.StatusCode is >= 200 and < 300 ? "OK" : "Error";
        string customHeaders = response.Headers == null
            ? string.Empty
            : string.Concat(response.Headers.Select(header => $"{header.Key}: {header.Value}\r\n"));
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {response.StatusCode} {reason}\r\n" +
            $"Content-Type: {response.ContentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            customHeaders +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record Response(
        int StatusCode,
        byte[] Body,
        string ContentType,
        IReadOnlyDictionary<string, string>? Headers = null)
    {
        public Response(int statusCode, string body, string contentType)
            : this(statusCode, Encoding.UTF8.GetBytes(body), contentType, null)
        {
        }
    }

    internal sealed record Request(
        string Method,
        string RawTarget,
        string Body,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string Target => Regex.Replace(RawTarget, @"^/v\d+(?:\.\d+)?", string.Empty);
    }
}
