using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmDiagramTopologyCompilerTests
{
    [Fact]
    public void Apply_ReplacesDiagramOwnedTopologyAndPreservesServiceSettings()
    {
        SwarmServiceSpecOptions baseline = Baseline();
        var topology = new SwarmDiagramTopologyInput
        {
            HasDiagramPublishedPorts = true,
            PublishedPorts = new[] { new SwarmPublishedPortOptions(443, 8443) },
            HasDiagramMounts = true,
            Mounts = new[] { new SwarmMountOptions(SwarmMountKind.Volume, "web-data", "/data") },
            HasDiagramNetworks = true,
            Networks = new[] { new SwarmNetworkAttachmentOptions("overlay-id") }
        };

        SwarmServiceSpecOptions result = SwarmDiagramTopologyCompiler.Apply(baseline, topology);

        Assert.Equal(baseline.Name, result.Name);
        Assert.Equal(baseline.Image, result.Image);
        Assert.Equal(baseline.EnvironmentVariables, result.EnvironmentVariables);
        Assert.Equal(8443U, result.PublishedPorts.Single().PublishedPort);
        Assert.Equal("web-data", result.Mounts.Single().Source);
        Assert.Equal("overlay-id", result.Networks.Single().Target);
    }

    [Fact]
    public void Apply_EmptyDiagramCategories_DoNotEraseExistingRuntimeSettings()
    {
        SwarmServiceSpecOptions baseline = Baseline();

        SwarmServiceSpecOptions result = SwarmDiagramTopologyCompiler.Apply(
            baseline,
            new SwarmDiagramTopologyInput());

        Assert.Same(baseline.PublishedPorts, result.PublishedPorts);
        Assert.Same(baseline.Mounts, result.Mounts);
        Assert.Same(baseline.Networks, result.Networks);
    }

    [Fact]
    public void Apply_ManagedEmptyCategories_ClearExistingRuntimeSettings()
    {
        SwarmServiceSpecOptions baseline = Baseline();
        var topology = new SwarmDiagramTopologyInput
        {
            HasDiagramPublishedPorts = true,
            HasDiagramMounts = true,
            HasDiagramNetworks = true
        };

        SwarmServiceSpecOptions result = SwarmDiagramTopologyCompiler.Apply(baseline, topology);

        Assert.Empty(result.PublishedPorts);
        Assert.Empty(result.Mounts);
        Assert.Empty(result.Networks);
    }

    [Fact]
    public void Apply_OnlyPorts_PreservesMountsAndNetworks()
    {
        SwarmServiceSpecOptions baseline = Baseline();
        var topology = new SwarmDiagramTopologyInput
        {
            HasDiagramPublishedPorts = true,
            PublishedPorts = new[] { new SwarmPublishedPortOptions(8080, 18080) }
        };

        SwarmServiceSpecOptions result = SwarmDiagramTopologyCompiler.Apply(baseline, topology);

        Assert.Equal(18080U, result.PublishedPorts.Single().PublishedPort);
        Assert.Same(baseline.Mounts, result.Mounts);
        Assert.Same(baseline.Networks, result.Networks);
    }

    [Fact]
    public void Apply_DuplicatePublishedPort_IsRejected()
    {
        var topology = new SwarmDiagramTopologyInput
        {
            HasDiagramPublishedPorts = true,
            PublishedPorts = new[]
            {
                new SwarmPublishedPortOptions(80, 8080),
                new SwarmPublishedPortOptions(81, 8080)
            }
        };

        Assert.Throws<ArgumentException>(() => SwarmDiagramTopologyCompiler.Apply(Baseline(), topology));
    }

    [Fact]
    public void Apply_DuplicateMountTarget_IsRejected()
    {
        var topology = new SwarmDiagramTopologyInput
        {
            HasDiagramMounts = true,
            Mounts = new[]
            {
                new SwarmMountOptions(SwarmMountKind.Volume, "first", "/data"),
                new SwarmMountOptions(SwarmMountKind.Volume, "second", "/data")
            }
        };

        Assert.Throws<ArgumentException>(() => SwarmDiagramTopologyCompiler.Apply(Baseline(), topology));
    }

    private static SwarmServiceSpecOptions Baseline() => new()
    {
        Name = "web",
        Image = "example/web:1.0",
        Replicas = 2,
        EnvironmentVariables = new[] { "APP_ENV=production" },
        PublishedPorts = new[] { new SwarmPublishedPortOptions(80, 8080) },
        Mounts = new[] { new SwarmMountOptions(SwarmMountKind.Volume, "legacy-data", "/legacy") },
        Networks = new[] { new SwarmNetworkAttachmentOptions("legacy-network") }
    };
}
