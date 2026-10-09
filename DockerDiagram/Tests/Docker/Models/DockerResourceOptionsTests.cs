using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerResourceOptionsTests
{
    [Fact]
    public void VolumeOptions_UseExplicitDockerNameAndDefaultDriver()
    {
        VolumeCreateOptions options = VolumeCreateOptions.Basic("diagram-name", string.Empty);
        options.DockerVolumeName = "existing-volume";

        Assert.Equal("existing-volume", options.EffectiveDockerVolumeName);
        Assert.Equal("local", options.Driver);
    }

    [Fact]
    public void NetworkOptions_DetectEveryIpamSource()
    {
        Assert.True(new NetworkCreateOptions { Subnet = "10.20.0.0/16" }.HasIpam);
        Assert.True(new NetworkCreateOptions { Gateway = "10.20.0.1" }.HasIpam);
        Assert.True(new NetworkCreateOptions { IpRange = "10.20.1.0/24" }.HasIpam);
        Assert.True(new NetworkCreateOptions
        {
            AuxAddresses = new Dictionary<string, string> { ["router"] = "10.20.0.2" }
        }.HasIpam);
        Assert.False(new NetworkCreateOptions().HasIpam);
    }

    [Fact]
    public void PruneSummary_ReportsCountsSpaceAndDeletedIdentities()
    {
        var result = new DockerPruneResult
        {
            ContainersDeleted = new List<string> { "container-1" },
            ImagesDeleted = new List<DockerPruneImageDelete>
            {
                new() { Untagged = "example/api:old" }
            },
            VolumesDeleted = new List<string> { "cache" },
            SpaceReclaimed = 1024
        };

        Assert.Contains("Containers: 1", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Images: 1", result.Summary, StringComparison.Ordinal);
        Assert.Contains("1 KB", result.Summary, StringComparison.Ordinal);
        Assert.Contains("- example/api:old", result.Summary, StringComparison.Ordinal);
    }
}
