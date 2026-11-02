using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using System.Diagnostics;

namespace DockerDiagram.Tests;

public sealed class DockerComposeCliServiceTests
{
    [Fact]
    public void CreateStartInfoBuildsDetachedComposeCommandForSelectedEndpoint()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"DockerDiagram Compose {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string composePath = Path.Combine(directory, "compose.yml");
        File.WriteAllText(composePath, "services: {}\n");

        try
        {
            ProcessStartInfo startInfo = DockerComposeCliService.CreateStartInfo(
                composePath,
                new ConnectionProfile
                {
                    Type = EndpointType.DockerContext,
                    DockerEndpoint = "tcp://manager.example:2376"
                });

            Assert.Equal("docker", startInfo.FileName);
            Assert.Equal(
                new[] { "compose", "-f", composePath, "up", "-d" },
                startInfo.ArgumentList.ToArray());
            Assert.Equal(directory, startInfo.WorkingDirectory);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.False(startInfo.UseShellExecute);
            Assert.Equal("tcp://manager.example:2376", startInfo.Environment["DOCKER_HOST"]);
            Assert.False(startInfo.Environment.ContainsKey("DOCKER_CONTEXT"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateStartInfoRejectsMissingComposeFileBeforeStartingDocker()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.yml");

        Assert.Throws<FileNotFoundException>(() =>
            DockerComposeCliService.CreateStartInfo(missingPath, new ConnectionProfile()));
    }
}
