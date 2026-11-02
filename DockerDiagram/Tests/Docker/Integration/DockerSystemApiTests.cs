using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerSystemApiTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(500, false)]
    public async Task PingReturnsReachabilityInsteadOfLeakingDockerException(int statusCode, bool expected)
    {
        await using var server = new ScriptedDockerApiServer(
            new ScriptedDockerApiServer.Response(statusCode, statusCode == 200 ? "OK" : "failed", "text/plain"));
        using var service = CreateService(server);

        bool reachable = await service.PingAsync();

        Assert.Equal(expected, reachable);
        Assert.Equal(("GET", "/_ping"), RequestIdentity(server.Requests[0]));
    }

    [Fact]
    public async Task SystemPruneUsesDockerResourceEndpointsAndAggregatesResults()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "ContainersDeleted": ["old-container"],
                  "SpaceReclaimed": 1024
                }
                """),
            ScriptedDockerApiServer.Json("""
                {
                  "NetworksDeleted": ["old-network"],
                  "SpaceReclaimed": 0
                }
                """),
            ScriptedDockerApiServer.Json("""
                {
                  "ImagesDeleted": [{"Untagged":"example/api:old"}],
                  "SpaceReclaimed": 2048
                }
                """),
            ScriptedDockerApiServer.Json("""
                {
                  "CachesDeleted": ["build-cache-1"],
                  "SpaceReclaimed": 512
                }
                """),
            ScriptedDockerApiServer.Json("""
                {
                  "VolumesDeleted": ["old-volume"],
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

        Assert.Collection(
            server.Requests,
            request => Assert.Equal(("POST", "/containers/prune"), RequestIdentity(request)),
            request => Assert.Equal(("POST", "/networks/prune"), RequestIdentity(request)),
            request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.StartsWith("/images/prune?", request.Target, StringComparison.Ordinal);
                Assert.Contains("dangling", Uri.UnescapeDataString(request.Target), StringComparison.Ordinal);
                Assert.Contains("false", Uri.UnescapeDataString(request.Target), StringComparison.Ordinal);
            },
            request => Assert.Equal(("POST", "/build/prune?all=1"), RequestIdentity(request)),
            request => Assert.Equal(("POST", "/volumes/prune"), RequestIdentity(request)));
        Assert.Equal(7680, result.SpaceReclaimed);
        Assert.Equal("old-container", Assert.Single(result.ContainersDeleted));
        Assert.Equal("old-network", Assert.Single(result.NetworksDeleted));
        Assert.Equal("example/api:old", Assert.Single(result.ImagesDeleted).Untagged);
        Assert.Equal("build-cache-1", Assert.Single(result.BuildCacheDeleted));
        Assert.Equal("old-volume", Assert.Single(result.VolumesDeleted));
    }

    [Fact]
    public async Task SystemPruneLeavesVolumesAloneUnlessRequested()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("{\"ContainersDeleted\":null,\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"NetworksDeleted\":null,\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"ImagesDeleted\":null,\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"CachesDeleted\":null,\"SpaceReclaimed\":0}"));
        using var service = CreateService(server);

        DockerPruneResult result = await service.PruneAsync(new DockerPruneOptions
        {
            Target = DockerPruneTarget.System,
            AllImages = false,
            IncludeVolumes = false
        });

        Assert.Collection(
            server.Requests,
            request => Assert.Equal(("POST", "/containers/prune"), RequestIdentity(request)),
            request => Assert.Equal(("POST", "/networks/prune"), RequestIdentity(request)),
            request => Assert.Equal(("POST", "/images/prune"), RequestIdentity(request)),
            request => Assert.Equal(("POST", "/build/prune"), RequestIdentity(request)));
        Assert.Empty(result.VolumesDeleted);
        Assert.Contains("Containers: 0", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllResourcesPruneStopsRunningContainersAndDeletesNamedVolumes()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [{
                  "Id": "container-1",
                  "Names": ["/web"],
                  "Image": "nginx:latest",
                  "State": "running",
                  "Labels": {}
                }]
                """),
            ScriptedDockerApiServer.Empty(204),
            ScriptedDockerApiServer.Json("{\"ContainersDeleted\":[\"container-1\"],\"SpaceReclaimed\":1024}"),
            ScriptedDockerApiServer.Json("{\"NetworksDeleted\":[\"app-network\"],\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"ImagesDeleted\":[{\"Deleted\":\"sha256:image-1\"}],\"SpaceReclaimed\":2048}"),
            ScriptedDockerApiServer.Json("{\"CachesDeleted\":[\"cache-1\"],\"SpaceReclaimed\":512}"),
            ScriptedDockerApiServer.Json("{\"VolumesDeleted\":[\"anonymous-volume\"],\"SpaceReclaimed\":4096}"),
            ScriptedDockerApiServer.Json("""
                {
                  "Volumes": [{"Name":"named-data","Driver":"local","Labels":{}}],
                  "Warnings": []
                }
                """),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        DockerPruneResult result = await service.PruneAsync(new DockerPruneOptions
        {
            Target = DockerPruneTarget.AllResources
        });

        Assert.Collection(
            server.Requests,
            request =>
            {
                Assert.Equal("GET", request.Method);
                Assert.StartsWith("/containers/json", request.Target, StringComparison.Ordinal);
                Assert.Contains("all=1", request.Target, StringComparison.OrdinalIgnoreCase);
            },
            request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.StartsWith("/containers/container-1/stop", request.Target, StringComparison.Ordinal);
            },
            request => Assert.Equal(("POST", "/containers/prune"), RequestIdentity(request)),
            request => Assert.Equal(("POST", "/networks/prune"), RequestIdentity(request)),
            request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.StartsWith("/images/prune?", request.Target, StringComparison.Ordinal);
                Assert.Contains("false", Uri.UnescapeDataString(request.Target), StringComparison.Ordinal);
            },
            request => Assert.Equal(("POST", "/build/prune?all=1"), RequestIdentity(request)),
            request => Assert.Equal(("POST", "/volumes/prune"), RequestIdentity(request)),
            request => Assert.Equal(("GET", "/volumes"), RequestIdentity(request)),
            request =>
            {
                Assert.Equal("DELETE", request.Method);
                Assert.StartsWith("/volumes/named-data", request.Target, StringComparison.Ordinal);
                Assert.Contains("force=false", request.Target, StringComparison.OrdinalIgnoreCase);
            });
        Assert.Contains("container-1", result.ContainersDeleted);
        Assert.Contains("anonymous-volume", result.VolumesDeleted);
        Assert.Contains("named-data", result.VolumesDeleted);
    }

    [Fact]
    public async Task AllResourcesPruneBlocksOrchestratorManagedContainersBeforeDeletingAnything()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [{
                  "Id": "task-container",
                  "Names": ["/service.1.task"],
                  "Image": "example/api:latest",
                  "State": "running",
                  "Labels": {"com.docker.swarm.task.id":"task-1"}
                }]
                """));
        using var service = CreateService(server);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PruneAsync(new DockerPruneOptions { Target = DockerPruneTarget.AllResources }));

        Assert.Contains("Swarm", error.Message, StringComparison.Ordinal);
        Assert.Single(server.Requests);
        Assert.Equal("GET", server.Requests[0].Method);
    }

    [Fact]
    public async Task AllResourcesPruneForceRemovesContainerWhenGracefulStopFails()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [{
                  "Id": "stuck-container",
                  "Names": ["/stuck"],
                  "Image": "example/stuck:latest",
                  "State": "running",
                  "Labels": {}
                }]
                """),
            ScriptedDockerApiServer.Json("{\"message\":\"stop failed\"}", 500),
            ScriptedDockerApiServer.Empty(204),
            ScriptedDockerApiServer.Json("{\"ContainersDeleted\":[],\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"NetworksDeleted\":[],\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"ImagesDeleted\":[],\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"CachesDeleted\":[],\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"VolumesDeleted\":[],\"SpaceReclaimed\":0}"),
            ScriptedDockerApiServer.Json("{\"Volumes\":[],\"Warnings\":[]}"));
        using var service = CreateService(server);

        DockerPruneResult result = await service.PruneAsync(new DockerPruneOptions
        {
            Target = DockerPruneTarget.AllResources
        });

        Assert.Equal("DELETE", server.Requests[2].Method);
        Assert.StartsWith("/containers/stuck-container", server.Requests[2].Target, StringComparison.Ordinal);
        Assert.Contains("force=1", server.Requests[2].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("stuck-container", Assert.Single(result.ContainersDeleted));
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
