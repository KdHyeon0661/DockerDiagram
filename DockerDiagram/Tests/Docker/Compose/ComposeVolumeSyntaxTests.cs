using DockerDiagram.ApplicationServices;

namespace DockerDiagram.Tests;

public sealed class ComposeVolumeSyntaxTests
{
    [Theory]
    [InlineData("cache:/var/cache/app", "cache", "/var/cache/app")]
    [InlineData("cache:/var/cache/app:ro", "cache", "/var/cache/app")]
    [InlineData("C:\\\\host\\config:/etc/app:rw", "C:\\\\host\\config", "/etc/app")]
    public void ShortVolumeSyntax_SeparatesSourceTargetAndMode(
        string value,
        string expectedSource,
        string expectedTarget)
    {
        VolumeMountInfo mount = Assert.Single(ComposeYamlHelper.ToVolumeMounts(value));

        Assert.Equal(expectedSource, mount.Source);
        Assert.Equal(expectedTarget, mount.Target);
    }

    [Fact]
    public void AnonymousVolumeTarget_IsNotInventedAsNamedSource()
    {
        Assert.Empty(ComposeYamlHelper.ToVolumeMounts("/var/lib/app"));
    }
}
