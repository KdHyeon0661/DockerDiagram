using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmLeavePolicyTests
{
    [Fact]
    public void Evaluate_WorkerLeavesWithoutForce()
    {
        SwarmClusterState state = SwarmClusterState.Create("active", false, nodeId: "worker-1");

        SwarmLeavePlan plan = SwarmLeavePolicy.Evaluate(state);

        Assert.Equal("worker-1", plan.NodeId);
        Assert.False(plan.Force);
    }

    [Fact]
    public void Evaluate_SingleManagerUsesForce()
    {
        SwarmClusterState state = SwarmClusterState.Create("active", true, nodeId: "manager-1");
        var nodes = new[] { new DockerSwarmNode { Id = "manager-1", Role = "manager" } };

        SwarmLeavePlan plan = SwarmLeavePolicy.Evaluate(state, nodes);

        Assert.True(plan.Force);
    }

    [Fact]
    public void Evaluate_RejectsManagerInMultiNodeCluster()
    {
        SwarmClusterState state = SwarmClusterState.Create("active", true, nodeId: "manager-1");
        var nodes = new[]
        {
            new DockerSwarmNode { Id = "manager-1", Role = "manager" },
            new DockerSwarmNode { Id = "worker-1", Role = "worker" }
        };

        Assert.Throws<InvalidOperationException>(() => SwarmLeavePolicy.Evaluate(state, nodes));
    }
}
