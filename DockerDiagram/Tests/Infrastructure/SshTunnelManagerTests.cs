using System.Diagnostics;
using DockerDiagram.Infrastructure;

namespace DockerDiagram.Tests;

public sealed class SshTunnelManagerTests
{
    [Fact]
    public void IsProcessAliveReturnsFalseForExitedProcess()
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "exit", "0" },
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();

        Assert.False(SshTunnelManager.IsProcessAlive(process));
    }

    [Fact]
    public void NormalizeRemoteDockerSocketPathUsesDefaultAndRejectsUnsafePaths()
    {
        Assert.Equal(
            SshTunnelManager.DefaultRemoteDockerSocketPath,
            SshTunnelManager.NormalizeRemoteDockerSocketPath(null));
        Assert.Equal(
            "/run/user/1000/docker.sock",
            SshTunnelManager.NormalizeRemoteDockerSocketPath("  /run/user/1000/docker.sock  "));

        Assert.Throws<ArgumentException>(() =>
            SshTunnelManager.NormalizeRemoteDockerSocketPath("run/docker.sock"));
        Assert.Throws<ArgumentException>(() =>
            SshTunnelManager.NormalizeRemoteDockerSocketPath("/run/docker.sock\n-oProxyCommand=bad"));
        Assert.Throws<ArgumentException>(() =>
            SshTunnelManager.NormalizeRemoteDockerSocketPath("/run/docker:engine.sock"));
    }

    [Fact]
    public void BuildOpenSshArgumentsKeepsUserValuesAsSeparateArguments()
    {
        IReadOnlyList<string> arguments = SshTunnelManager.BuildOpenSshArguments(
            "docker.example",
            2222,
            "deploy-user",
            @"C:\keys\docker key.pem",
            "/var/run/docker.sock",
            23750);

        Assert.Contains(@"C:\keys\docker key.pem", arguments);
        Assert.Contains("deploy-user@docker.example", arguments);
        Assert.Contains("23750:/var/run/docker.sock", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains('"'));
    }
}
