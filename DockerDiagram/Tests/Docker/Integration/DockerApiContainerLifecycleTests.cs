using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiContainerLifecycleTests
{
    [Theory]
    [InlineData("start", "POST", "/containers/container-1/start")]
    [InlineData("stop", "POST", "/containers/container-1/stop")]
    [InlineData("pause", "POST", "/containers/container-1/pause")]
    [InlineData("unpause", "POST", "/containers/container-1/unpause")]
    [InlineData("restart", "POST", "/containers/container-1/restart")]
    [InlineData("kill", "POST", "/containers/container-1/kill")]
    [InlineData("rename", "POST", "/containers/container-1/rename")]
    [InlineData("remove", "DELETE", "/containers/container-1")]
    public async Task LifecycleOperation_UsesExpectedDockerEndpoint(
        string operation,
        string expectedMethod,
        string expectedTarget)
    {
        await using var server = new ScriptedDockerApiServer(ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        await InvokeAsync(service, operation);

        ScriptedDockerApiServer.Request request = Assert.Single(server.Requests);
        Assert.Equal(expectedMethod, request.Method);
        Assert.StartsWith(expectedTarget, request.Target, StringComparison.Ordinal);
        if (operation == "stop") Assert.Contains("t=5", request.Target, StringComparison.OrdinalIgnoreCase);
        if (operation == "kill") Assert.Contains("signal=SIGTERM", request.Target, StringComparison.OrdinalIgnoreCase);
        if (operation == "rename") Assert.Contains("name=renamed", request.Target, StringComparison.OrdinalIgnoreCase);
        if (operation == "remove")
        {
            Assert.Contains("force=1", request.Target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("v=true", request.Target, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CreateAndStart_MapsRuntimeOptionsAndReturnsContainerId()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("{\"Id\":\"created-1\",\"Warnings\":[]}", 201),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        string id = await service.CreateAndStartContainerAsync(
            "web api",
            "registry.example:5000/team/api:2",
            "ignored",
            new List<string> { "8080:80", "53/udp" },
            new List<string> { "MODE=production" },
            new List<string> { "data:/data" },
            "unless-stopped",
            memoryMb: 128,
            cpuCount: 1.5,
            command: "dotnet Api.dll --urls \"http://+:80\"",
            tty: true,
            networkName: "backend",
            labels: new Dictionary<string, string> { ["owner"] = "diagram" });

        Assert.Equal("created-1", id);
        Assert.StartsWith("/containers/create", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("name=web-api", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        JObject body = JObject.Parse(server.Requests[0].Body);
        Assert.Equal("registry.example:5000/team/api:2", body.Value<string>("Image"));
        Assert.Equal(new[] { "dotnet", "Api.dll", "--urls", "http://+:80" }, body["Cmd"]!.Values<string>());
        Assert.True(body.Value<bool>("Tty"));
        Assert.True(body.Value<bool>("OpenStdin"));
        Assert.Equal("MODE=production", body["Env"]!.Values<string>().Single());
        Assert.Equal("diagram", body["Labels"]!.Value<string>("owner"));

        var hostConfig = (JObject)body["HostConfig"]!;
        Assert.Equal(128L * 1024 * 1024, hostConfig.Value<long>("Memory"));
        Assert.Equal(
            1_500_000_000L,
            hostConfig.GetValue("NanoCpus", StringComparison.OrdinalIgnoreCase)!.Value<long>());
        Assert.Equal("backend", hostConfig.Value<string>("NetworkMode"));
        Assert.Equal(("POST", "/containers/created-1/start"), RequestIdentity(server.Requests[1]));
    }

    [Fact]
    public async Task StartFailure_RemovesPartiallyCreatedContainer()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("{\"Id\":\"created-1\",\"Warnings\":[]}", 201),
            ScriptedDockerApiServer.Json("{\"message\":\"start failed\"}", 500),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        await Assert.ThrowsAsync<DockerApiException>(() => service.CreateAndStartContainerAsync(
            "web",
            "nginx",
            "latest",
            [],
            [],
            [],
            "no",
            0,
            0));

        Assert.Equal(3, server.Requests.Count);
        Assert.Equal("DELETE", server.Requests[2].Method);
        Assert.StartsWith("/containers/created-1", server.Requests[2].Target, StringComparison.Ordinal);
        Assert.Contains("force=1", server.Requests[2].Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecreateFromInspect_PreservesContainerAndHostConfiguration()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("{\"Id\":\"recreated-1\",\"Warnings\":[]}", 201));
        using var service = CreateService(server);
        var inspect = new ContainerInspectResponse
        {
            Name = "/web",
            Config = new Config
            {
                Image = "nginx:latest",
                User = "1000:1000",
                WorkingDir = "/app",
                Env = new List<string> { "MODE=production" },
                Entrypoint = new List<string> { "/entrypoint.sh" },
                Cmd = new List<string> { "nginx", "-g", "daemon off;" },
                Labels = new Dictionary<string, string> { ["owner"] = "diagram" }
            },
            HostConfig = new HostConfig
            {
                Binds = new List<string> { "old:/old" },
                Memory = 256L * 1024 * 1024,
                NanoCPUs = 750_000_000,
                Privileged = true,
                CapAdd = new List<string> { "NET_ADMIN" },
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
            },
            NetworkSettings = new NetworkSettings
            {
                Networks = new Dictionary<string, EndpointSettings>
                {
                    ["backend"] = new()
                    {
                        Aliases = new List<string> { "web" },
                        DriverOpts = new Dictionary<string, string> { ["com.example.option"] = "on" }
                    }
                }
            }
        };

        string id = await service.RecreateContainerFromInspectAsync(
            "web",
            inspect,
            new List<string> { "important-data:/data:ro" },
            startContainer: false);

        Assert.Equal("recreated-1", id);
        ScriptedDockerApiServer.Request request = Assert.Single(server.Requests);
        JObject body = JObject.Parse(request.Body);
        Assert.Equal("1000:1000", body.Value<string>("User"));
        Assert.Equal("/app", body.Value<string>("WorkingDir"));
        Assert.Equal("diagram", body["Labels"]!.Value<string>("owner"));
        Assert.Equal("/entrypoint.sh", body["Entrypoint"]!.Values<string>().Single());
        var hostConfig = (JObject)body["HostConfig"]!;
        Assert.Equal(256L * 1024 * 1024, hostConfig.Value<long>("Memory"));
        Assert.True(hostConfig.Value<bool>("Privileged"));
        Assert.Equal("NET_ADMIN", hostConfig["CapAdd"]!.Values<string>().Single());
        Assert.Equal("important-data:/data:ro", hostConfig["Binds"]!.Values<string>().Single());
        Assert.Equal(
            "web",
            body["NetworkingConfig"]!["EndpointsConfig"]!["backend"]!["Aliases"]!.Values<string>().Single());
    }

    private static Task InvokeAsync(DockerApiService service, string operation) => operation switch
    {
        "start" => service.StartContainerAsync("container-1"),
        "stop" => service.StopContainerAsync("container-1"),
        "pause" => service.PauseContainerAsync("container-1"),
        "unpause" => service.UnpauseContainerAsync("container-1"),
        "restart" => service.RestartContainerAsync("container-1"),
        "kill" => service.KillContainerAsync("container-1", "SIGTERM"),
        "rename" => service.RenameContainerAsync("container-1", "renamed"),
        "remove" => service.RemoveContainerAsync("container-1"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

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
