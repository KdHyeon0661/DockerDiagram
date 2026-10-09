using System.Formats.Tar;
using System.Text;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerApiFileTransferTests
{
    [Fact]
    public async Task CopyFromContainer_ExtractsReturnedTarIntoRequestedDirectory()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Archive(CreateTar("result.txt", "copied from container")));
        using var service = CreateService(server);
        string directory = CreateTempDirectory();

        try
        {
            await service.CopyFromContainerAsync("container-1", "/app/result.txt", directory);

            Assert.Equal("copied from container", await File.ReadAllTextAsync(Path.Combine(directory, "result.txt")));
            Assert.Equal("GET", server.Requests[0].Method);
            Assert.StartsWith("/containers/container-1/archive", server.Requests[0].Target, StringComparison.Ordinal);
            Assert.Contains("path=%2Fapp%2Fresult.txt", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CopyToContainer_PackagesHostFileAndTargetsContainerPath()
    {
        await using var server = new ScriptedDockerApiServer(ScriptedDockerApiServer.Empty(200));
        using var service = CreateService(server);
        string directory = CreateTempDirectory();
        string source = Path.Combine(directory, "input.txt");
        await File.WriteAllTextAsync(source, "copied to container");

        try
        {
            await service.CopyToContainerAsync("container-1", source, "/tmp/uploads");

            ScriptedDockerApiServer.Request request = Assert.Single(server.Requests);
            Assert.Equal("PUT", request.Method);
            Assert.StartsWith("/containers/container-1/archive", request.Target, StringComparison.Ordinal);
            Assert.Contains("path=%2Ftmp%2Fuploads", request.Target, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                request.Headers.ContainsKey("Content-Length") ||
                request.Headers.TryGetValue("Transfer-Encoding", out string? encoding) &&
                encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImageImportLoadAndBuild_UseTarUploadEndpoints()
    {
        const string progress = "{\"status\":\"done\"}\n";
        await using var server = new ScriptedDockerApiServer(
            new ScriptedDockerApiServer.Response(200, progress, "application/json"),
            new ScriptedDockerApiServer.Response(200, progress, "application/json"),
            new ScriptedDockerApiServer.Response(200, progress, "application/json"));
        using var service = CreateService(server);
        string directory = CreateTempDirectory();
        string importTar = Path.Combine(directory, "rootfs.tar");
        string loadTar = Path.Combine(directory, "image.tar");
        string dockerfile = Path.Combine(directory, "Dockerfile");
        await File.WriteAllBytesAsync(importTar, "rootfs"u8.ToArray());
        await File.WriteAllBytesAsync(loadTar, "image"u8.ToArray());
        await File.WriteAllTextAsync(dockerfile, "FROM scratch\n");

        try
        {
            await service.ImportImageFromTarAsync(importTar, "example/rootfs", "1", "imported");
            await service.LoadImageFromTarAsync(loadTar);
            await service.BuildImageAsync("example/build:1", directory, dockerfile);

            Assert.Equal(3, server.Requests.Count);
            Assert.StartsWith("/images/create", server.Requests[0].Target, StringComparison.Ordinal);
            string importTarget = Uri.UnescapeDataString(server.Requests[0].Target);
            Assert.Contains("fromSrc=-", importTarget, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("repo=example/rootfs", importTarget, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tag=1", importTarget, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("/images/load", server.Requests[1].Target, StringComparison.Ordinal);
            Assert.StartsWith("/build", server.Requests[2].Target, StringComparison.Ordinal);
            string buildTarget = Uri.UnescapeDataString(server.Requests[2].Target);
            Assert.Contains("t=example/build:1", buildTarget, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dockerfile=Dockerfile", buildTarget, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreateTar(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
            };
            writer.WriteEntry(entry);
        }
        return stream.ToArray();
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"DockerDiagramTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static DockerApiService CreateService(ScriptedDockerApiServer server) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = RuntimeKind.DockerEngine,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });
}
