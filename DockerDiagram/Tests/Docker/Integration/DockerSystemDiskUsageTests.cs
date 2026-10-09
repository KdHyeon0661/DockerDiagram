using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerSystemDiskUsageTests
{
    [Fact]
    public async Task DiskUsage_AggregatesCoreResourcesAndCachesResult()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "Id":"sha256:image",
                    "ParentId":"",
                    "RepoTags":["example/api:2"],
                    "RepoDigests":[],
                    "Created":1710000000,
                    "Size":2048,
                    "SharedSize":512,
                    "VirtualSize":2560,
                    "Containers":1
                  }
                ]
                """),
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "Id":"container-1",
                    "Names":["/web"],
                    "Image":"example/api:2",
                    "State":"running",
                    "SizeRw":1024,
                    "SizeRootFs":4096
                  }
                ]
                """),
            ScriptedDockerApiServer.Json("""
                {
                  "Volumes":[
                    {
                      "Name":"data",
                      "Driver":"local",
                      "Mountpoint":"/var/lib/docker/volumes/data",
                      "UsageData":{"Size":8192,"RefCount":1}
                    }
                  ],
                  "Warnings":[]
                }
                """));
        using var service = CreateService(server);

        SystemDiskUsage first = await service.GetSystemDiskUsageAsync();
        SystemDiskUsage second = await service.GetSystemDiskUsageAsync();

        Assert.Same(first, second);
        Assert.Equal(2048, first.ImagesSize);
        Assert.Equal(1024, first.ContainersSize);
        Assert.Equal(8192, first.VolumesSize);
        Assert.Equal("web", Assert.Single(first.Containers).Name);
        Assert.Equal("example/api:2", Assert.Single(first.Images).Repository);
        Assert.Equal("data", Assert.Single(first.Volumes).Name);
        Assert.Equal(3, server.Requests.Count);
        Assert.Contains(server.Requests, request => request.Target.StartsWith("/images/json", StringComparison.Ordinal));
        Assert.Contains(server.Requests, request => request.Target.StartsWith("/containers/json", StringComparison.Ordinal));
        Assert.Contains(server.Requests, request => request.Target == "/volumes");
    }

    private static DockerApiService CreateService(ScriptedDockerApiServer server) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = RuntimeKind.DockerEngine,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });
}
