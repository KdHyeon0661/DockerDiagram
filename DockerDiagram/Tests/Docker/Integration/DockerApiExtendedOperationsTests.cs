using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiExtendedOperationsTests
{
    [Fact]
    public async Task CommitAndResourceUpdate_ForwardAllMutationOptions()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""{"Id":"sha256:committed"}""", 201),
            ScriptedDockerApiServer.Json("""{"Warnings":[]}"""));
        using var service = CreateService(server);

        string imageId = await service.CommitContainerAsync(
            "container-1", "example/api", "snapshot", "checkpoint", "DockerDiagram", pause: false);
        await service.UpdateContainerResourcesAsync("container-1", cpuCount: 1.5, memoryMb: 256);

        Assert.Equal("sha256:committed", imageId);
        ScriptedDockerApiServer.Request commit = server.Requests[0];
        Assert.Equal("POST", commit.Method);
        Assert.StartsWith("/commit?", commit.Target, StringComparison.Ordinal);
        string decodedCommit = Uri.UnescapeDataString(commit.Target);
        Assert.Contains("container=container-1", decodedCommit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repo=example/api", decodedCommit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tag=snapshot", decodedCommit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comment=checkpoint", decodedCommit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("author=DockerDiagram", decodedCommit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pause=1", decodedCommit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pause=true", decodedCommit, StringComparison.OrdinalIgnoreCase);

        ScriptedDockerApiServer.Request update = server.Requests[1];
        Assert.Equal(("POST", "/containers/container-1/update"), RequestIdentity(update));
        JObject body = JObject.Parse(update.Body);
        Assert.Equal(1_500_000_000L, body.GetValue("NanoCpus", StringComparison.OrdinalIgnoreCase)!.Value<long>());
        Assert.Equal(256L * 1024 * 1024, body.Value<long>("Memory"));
        Assert.Equal(256L * 1024 * 1024, body.Value<long>("MemorySwap"));
    }

    [Fact]
    public async Task ExportContainerAndSaveImage_WriteEngineStreamsToFiles()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Bytes("container-archive"u8.ToArray(), "application/x-tar"),
            ScriptedDockerApiServer.Bytes("image-archive"u8.ToArray(), "application/x-tar"));
        using var service = CreateService(server);
        string directory = Path.Combine(Path.GetTempPath(), $"DockerDiagramTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string containerTar = Path.Combine(directory, "container.tar");
        string imageTar = Path.Combine(directory, "image.tar");

        try
        {
            await service.ExportContainerAsync("container-1", containerTar);
            await service.SaveImageAsync("example/api:2", imageTar);

            Assert.Equal("container-archive", await File.ReadAllTextAsync(containerTar));
            Assert.Equal("image-archive", await File.ReadAllTextAsync(imageTar));
            Assert.Equal(("GET", "/containers/container-1/export"), RequestIdentity(server.Requests[0]));
            Assert.Equal("GET", server.Requests[1].Method);
            Assert.Equal("/images/get?names=example/api:2", Uri.UnescapeDataString(server.Requests[1].Target));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PullAndPushImage_ForwardTagAndRegistryAuthentication()
    {
        const string progress = "{\"status\":\"done\"}\n";
        await using var server = new ScriptedDockerApiServer(
            new ScriptedDockerApiServer.Response(200, progress, "application/json"),
            new ScriptedDockerApiServer.Response(200, progress, "application/json"));
        using var service = CreateService(server);

        await service.PullImageAsync("private/api", "2", "user", "password", "registry.example");
        await service.PushImageAsync("private/api", "2", "user", "password", "registry.example");

        ScriptedDockerApiServer.Request pull = server.Requests[0];
        Assert.Equal("POST", pull.Method);
        Assert.StartsWith("/images/create", pull.Target, StringComparison.Ordinal);
        Assert.Contains("fromImage=private%2Fapi", pull.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tag=2", pull.Target, StringComparison.OrdinalIgnoreCase);
        Assert.True(pull.Headers.ContainsKey("X-Registry-Auth"));
        Assert.DoesNotContain("password", pull.Headers["X-Registry-Auth"], StringComparison.Ordinal);

        ScriptedDockerApiServer.Request push = server.Requests[1];
        Assert.Equal("POST", push.Method);
        Assert.Contains("/images/private/api/push", Uri.UnescapeDataString(push.Target), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tag=2", push.Target, StringComparison.OrdinalIgnoreCase);
        Assert.True(push.Headers.ContainsKey("X-Registry-Auth"));
    }

    [Fact]
    public async Task EngineMetadata_MapsRuntimeConfigurationWithoutRegistryFallback()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "Id":"sha256:image-1",
                  "Config":{
                    "Env":["MODE=production"],
                    "ExposedPorts":{"8080/tcp":{}},
                    "Volumes":{"/data":{}},
                    "Entrypoint":["dotnet"],
                    "Cmd":["Api.dll"]
                  }
                }
                """));
        using var service = CreateService(server);

        ContainerImageMetadata metadata = Assert.IsType<ContainerImageMetadata>(
            await service.GetImageMetadataAsync("example/api:2"));

        Assert.Equal("Docker Engine", metadata.Source);
        Assert.Equal(new[] { "MODE=production" }, metadata.Environment);
        Assert.Equal(new[] { "8080/tcp" }, metadata.ExposedPorts);
        Assert.Equal(new[] { "/data" }, metadata.Volumes);
        Assert.Equal(new[] { "dotnet" }, metadata.Entrypoint);
        Assert.Equal(new[] { "Api.dll" }, metadata.Command);
    }

    [Fact]
    public async Task LocalFileOperations_ValidateInputsBeforeContactingDocker()
    {
        await using var server = new ScriptedDockerApiServer();
        using var service = CreateService(server);
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.CopyToContainerAsync("container-1", missing, "/tmp"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.BuildImageAsync("example/api:2", missing, Path.Combine(missing, "Dockerfile")));

        Assert.Empty(server.Requests);
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
