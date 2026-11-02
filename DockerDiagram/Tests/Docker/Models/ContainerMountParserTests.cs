using Docker.DotNet.Models;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class ContainerMountParserTests
{
    [Theory]
    [InlineData("data:/data", MountKind.Volume, "data", "/data", false)]
    [InlineData("data:/data:ro", MountKind.Volume, "data", "/data", true)]
    [InlineData("C:\\host\\data:/data:rw", MountKind.Bind, "C:\\host\\data", "/data", false)]
    [InlineData("/host/data:/data:ro", MountKind.Bind, "/host/data", "/data", true)]
    [InlineData("/anonymous", MountKind.Volume, "", "/anonymous", false)]
    public void LegacySyntaxClassifiesMountsWithoutLosingWindowsDriveColons(
        string value,
        MountKind expectedKind,
        string expectedSource,
        string expectedTarget,
        bool expectedReadOnly)
    {
        ContainerMountSpec mount = ContainerMountParser.ParseLegacy(value);

        Assert.Equal(expectedKind, mount.Kind);
        Assert.Equal(expectedSource, mount.Source);
        Assert.Equal(expectedTarget, mount.Target);
        Assert.Equal(expectedReadOnly, mount.ReadOnly);
    }

    [Fact]
    public void DockerRunTokensPreserveAllSupportedMountTypesAndOptions()
    {
        List<ContainerMountSpec> mounts = ContainerMountParser.ParseDockerRunTokens(
        [
            "docker", "run",
            "-vdata:/data:ro",
            "--mount", "type=bind,source=C:\\host,target=/host,readonly,bind-propagation=rshared",
            "--tmpfs=/cache:size=2g,mode=1755,ro",
            "alpine"
        ]);

        Assert.Collection(
            mounts,
            volume =>
            {
                Assert.Equal(MountKind.Volume, volume.Kind);
                Assert.Equal("data", volume.Source);
                Assert.True(volume.ReadOnly);
            },
            bind =>
            {
                Assert.Equal(MountKind.Bind, bind.Kind);
                Assert.Equal("C:\\host", bind.Source);
                Assert.Equal("rshared", bind.BindPropagation);
                Assert.True(bind.ReadOnly);
            },
            tmpfs =>
            {
                Assert.Equal(MountKind.Tmpfs, tmpfs.Kind);
                Assert.Equal(2L * 1024 * 1024 * 1024, tmpfs.TmpfsSizeBytes);
                Assert.Equal(Convert.ToUInt32("1755", 8), tmpfs.TmpfsMode);
                Assert.True(tmpfs.ReadOnly);
            });
    }

    [Fact]
    public void DuplicateContainerTargetsAreRejectedAcrossMountTypes()
    {
        FormatException error = Assert.Throws<FormatException>(() =>
            ContainerMountParser.ParseDockerRunTokens(
            [
                "docker", "run",
                "--volume", "data:/shared",
                "--tmpfs", "/shared",
                "alpine"
            ]));

        Assert.Contains("/shared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteSizeParserSupportsTerabytesAndRejectsUnknownSuffixes()
    {
        Assert.Equal(2L * 1024 * 1024 * 1024 * 1024, ContainerMountParser.ParseByteSize("2TiB"));
        Assert.Throws<FormatException>(() => ContainerMountParser.ParseByteSize("12 elephants"));
    }

    [Theory]
    [InlineData("\\\\server\\share", true)]
    [InlineData("\\\\?\\C:\\data", true)]
    [InlineData("C:\\data", true)]
    [InlineData("/srv/data", true)]
    [InlineData("./data", false)]
    [InlineData("../data", false)]
    [InlineData("named-volume", false)]
    public void AbsoluteHostPathRecognizesWindowsLinuxAndUncWithoutAcceptingRelativePaths(
        string source,
        bool expected)
    {
        Assert.Equal(expected, ContainerMountParser.IsAbsoluteHostPath(source));
    }

    [Fact]
    public void LegacySyntaxClassifiesUncSourceAsBindMount()
    {
        ContainerMountSpec mount = ContainerMountParser.ParseLegacy("\\\\server\\share:/data:ro");

        Assert.Equal(MountKind.Bind, mount.Kind);
        Assert.Equal("\\\\server\\share", mount.Source);
        Assert.Equal("/data", mount.Target);
        Assert.True(mount.ReadOnly);
    }

    [Theory]
    [InlineData("docker run -v ./relative:/data alpine")]
    [InlineData("docker run -v data:relative alpine")]
    [InlineData("docker run --mount type=bind,source=./relative,target=/data alpine")]
    public void DockerRunRejectsRelativeRuntimeMountPaths(string command)
    {
        string[] tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Throws<FormatException>(() => ContainerMountParser.ParseDockerRunTokens(tokens));
    }

    [Fact]
    public void LongSyntaxRejectsUnsupportedMountType()
    {
        Assert.Throws<FormatException>(() =>
            ContainerMountParser.ParseLongMount("type=secret,source=token,target=/run/token"));
    }

    [Fact]
    public void HostPathComparisonUsesWindowsRulesOnlyForWindowsAndUncPaths()
    {
        Assert.True(ContainerMountParser.HostPathsEqual("C:\\Data\\", "c:\\data"));
        Assert.True(ContainerMountParser.HostPathsEqual("\\\\SERVER\\Share", "\\\\server\\share\\"));
        Assert.False(ContainerMountParser.HostPathsEqual("/srv/Data", "/srv/data"));
    }

    [Fact]
    public void InspectMapperUsesTargetToRestoreAdvancedConfiguration()
    {
        var actual = new[]
        {
            new MountPoint { Type = "volume", Name = "data", Source = "/docker/volumes/data", Destination = "/data", RW = false },
            new MountPoint { Type = "bind", Source = "C:\\host-normalized", Destination = "/host", RW = true },
            new MountPoint { Type = "tmpfs", Destination = "/run/cache", RW = true }
        };
        var configured = new[]
        {
            new Mount { Type = "volume", Source = "data", Target = "/data", VolumeOptions = new VolumeOptions { NoCopy = true } },
            new Mount { Type = "bind", Source = "C:\\host", Target = "/host", BindOptions = new BindOptions { Propagation = "rshared" } },
            new Mount { Type = "tmpfs", Target = "/run/cache", TmpfsOptions = new TmpfsOptions { SizeBytes = 4096, Mode = Convert.ToUInt32("1770", 8) } }
        };

        List<ContainerMountSpec> mounts = ContainerMountMapper.FromInspect(actual, configured);

        Assert.True(mounts[0].VolumeNoCopy);
        Assert.True(mounts[0].ReadOnly);
        Assert.Equal("rshared", mounts[1].BindPropagation);
        Assert.Equal(4096, mounts[2].TmpfsSizeBytes);
        Assert.Equal(Convert.ToUInt32("1770", 8), mounts[2].TmpfsMode);
    }
}
