using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmReadinessAuditServiceTests
{
    [Fact]
    public async Task Audit_ManagerQueriesEngineClusterNodesAndServices()
    {
        await using var server = new ScriptedDockerApiServer(
            new ScriptedDockerApiServer.Response(200, "OK", "text/plain"),
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(NodeList()),
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "ID":"service-1",
                    "Spec":{
                      "Name":"api",
                      "TaskTemplate":{"ContainerSpec":{"Image":"example/api:2"}},
                      "Mode":{"Replicated":{"Replicas":1}}
                    },
                    "ServiceStatus":{"RunningTasks":1,"DesiredTasks":1}
                  }
                ]
                """));
        using var docker = CreateService(server);
        var audit = new SwarmReadinessAuditService(
            new StubCliProbe(new DockerCliProbeResult(true, "29.8.0", string.Empty)));

        SwarmReadinessReport report = await audit.AuditAsync(docker);

        Assert.Equal(SwarmReadinessSeverity.Pass, report.OverallSeverity);
        Assert.Contains(report.Checks, check => check.Name == "Service inventory" && check.Severity == SwarmReadinessSeverity.Pass);
        Assert.Equal(
            new[] { ("GET", "/_ping"), ("GET", "/info"), ("GET", "/nodes"), ("GET", "/services") },
            server.Requests.Select(RequestIdentity));
    }

    [Fact]
    public async Task Audit_UnreachableEngineStopsBeforeSwarmQueries()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""{"message":"engine unavailable"}""", 500));
        using var docker = CreateService(server);
        var audit = new SwarmReadinessAuditService(
            new StubCliProbe(new DockerCliProbeResult(false, string.Empty, "docker CLI missing")));

        SwarmReadinessReport report = await audit.AuditAsync(docker);

        Assert.Equal(SwarmReadinessSeverity.Blocked, report.OverallSeverity);
        Assert.Contains(report.Checks, check => check.Name == "Docker Engine" && check.Severity == SwarmReadinessSeverity.Blocked);
        Assert.Single(server.Requests);
        Assert.Equal(("GET", "/_ping"), RequestIdentity(server.Requests[0]));
    }

    private static string ManagerInfo() => """
        {"Swarm":{"LocalNodeState":"active","ControlAvailable":true,"NodeID":"manager-1","NodeAddr":"10.0.0.1","Error":""}}
        """;

    private static string NodeList() => """
        [
          {
            "ID":"manager-1",
            "Spec":{"Role":"manager","Availability":"active"},
            "Description":{"Hostname":"manager-a","Engine":{"EngineVersion":"27.5.1"}},
            "Status":{"State":"ready","Addr":"10.0.0.1"},
            "ManagerStatus":{"Leader":true,"Reachability":"reachable"}
          }
        ]
        """;

    private static DockerApiService CreateService(ScriptedDockerApiServer server) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = RuntimeKind.DockerSwarm,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });

    private static (string Method, string Target) RequestIdentity(ScriptedDockerApiServer.Request request) =>
        (request.Method, request.Target);

    private sealed class StubCliProbe(DockerCliProbeResult result) : IDockerCliProbe
    {
        public Task<DockerCliProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
