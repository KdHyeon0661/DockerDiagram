using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class ComposeVolumeSyntaxTests
{
    [Theory]
    [InlineData("cache:/var/cache/app", "cache", "/var/cache/app")]
    [InlineData("cache:/var/cache/app:ro", "cache", "/var/cache/app")]
    [InlineData("C:\\\\host\\config:/etc/app:rw", "C:\\\\host\\config", "/etc/app")]
    public void ShortVolumeSyntaxSeparatesSourceTargetAndMode(
        string value,
        string expectedSource,
        string expectedTarget)
    {
        VolumeMountInfo mount = Assert.Single(ComposeYamlHelper.ToVolumeMounts(value));

        Assert.Equal(expectedSource, mount.Source);
        Assert.Equal(expectedTarget, mount.Target);
        Assert.Equal(ContainerMountParser.IsBindSource(expectedSource) ? MountKind.Bind : MountKind.Volume, mount.Kind);
        Assert.Equal(value.EndsWith(":ro", StringComparison.OrdinalIgnoreCase), mount.ReadOnly);
    }

    [Fact]
    public void AnonymousVolumeTargetIsNotInventedAsNamedSource()
    {
        VolumeMountInfo mount = Assert.Single(ComposeYamlHelper.ToVolumeMounts("/var/lib/app"));

        Assert.Equal(MountKind.Volume, mount.Kind);
        Assert.Equal(string.Empty, mount.Source);
        Assert.Equal("/var/lib/app", mount.Target);
    }
}
