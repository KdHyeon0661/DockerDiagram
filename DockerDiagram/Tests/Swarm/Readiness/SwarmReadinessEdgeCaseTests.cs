using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmReadinessEdgeCaseTests
{
    [Fact]
    public void PausedNode_IsReportedAsNotSchedulable()
    {
        IReadOnlyList<SwarmReadinessCheck> checks = Evaluate(
            Manager("one", "leader"),
            new DockerSwarmNode
            {
                Id = "worker",
                Name = "worker",
                Hostname = "worker",
                Role = "worker",
                Availability = "pause",
                Status = "ready",
                EngineVersion = "27.5.1"
            });

        Assert.Contains(checks, check =>
            check.Name == "Scheduling" && check.Severity == SwarmReadinessSeverity.Warning);
    }

    [Fact]
    public void UnknownManagerStatus_DoesNotCountTowardQuorum()
    {
        IReadOnlyList<SwarmReadinessCheck> checks = Evaluate(
            Manager("one", "leader"),
            Manager("two", "unknown"),
            Manager("three", string.Empty));

        Assert.Contains(checks, check =>
            check.Name == "Quorum reachability" && check.Severity == SwarmReadinessSeverity.Blocked);
    }

    private static IReadOnlyList<SwarmReadinessCheck> Evaluate(params DockerSwarmNode[] nodes) =>
        SwarmReadinessPolicy.Evaluate(
            true,
            new DockerCliProbeResult(true, "29.8.0", string.Empty),
            SwarmClusterState.Create("active", true, nodeId: "one"),
            nodes,
            serviceCount: 0);

    private static DockerSwarmNode Manager(string id, string managerStatus) => new()
    {
        Id = id,
        Name = id,
        Hostname = id,
        Role = "manager",
        Availability = "active",
        Status = "ready",
        ManagerStatus = managerStatus,
        EngineVersion = "27.5.1"
    };
}
