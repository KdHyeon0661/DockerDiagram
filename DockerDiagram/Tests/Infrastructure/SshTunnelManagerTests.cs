using System.Diagnostics;
using DockerDiagram.Infrastructure;

namespace DockerDiagram.Tests;

public sealed class SshTunnelManagerTests
{
    [Fact]
    public void IsProcessAlive_ReturnsFalseForExitedProcess()
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
}
