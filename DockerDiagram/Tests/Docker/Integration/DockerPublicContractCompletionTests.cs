using Docker.DotNet.Models;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerPublicContractCompletionTests
{
    [Fact]
    public async Task ComposeUp_RejectsMissingFileBeforeStartingDockerCli()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-compose-{Guid.NewGuid():N}.yml");
        var compose = new DockerComposeCliService();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            compose.UpAsync(missing, new ConnectionProfile()));
    }

    [Fact]
    public async Task PullWithProgress_ForwardsProgressObjectAndDefaultsBlankTag()
    {
        await using var server = new ScriptedDockerApiServer(
            new ScriptedDockerApiServer.Response(
                200,
                "{\"status\":\"Pull complete\",\"id\":\"layer-1\",\"progressDetail\":{\"current\":10,\"total\":10}}\n",
                "application/json"));
        using var service = CreateService(server);
        var messages = new List<JSONMessage>();

        await service.PullImageWithProgressAsync(
            "example/api", " ", new SynchronousProgress<JSONMessage>(messages.Add));

        JSONMessage message = Assert.Single(messages);
        Assert.Equal("Pull complete", message.Status);
        Assert.Equal("layer-1", message.ID);
        Assert.StartsWith("/images/create", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("fromImage=example%2Fapi", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tag=latest", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FireAndForgetExecuteCommand_ContainsDockerFailureAtWrapperBoundary()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""{"Id":"container-1","Platform":"linux"}"""),
            ScriptedDockerApiServer.Json("""{"message":"exec create failed"}""", 500));
        using var service = CreateService(server);

        await service.ExecuteCommandAsync("container-1", "echo hello");

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("GET", server.Requests[0].Method);
        Assert.Equal("POST", server.Requests[1].Method);
        Assert.Equal("/containers/container-1/exec", server.Requests[1].Target);
    }

    private static DockerApiService CreateService(ScriptedDockerApiServer server) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = RuntimeKind.DockerEngine,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
