using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiExecAndStatsTests
{
    [Fact]
    public async Task ExecuteCommand_UsesPlatformShellBeforeHijackedStreamBoundary()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""{"Id":"container-1","Platform":"linux"}"""),
            ScriptedDockerApiServer.Json("""{"Id":"exec-1"}""", 201),
            ScriptedDockerApiServer.Json("""{"message":"hijacked stream requires a Docker transport"}""", 500));
        using var service = CreateService(server);

        await Assert.ThrowsAsync<Docker.DotNet.DockerApiException>(() =>
            service.ExecuteCommandWithOutputAsync("container-1", "echo hello"));

        Assert.Equal(("GET", "/containers/container-1/json"), RequestIdentity(server.Requests[0]));
        Assert.Equal(("POST", "/containers/container-1/exec"), RequestIdentity(server.Requests[1]));
        JObject create = JObject.Parse(server.Requests[1].Body);
        Assert.Equal(new[] { "/bin/sh", "-c", "echo hello" }, create["Cmd"]!.Values<string>());
        Assert.Equal(("POST", "/exec/exec-1/start"), RequestIdentity(server.Requests[2]));
    }

    [Fact]
    public async Task OpenTerminal_ReturnsFalseWhenNoCandidateShellExistsWithoutLaunchingHostProcess()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""{"Id":"container-1","Platform":"linux"}"""),
            ScriptedDockerApiServer.Json("""{"message":"bash not found"}""", 404),
            ScriptedDockerApiServer.Json("""{"message":"sh not found"}""", 404),
            ScriptedDockerApiServer.Json("""{"message":"ash not found"}""", 404));
        using var service = CreateService(server);

        bool opened = await service.OpenTerminalAsync("container-1");

        Assert.False(opened);
        Assert.Equal(4, server.Requests.Count);
        Assert.All(server.Requests.Skip(1), request =>
            Assert.Equal(("POST", "/containers/container-1/exec"), RequestIdentity(request)));
    }

    [Fact]
    public async Task ContainerStats_MapsCpuDeltaAndMemoryBytes()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "cpu_stats":{
                    "cpu_usage":{"total_usage":300},
                    "system_cpu_usage":2000,
                    "online_cpus":4
                  },
                  "precpu_stats":{
                    "cpu_usage":{"total_usage":100},
                    "system_cpu_usage":1000,
                    "online_cpus":4
                  },
                  "memory_stats":{"usage":134217728,"limit":536870912}
                }
                """));
        using var service = CreateService(server);

        ContainerStats stats = await service.GetContainerStatsAsync("container-1");

        Assert.Equal(80.0, stats.CpuPercentage, precision: 5);
        Assert.Equal(128.0, stats.MemoryUsedMB, precision: 5);
        Assert.Equal(512.0, stats.MemoryLimitMB, precision: 5);
        Assert.StartsWith("/containers/container-1/stats", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("stream=0", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
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
