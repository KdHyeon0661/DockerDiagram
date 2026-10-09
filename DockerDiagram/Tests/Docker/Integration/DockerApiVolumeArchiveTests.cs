using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerApiVolumeArchiveTests
{
    [Fact]
    public async Task BackupVolume_UsesTemporaryContainerAndAlwaysRemovesIt()
    {
        const string progress = "{\"status\":\"done\"}\n";
        await using var server = new ScriptedDockerApiServer(
            new ScriptedDockerApiServer.Response(200, progress, "application/json"),
            ScriptedDockerApiServer.Json("""{"Id":"backup-container","Warnings":[]}""", 201),
            ScriptedDockerApiServer.Empty(204),
            ScriptedDockerApiServer.Archive("volume-archive"u8.ToArray()),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);
        string directory = CreateTempDirectory();
        string target = Path.Combine(directory, "backup.tar");

        try
        {
            await service.BackupVolumeAsync("data", target);

            Assert.Equal("volume-archive", await File.ReadAllTextAsync(target));
            Assert.Equal(5, server.Requests.Count);
            Assert.StartsWith("/images/create", server.Requests[0].Target, StringComparison.Ordinal);
            Assert.StartsWith("/containers/create", server.Requests[1].Target, StringComparison.Ordinal);
            Assert.Equal(("POST", "/containers/backup-container/start"), RequestIdentity(server.Requests[2]));
            Assert.StartsWith("/containers/backup-container/archive", server.Requests[3].Target, StringComparison.Ordinal);
            Assert.Equal("DELETE", server.Requests[4].Method);
            Assert.StartsWith("/containers/backup-container", server.Requests[4].Target, StringComparison.Ordinal);
            Assert.Contains("force=1", server.Requests[4].Target, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreVolume_UploadsArchiveAndAlwaysRemovesTemporaryContainer()
    {
        const string progress = "{\"status\":\"done\"}\n";
        await using var server = new ScriptedDockerApiServer(
            new ScriptedDockerApiServer.Response(200, progress, "application/json"),
            ScriptedDockerApiServer.Json("""{"Id":"restore-container","Warnings":[]}""", 201),
            ScriptedDockerApiServer.Empty(204),
            ScriptedDockerApiServer.Empty(200),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);
        string directory = CreateTempDirectory();
        string source = Path.Combine(directory, "backup.tar");
        await File.WriteAllBytesAsync(source, "volume-archive"u8.ToArray());

        try
        {
            await service.RestoreVolumeAsync("data", source);

            Assert.Equal(5, server.Requests.Count);
            Assert.StartsWith("/images/create", server.Requests[0].Target, StringComparison.Ordinal);
            Assert.StartsWith("/containers/create", server.Requests[1].Target, StringComparison.Ordinal);
            Assert.Equal(("POST", "/containers/restore-container/start"), RequestIdentity(server.Requests[2]));
            Assert.Equal("PUT", server.Requests[3].Method);
            Assert.StartsWith("/containers/restore-container/archive", server.Requests[3].Target, StringComparison.Ordinal);
            Assert.Contains("path=%2F", server.Requests[3].Target, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("DELETE", server.Requests[4].Method);
            Assert.Contains("force=1", server.Requests[4].Target, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    private static (string Method, string Target) RequestIdentity(ScriptedDockerApiServer.Request request) =>
        (request.Method, request.Target);
}
