using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerSystemApiTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(500, false)]
    public async Task Ping_ReturnsReachabilityInsteadOfLeakingDockerException(int statusCode, bool expected)
    {
        await using var server = new ScriptedDockerApiServer(
            new ScriptedDockerApiServer.Response(statusCode, statusCode == 200 ? "OK" : "failed", "text/plain"));
        using var service = CreateService(server);

        bool reachable = await service.PingAsync();

        Assert.Equal(expected, reachable);
        Assert.Equal(("GET", "/_ping"), RequestIdentity(server.Requests[0]));
    }

    [Fact]
    public async Task SystemPrune_UsesRequestedFlagsAndMapsDockerResult()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "ContainersDeleted": ["old-container"],
                  "ImagesDeleted": [{"Untagged":"example/api:old"}],
                  "VolumesDeleted": ["old-volume"],
                  "NetworksDeleted": [],
                  "SpaceReclaimed": 4096
                }
                """));
        using var service = CreateService(server);

        DockerPruneResult result = await service.PruneAsync(new DockerPruneOptions
        {
            Target = DockerPruneTarget.System,
            AllImages = true,
            IncludeVolumes = true
        });

        Assert.Equal("POST", server.Requests[0].Method);
        Assert.StartsWith("/system/prune?", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("all=1", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("volumes=1", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Equal(4096, result.SpaceReclaimed);
        Assert.Equal("old-container", Assert.Single(result.ContainersDeleted));
        Assert.Equal("old-volume", Assert.Single(result.VolumesDeleted));
    }

    private static DockerApiService CreateService(ScriptedDockerApiServer server) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = RuntimeKind.DockerEngine,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });

    private static (string Method, string Target) RequestIdentity(ScriptedDockerApiServer.Request request) =>
        (request.Method, request.Target);
}
