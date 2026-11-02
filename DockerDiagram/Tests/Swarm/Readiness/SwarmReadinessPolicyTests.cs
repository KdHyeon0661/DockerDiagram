using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmReadinessPolicyTests
{
    [Fact]
    public void UnreachableEngineIsBlockedWithoutInventingClusterState()
    {
        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            false,
            new DockerCliProbeResult(true, "29.8.0", string.Empty),
            null,
            null,
            null);

        Assert.Contains(checks, check =>
            check.Name == "Docker Engine" && check.Severity == SwarmReadinessSeverity.Blocked);
        Assert.DoesNotContain(checks, check => check.Name == "Manager count");
    }

    [Fact]
    public void HealthyThreeManagerClusterPassesReadinessPolicy()
    {
        SwarmClusterState cluster = SwarmClusterState.Create(
            "active",
            true,
            nodeId: "manager-one",
            nodeAddress: "10.0.0.10");
        IReadOnlyList<DockerSwarmNode> nodes = new[]
        {
            Manager("one", "leader", "27.5.1"),
            Manager("two", "reachable", "27.5.1"),
            Manager("three", "reachable", "27.5.1"),
            Worker("worker-one", "27.5.1")
        };

        var report = new SwarmReadinessReport(
            DateTimeOffset.Now,
            SwarmReadinessPolicy.Evaluate(
                true,
                new DockerCliProbeResult(true, "29.8.0", string.Empty),
                cluster,
                nodes,
                serviceCount: 4));

        Assert.Equal(SwarmReadinessSeverity.Pass, report.OverallSeverity);
        Assert.Contains(report.Checks, check =>
            check.Name == "Quorum reachability" && check.Severity == SwarmReadinessSeverity.Pass);
        Assert.Contains(report.Checks, check =>
            check.Name == "Leader" && check.Severity == SwarmReadinessSeverity.Pass);
    }

    [Fact]
    public void EvenManagerCountIsWarning()
    {
        SwarmClusterState cluster = SwarmClusterState.Create("active", true, nodeId: "one");
        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            true,
            new DockerCliProbeResult(true, "29.8.0", string.Empty),
            cluster,
            new[]
            {
                Manager("one", "leader", "27.5.1"),
                Manager("two", "reachable", "27.5.1")
            },
            serviceCount: 0);

        Assert.Contains(checks, check =>
            check.Name == "Manager count" && check.Severity == SwarmReadinessSeverity.Warning);
    }

    [Fact]
    public void SingleManagerAndNodeWarnsThatFailoverCannotBeVerified()
    {
        SwarmClusterState cluster = SwarmClusterState.Create("active", true, nodeId: "one");
        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            true,
            new DockerCliProbeResult(true, "29.8.0", string.Empty),
            cluster,
            new[] { Manager("one", "leader", "29.8.0") },
            serviceCount: 0);

        Assert.Contains(checks, check =>
            check.Name == "Manager count" && check.Severity == SwarmReadinessSeverity.Warning);
        Assert.Contains(checks, check =>
            check.Name == "Failure recovery" && check.Severity == SwarmReadinessSeverity.Warning);
    }

    [Fact]
    public void LostManagerMajorityIsBlocked()
    {
        SwarmClusterState cluster = SwarmClusterState.Create("active", true, nodeId: "one");
        var nodes = new[]
        {
            Manager("one", "leader", "27.5.1"),
            Manager("two", "unreachable", "27.5.1", status: "down"),
            Manager("three", "unreachable", "27.5.1", status: "down")
        };

        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            true,
            new DockerCliProbeResult(true, "29.8.0", string.Empty),
            cluster,
            nodes,
            serviceCount: null,
            serviceQueryError: "control plane unavailable");

        Assert.Contains(checks, check =>
            check.Name == "Quorum reachability" && check.Severity == SwarmReadinessSeverity.Blocked);
    }

    [Fact]
    public void MissingDockerCliIsWarningBecauseApiInspectionStillWorks()
    {
        SwarmClusterState cluster = SwarmClusterState.Create("active", false, nodeId: "worker");

        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            true,
            new DockerCliProbeResult(false, string.Empty, "not found"),
            cluster,
            null,
            null);

        Assert.Contains(checks, check =>
            check.Name == "Docker CLI" && check.Severity == SwarmReadinessSeverity.Warning);
        Assert.Contains(checks, check =>
            check.Name == "Local role" && check.Severity == SwarmReadinessSeverity.Warning);
    }

    [Fact]
    public void MixedEngineVersionsAreVisibleAsWarning()
    {
        SwarmClusterState cluster = SwarmClusterState.Create("active", true, nodeId: "one");
        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            true,
            new DockerCliProbeResult(true, "29.8.0", string.Empty),
            cluster,
            new[]
            {
                Manager("one", "leader", "27.5.1"),
                Worker("worker", "26.1.4")
            },
            serviceCount: 1);

        Assert.Contains(checks, check =>
            check.Name == "Engine versions" && check.Severity == SwarmReadinessSeverity.Warning);
    }

    [Fact]
    public void MissingIngressNetworkIsVisibleAsWarningWithoutBlockingHostPublishing()
    {
        SwarmClusterState cluster = SwarmClusterState.Create("active", true, nodeId: "one");

        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            true,
            new DockerCliProbeResult(true, "29.8.0", string.Empty),
            cluster,
            new[] { Manager("one", "leader", "27.5.1") },
            serviceCount: 0,
            ingressNetworkAvailable: false);

        Assert.Contains(checks, check =>
            check.Name == "Routing mesh ingress" && check.Severity == SwarmReadinessSeverity.Warning);
    }

    [Fact]
    public void AvailableIngressNetworkPassesNetworkingReadiness()
    {
        SwarmClusterState cluster = SwarmClusterState.Create("active", true, nodeId: "one");

        IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
            true,
            new DockerCliProbeResult(true, "29.8.0", string.Empty),
            cluster,
            new[] { Manager("one", "leader", "27.5.1") },
            serviceCount: 0,
            ingressNetworkAvailable: true);

        Assert.Contains(checks, check =>
            check.Name == "Routing mesh ingress" && check.Severity == SwarmReadinessSeverity.Pass);
    }

    private static DockerSwarmNode Manager(
        string id,
        string managerStatus,
        string version,
        string status = "ready") => new()
    {
        Id = id,
        Name = id,
        Hostname = id,
        Role = "manager",
        Availability = "active",
        Status = status,
        ManagerStatus = managerStatus,
        EngineVersion = version
    };

    private static DockerSwarmNode Worker(string id, string version) => new()
    {
        Id = id,
        Name = id,
        Hostname = id,
        Role = "worker",
        Availability = "active",
        Status = "ready",
        EngineVersion = version
    };
}
