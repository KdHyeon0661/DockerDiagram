using Docker.DotNet.Models;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using System.Collections.Concurrent;

namespace DockerDiagram.Tests;

public sealed class DockerEventMonitoringTests
{
    [Fact]
    public async Task MonitorEvents_ForwardsDockerEventAndRequestsCoreResourceFilters()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {"Type":"container","Action":"start","Actor":{"ID":"container-1","Attributes":{"name":"web"}},"time":1710000000,"timeNano":1710000000000000000}
                """));
        using var service = CreateService(server);
        var progress = new RecordingProgress<Message>();

        await service.MonitorDockerEventsAsync(progress, CancellationToken.None);

        Message message = Assert.Single(progress.Values);
        Assert.Equal("container", message.Type);
        Assert.Equal("start", message.Action);
        Assert.Equal("container-1", message.Actor.ID);

        ScriptedDockerApiServer.Request request = Assert.Single(server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.StartsWith("/events?", request.Target, StringComparison.Ordinal);
        string decoded = Uri.UnescapeDataString(request.Target);
        Assert.Contains("container", decoded, StringComparison.Ordinal);
        Assert.Contains("volume", decoded, StringComparison.Ordinal);
        Assert.Contains("network", decoded, StringComparison.Ordinal);
        Assert.Contains("image", decoded, StringComparison.Ordinal);
    }

    private static DockerApiService CreateService(ScriptedDockerApiServer server) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = RuntimeKind.DockerEngine,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _values = new();
        public IReadOnlyList<T> Values => _values.ToArray();
        public void Report(T value) => _values.Enqueue(value);
    }
}
