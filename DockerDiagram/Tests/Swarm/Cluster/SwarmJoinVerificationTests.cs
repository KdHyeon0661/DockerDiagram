using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmJoinVerificationTests
{
    [Fact]
    public void FindJoinedNode_PrefersNodeId()
    {
        var nodes = new[]
        {
            new DockerSwarmNode { Id = "node-1", Role = "worker", Address = "10.0.0.10" },
            new DockerSwarmNode { Id = "node-2", Role = "worker", Address = "10.0.0.11" }
        };

        DockerSwarmNode? result = SwarmJoinVerification.FindJoinedNode(
            nodes,
            "node-2",
            "10.0.0.10:2377",
            SwarmJoinRole.Worker);

        Assert.Equal("node-2", result?.Id);
    }

    [Theory]
    [InlineData("10.0.0.10:2377", "10.0.0.10")]
    [InlineData("[2001:db8::10]:2377", "2001:db8::10")]
    public void FindJoinedNode_FallsBackToNormalizedAdvertiseAddress(string advertiseAddress, string nodeAddress)
    {
        var nodes = new[] { new DockerSwarmNode { Id = "node-1", Role = "worker", Address = nodeAddress } };

        DockerSwarmNode? result = SwarmJoinVerification.FindJoinedNode(
            nodes,
            string.Empty,
            advertiseAddress,
            SwarmJoinRole.Worker);

        Assert.Equal("node-1", result?.Id);
    }
}
