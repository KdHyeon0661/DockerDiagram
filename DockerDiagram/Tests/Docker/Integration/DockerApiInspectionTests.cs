using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerApiInspectionTests
{
    [Fact]
    public async Task ResourceInspect_UsesIdentityAndMapsDockerResponses()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "Id":"container-1",
                  "Name":"/web",
                  "Config":{"Image":"nginx:1.27"}
                }
                """),
            ScriptedDockerApiServer.Json("""
                {
                  "Name":"data",
                  "Driver":"local",
                  "Mountpoint":"/var/lib/docker/volumes/data/_data",
                  "Labels":{"owner":"diagram"}
                }
                """),
            ScriptedDockerApiServer.Json("""
                {
                  "Id":"network-1",
                  "Name":"backend",
                  "Driver":"bridge",
                  "Scope":"local",
                  "IPAM":{"Driver":"default","Config":[]}
                }
                """));
        using var service = CreateService(server);

        var container = await service.InspectContainerAsync("container-1");
        var volume = await service.InspectVolumeAsync("data");
        var network = await service.InspectNetworkAsync("network-1");

        Assert.Equal("container-1", container.ID);
        Assert.Equal("/web", container.Name);
        Assert.Equal("data", volume.Name);
        Assert.Equal("local", volume.Driver);
        Assert.Equal("network-1", network.ID);
        Assert.Equal("backend", network.Name);
        Assert.Equal(
            new[]
            {
                ("GET", "/containers/container-1/json"),
                ("GET", "/volumes/data"),
                ("GET", "/networks/network-1")
            },
            server.Requests.Select(RequestIdentity));
    }

    [Fact]
    public async Task VolumeUsage_FiltersNamedAndSourceFallbackMounts()
    {
        const string response = """
            [
              {
                "Id":"container-1",
                "Names":["/web"],
                "Mounts":[
                  {
                    "Type":"volume",
                    "Name":"data",
                    "Source":"/var/lib/docker/volumes/data/_data",
                    "Destination":"/app/data",
                    "Mode":"rw",
                    "RW":true
                  },
                  {
                    "Type":"bind",
                    "Source":"/host/data",
                    "Destination":"/bind",
                    "RW":true
                  }
                ]
              },
              {
                "Id":"container-2",
                "Names":["/worker"],
                "Mounts":[
                  {
                    "Type":"volume",
                    "Source":"/var/lib/docker/volumes/data",
                    "Destination":"/worker/data",
                    "Mode":"ro",
                    "RW":false
                  }
                ]
              }
            ]
            """;
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(response),
            ScriptedDockerApiServer.Json(response));
        using var service = CreateService(server);

        List<VolumeUsageInfo> usage = await service.GetVolumeUsageDetailsAsync("data");
        List<string> names = await service.GetContainersUsingVolumeAsync("data");

        Assert.Collection(
            usage.OrderBy(item => item.ContainerName),
            item =>
            {
                Assert.Equal("web", item.ContainerName);
                Assert.Equal("/app/data", item.Destination);
                Assert.True(item.ReadWrite);
            },
            item =>
            {
                Assert.Equal("worker", item.ContainerName);
                Assert.Equal("/worker/data", item.Destination);
                Assert.False(item.ReadWrite);
            });
        Assert.Equal(new[] { "web", "worker" }, names.OrderBy(name => name));
        Assert.All(server.Requests, request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.StartsWith("/containers/json", request.Target, StringComparison.Ordinal);
            Assert.Contains("all=1", request.Target, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task SystemInfo_IsCachedWithinServiceLifetime()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "ID":"engine-1",
                  "Name":"docker-host",
                  "NCPU":8,
                  "MemTotal":17179869184
                }
                """));
        using var service = CreateService(server);

        var first = await service.GetSystemInfoAsync();
        var second = await service.GetSystemInfoAsync();

        Assert.Same(first, second);
        Assert.Equal("engine-1", first.ID);
        Assert.Equal(8, first.NCPU);
        Assert.Single(server.Requests);
        Assert.Equal(("GET", "/info"), RequestIdentity(server.Requests[0]));
    }

    [Fact]
    public async Task ImageInspectAndSearch_MapResponsesAndQuery()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "Id":"sha256:image-1",
                  "RepoTags":["nginx:1.27"],
                  "Config":{"Env":["MODE=prod"],"Cmd":["nginx","-g","daemon off;"]}
                }
                """),
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "name":"nginx",
                    "description":"Official web server",
                    "star_count":1000,
                    "is_official":true,
                    "is_automated":false
                  }
                ]
                """));
        using var service = CreateService(server);

        var image = await service.InspectImageAsync("nginx:1.27");
        var result = Assert.Single(await service.SearchImagesAsync("nginx", 7));

        Assert.Equal("sha256:image-1", image.ID);
        Assert.Equal("nginx", result.Name);
        Assert.True(result.IsOfficial);
        Assert.StartsWith("/images/nginx:1.27/json", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.StartsWith("/images/search", server.Requests[1].Target, StringComparison.Ordinal);
        Assert.Contains("term=nginx", server.Requests[1].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit=7", server.Requests[1].Target, StringComparison.OrdinalIgnoreCase);
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
