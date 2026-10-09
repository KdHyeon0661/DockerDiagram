using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmReadinessUnknownStateTests
{
    [Fact]
    public void UnknownNodeFacts_DoNotReportPass()
    {
        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            engineReachable: true,
            new DockerCliProbeResult(true, "28.0.0", string.Empty),
            SwarmClusterState.Create("active", controlAvailable: true, nodeId: "manager"),
            Array.Empty<DockerSwarmNode>(),
            serviceCount: 0);

        Assert.Equal(
            SwarmReadinessSeverity.Warning,
            checks.Single(check => check.Name == "Engine versions").Severity);
        Assert.Equal(
            SwarmReadinessSeverity.Blocked,
            checks.Single(check => check.Name == "Scheduling").Severity);
    }
}
