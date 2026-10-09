using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceNodeProjectionTests
{
    [Fact]
    public void InitialSummary_ReplicatedWithPorts_IncludesReplicaAndPortData()
    {
        var spec = new SwarmServiceSpecOptions
        {
            Name = "web",
            Image = "nginx",
            Replicas = 3,
            PublishedPorts = new[]
            {
                new SwarmPublishedPortOptions(80, 8080, SwarmPortProtocol.Tcp, SwarmPublishMode.Ingress)
            }
        };

        Assert.Equal("0/3 · 8080->80/tcp (ingress)", SwarmServiceNodeProjection.InitialSummary(spec));
        Assert.Equal(new[] { "8080->80/tcp (ingress)" }, SwarmServiceNodeProjection.PortBindings(spec));
    }

    [Fact]
    public void InitialSummary_GlobalWithoutPorts_UsesGlobalMode()
    {
        var spec = new SwarmServiceSpecOptions
        {
            Name = "agent",
            Image = "example/agent",
            Mode = SwarmServiceModeKind.Global,
            Replicas = null
        };

        Assert.Equal("global", SwarmServiceNodeProjection.ModeText(spec));
        Assert.Equal("global / 0 running", SwarmServiceNodeProjection.InitialSummary(spec));
    }

    [Theory]
    [InlineData(SwarmRestartCondition.None, "none")]
    [InlineData(SwarmRestartCondition.OnFailure, "on-failure")]
    [InlineData(SwarmRestartCondition.Any, "any")]
    public void RestartText_UsesDockerNames(SwarmRestartCondition condition, string expected)
    {
        Assert.Equal(expected, SwarmServiceNodeProjection.RestartText(new SwarmRestartPolicyOptions { Condition = condition }));
    }
}
