using DockerDiagram.Diagram;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmResourceFilterTests
{
    [Fact]
    public void IsOverlayNetwork_AcceptsUserOverlay()
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
    public void IsOverlayNetwork_RejectsRoutingMeshIngress()
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
    public void IsOverlayNetwork_RejectsLocalBridge()
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
