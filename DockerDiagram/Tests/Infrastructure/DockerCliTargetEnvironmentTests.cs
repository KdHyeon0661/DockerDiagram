using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DockerDiagram.Tests;

public sealed class DockerCliTargetEnvironmentTests
{
    [Fact]
    public void LocalProfile_ReplacesInheritedTarget()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["DOCKER_CONTEXT"] = "production";
        startInfo.Environment["DOCKER_HOST"] = "tcp://wrong-cluster:2375";

        DockerCliTargetEnvironment.Apply(
            startInfo,
            new ConnectionProfile { Type = EndpointType.Local });

        Assert.False(startInfo.Environment.ContainsKey("DOCKER_CONTEXT"));
        Assert.Equal(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "npipe://./pipe/docker_engine"
                : "unix:///var/run/docker.sock",
            startInfo.Environment["DOCKER_HOST"]);
    }

    [Fact]
    public void RemoteProfiles_ReplaceInheritedContext()
    {
        var tunnelStartInfo = new ProcessStartInfo();
        tunnelStartInfo.Environment["DOCKER_CONTEXT"] = "production";
        DockerCliTargetEnvironment.Apply(
            tunnelStartInfo,
            new ConnectionProfile
            {
                Type = EndpointType.SshRemote,
                LocalTunnelPort = 42376
            });

        Assert.False(tunnelStartInfo.Environment.ContainsKey("DOCKER_CONTEXT"));
        Assert.Equal("tcp://127.0.0.1:42376", tunnelStartInfo.Environment["DOCKER_HOST"]);

        var contextStartInfo = new ProcessStartInfo();
        contextStartInfo.Environment["DOCKER_CONTEXT"] = "stale";
        DockerCliTargetEnvironment.Apply(
            contextStartInfo,
            new ConnectionProfile
            {
                Type = EndpointType.DockerContext,
                DockerEndpoint = "tcp://manager.example:2376"
            });

        Assert.False(contextStartInfo.Environment.ContainsKey("DOCKER_CONTEXT"));
        Assert.Equal("tcp://manager.example:2376", contextStartInfo.Environment["DOCKER_HOST"]);
    }
}
