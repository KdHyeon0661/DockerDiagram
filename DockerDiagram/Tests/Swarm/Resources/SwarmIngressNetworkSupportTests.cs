using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmIngressNetworkSupportTests
{
    [Fact]
    public void RequiresIngressOnlyForIngressPublishedPorts()
    {
        var ingress = new SwarmServiceSpecOptions
        {
            PublishedPorts = new[]
            {
                new SwarmPublishedPortOptions(80, 8080, SwarmPortProtocol.Tcp, SwarmPublishMode.Ingress)
            }
        };
        var host = new SwarmServiceSpecOptions
        {
            PublishedPorts = new[]
            {
                new SwarmPublishedPortOptions(80, 8080, SwarmPortProtocol.Tcp, SwarmPublishMode.Host)
            }
        };

        Assert.True(SwarmIngressNetworkSupport.RequiresIngress(ingress));
        Assert.False(SwarmIngressNetworkSupport.RequiresIngress(host));
        Assert.False(SwarmIngressNetworkSupport.RequiresIngress(new SwarmServiceSpecOptions()));
    }

    [Fact]
    public void HasIngressNetworkUsesApiFlagAndAcceptsLegacyDockerIdentity()
    {
        Assert.True(SwarmIngressNetworkSupport.HasIngressNetwork(new[]
        {
            new DockerNetworkGroup { Name = "routing-mesh", Ingress = true }
        }));
        Assert.True(SwarmIngressNetworkSupport.HasIngressNetwork(new[]
        {
            new DockerNetworkGroup { Name = "ingress", Driver = "overlay", Scope = "swarm" }
        }));
        Assert.False(SwarmIngressNetworkSupport.HasIngressNetwork(new[]
        {
            new DockerNetworkGroup { Name = "ingress", Driver = "bridge", Scope = "local" }
        }));
    }
}
