using DockerDiagram.Diagram;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmResourceFilterTests
{
    [Fact]
    public void IsOverlayNetworkAcceptsUserOverlay()
    {
        var network = new DockerNetworkGroup
        {
            Name = "app-mesh",
            Driver = "overlay",
            Scope = "swarm"
        };

        Assert.True(SwarmResourceFilter.IsOverlayNetwork(network));
    }

    [Fact]
    public void IsOverlayNetworkRejectsRoutingMeshIngress()
    {
        var network = new DockerNetworkGroup
        {
            Name = "ingress",
            Driver = "overlay",
            Scope = "swarm"
        };

        Assert.False(SwarmResourceFilter.IsOverlayNetwork(network));
    }

    [Fact]
    public void IsOverlayNetworkRejectsIngressFlagEvenWhenNameIsUnexpected()
    {
        var network = new DockerNetworkGroup
        {
            Name = "routing-mesh",
            Driver = "overlay",
            Scope = "swarm",
            Ingress = true
        };

        Assert.False(SwarmResourceFilter.IsOverlayNetwork(network));
    }

    [Fact]
    public void IsOverlayNetworkRejectsLocalBridge()
    {
        var network = new DockerNetworkGroup
        {
            Name = "bridge-app",
            Driver = "bridge",
            Scope = "local"
        };

        Assert.False(SwarmResourceFilter.IsOverlayNetwork(network));
    }
}
