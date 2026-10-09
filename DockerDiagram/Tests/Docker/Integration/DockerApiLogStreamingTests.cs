using System.Buffers.Binary;
using System.Text;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerApiLogStreamingTests
{
    [Fact]
    public async Task ContainerLogs_ReadStdoutAndStderrMultiplexFrames()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Bytes(
                Multiplex((1, "2026-09-20T03:04:05Z ready\n"), (2, "2026-09-20T03:04:06Z warning\n")),
                "application/vnd.docker.raw-stream"));
        using var service = CreateDockerService(server);

        string logs = await service.GetContainerLogsAsync("container-1", 25);

        Assert.Contains("ready", logs, StringComparison.Ordinal);
        Assert.Contains("warning", logs, StringComparison.Ordinal);
        Assert.StartsWith("/containers/container-1/logs", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("stdout=1", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stderr=1", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tail=25", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerLogStream_ForwardsDataAndStopsAtEngineEof()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Bytes(
                Multiplex((1, "2026-09-20T03:04:05Z streamed\n")),
                "application/vnd.docker.raw-stream"));
        using var service = CreateDockerService(server);
        var chunks = new List<string>();

        await service.StreamContainerLogsAsync("container-1", chunks.Add, CancellationToken.None, 10);

        Assert.Single(chunks);
        Assert.Contains("streamed", chunks[0], StringComparison.Ordinal);
        Assert.Contains("follow=1", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tail=10", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwarmServiceLogs_SeparatesStderrSection()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Bytes(
                Multiplex((1, "service ready\n"), (2, "service warning\n")),
                "application/vnd.docker.raw-stream"));
        using var service = CreateSwarmService(server);

        string logs = await service.GetSwarmServiceLogsAsync("service-1", 20);

        Assert.Contains("service ready", logs, StringComparison.Ordinal);
        Assert.Contains("[stderr]", logs, StringComparison.Ordinal);
        Assert.Contains("service warning", logs, StringComparison.Ordinal);
        Assert.StartsWith("/services/service-1/logs", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("tail=20", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Multiplex(params (byte Stream, string Text)[] frames)
    {
        using var output = new MemoryStream();
        var header = new byte[8];
        foreach ((byte stream, string text) in frames)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            Array.Clear(header);
            header[0] = stream;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), payload.Length);
            output.Write(header);
            output.Write(payload);
        }
        return output.ToArray();
    }

    private static DockerApiService CreateDockerService(ScriptedDockerApiServer server) =>
        CreateService(server, RuntimeKind.DockerEngine);

    private static DockerApiService CreateSwarmService(ScriptedDockerApiServer server) =>
        CreateService(server, RuntimeKind.DockerSwarm);

    private static DockerApiService CreateService(ScriptedDockerApiServer server, RuntimeKind runtimeKind) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = runtimeKind,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });
}
