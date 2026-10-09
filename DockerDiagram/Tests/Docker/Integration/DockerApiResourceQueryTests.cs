using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerApiResourceQueryTests
{
    [Fact]
    public async Task Containers_MapPortsStateAndComposeIdentity()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "Id": "container-1",
                    "Names": ["/web-1"],
                    "Image": "nginx:1.27",
                    "State": "running",
                    "Status": "Up 5 minutes",
                    "Ports": [
                      { "IP": "0.0.0.0", "PrivatePort": 80, "PublicPort": 8080, "Type": "tcp" }
                    ],
                    "Labels": {
                      "com.docker.compose.project": "demo",
                      "com.docker.compose.service": "web",
                      "com.docker.compose.container-number": "2",
                      "com.docker.compose.project.working_dir": "C:\\\\demo",
                      "com.docker.compose.project.config_files": "compose.yml"
                    }
                  }
                ]
                """));
        using var service = CreateService(server);

        DockerContainer container = Assert.Single(await service.GetContainersAsync());

        Assert.Equal("container-1", container.Id);
        Assert.Equal("web-1", container.Name);
        Assert.Equal("0.0.0.0:8080->80/tcp", container.Ports);
        Assert.Equal("#28a745", container.StateColor);
        Assert.Equal("demo", container.ComposeProjectName);
        Assert.Equal("web", container.ComposeServiceName);
        Assert.Equal(2, container.ComposeContainerNumber);
        Assert.Equal("Compose", container.ProjectSource);
        Assert.Equal("GET", server.Requests[0].Method);
        Assert.StartsWith("/containers/json", server.Requests[0].Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_SplitTaggedAndDanglingRows()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "Id": "sha256:tagged",
                    "RepoTags": ["registry.example:5000/team/api:2", "team/api:latest"],
                    "Size": 2048
                  },
                  {
                    "Id": "sha256:dangling",
                    "RepoTags": [],
                    "Size": 1024
                  }
                ]
                """));
        using var service = CreateService(server);

        List<DockerImage> images = await service.GetImagesAsync();

        Assert.Equal(3, images.Count);
        Assert.Contains(images, image =>
            image.Repository == "registry.example:5000/team/api" && image.Tag == "2");
        Assert.Contains(images, image =>
            image.Repository == "team/api" && image.Tag == "latest");
        Assert.Contains(images, image =>
            image.Id == "sha256:dangling" && image.Repository == "<none>" && image.Tag == "<none>");
        Assert.StartsWith("/images/json", server.Requests[0].Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Volumes_MapComposeAndTemplateLabels()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "Volumes": [
                    {
                      "Name": "demo_data",
                      "Driver": "local",
                      "Labels": {
                        "com.docker.compose.project": "demo",
                        "com.docker.compose.volume": "data"
                      }
                    },
                    {
                      "Name": "template-cache",
                      "Driver": "local",
                      "Labels": {
                        "com.dockerdiagram.project": "template-one",
                        "com.dockerdiagram.resource": "cache"
                      }
                    }
                  ],
                  "Warnings": []
                }
                """));
        using var service = CreateService(server);

        List<DockerVolume> volumes = await service.GetVolumesAsync();

        Assert.Contains(volumes, volume =>
            volume.Name == "demo_data" && volume.ComposeProjectName == "demo" &&
            volume.ComposeResourceName == "data" && volume.ProjectSource == "Compose");
        Assert.Contains(volumes, volume =>
            volume.Name == "template-cache" && volume.ComposeProjectName == "template-one" &&
            volume.ComposeResourceName == "cache" && volume.ProjectSource == "Template");
        Assert.Equal(("GET", "/volumes"), RequestIdentity(server.Requests[0]));
    }

    [Fact]
    public async Task Networks_MapDriverScopeAndComposeLabels()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "Id": "network-1",
                    "Name": "demo_backend",
                    "Driver": "bridge",
                    "Scope": "local",
                    "Labels": {
                      "com.docker.compose.project": "demo",
                      "com.docker.compose.network": "backend"
                    }
                  }
                ]
                """));
        using var service = CreateService(server);

        DockerNetworkGroup network = Assert.Single(await service.GetNetworksAsync());

        Assert.Equal("network-1", network.Id);
        Assert.Equal("bridge", network.Driver);
        Assert.Equal("local", network.Scope);
        Assert.Equal("demo", network.ComposeProjectName);
        Assert.Equal("backend", network.ComposeResourceName);
        Assert.Equal("Compose", network.ProjectSource);
        Assert.Equal(("GET", "/networks"), RequestIdentity(server.Requests[0]));
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
