using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerApiSwarmDiagnosticsTests
{
    [Fact]
    public async Task Diagnostics_CombinesTasksServiceNodesAndLocalClusterState()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "ID":"task-1",
                    "Annotations":{"Name":"api.1.task-1"},
                    "Slot":1,
                    "NodeID":"manager-1",
                    "DesiredState":"running",
                    "Spec":{"ContainerSpec":{"Image":"example/api:2@sha256:abc"}},
                    "Status":{
                      "State":"rejected",
                      "Err":"pull access denied for example/api",
                      "Message":"preparing",
                      "Timestamp":"2026-09-20T03:04:05Z",
                      "ContainerStatus":{"ContainerID":"container-1","ExitCode":1}
                    },
                    "CreatedAt":"2026-09-20T03:00:00Z",
                    "UpdatedAt":"2026-09-20T03:04:05Z"
                  }
                ]
                """),
            ScriptedDockerApiServer.Json("""
                {"ID":"service-1","Spec":{"Name":"api"}}
                """),
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "ID":"manager-1",
                    "Spec":{"Role":"manager","Availability":"active"},
                    "Description":{"Hostname":"manager-a","Engine":{"EngineVersion":"27.5.1"}},
                    "Status":{"State":"ready","Addr":"10.0.0.1"},
                    "ManagerStatus":{"Leader":true,"Reachability":"reachable"}
                  }
                ]
                """),
            ScriptedDockerApiServer.Json(ManagerInfo()));
        using var service = CreateService(server);

        SwarmTaskDiagnosticReport report = await service.GetSwarmTaskDiagnosticsAsync("service-1");

        Assert.Equal("service-1", report.ServiceId);
        Assert.Equal("api", report.ServiceName);
        Assert.Equal("manager-1", report.LocalNodeId);
        SwarmTaskDiagnostic task = Assert.Single(report.Tasks);
        Assert.Equal("manager-a", task.NodeName);
        Assert.Equal("example/api:2", task.Image);
        Assert.Equal("container-1", task.ContainerId);
        Assert.Equal(SwarmTaskFailureCategory.ImagePullFailure, task.Category);
        Assert.StartsWith("/tasks?filters=", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("service-1", Uri.UnescapeDataString(server.Requests[0].Target), StringComparison.Ordinal);
        Assert.Equal(("GET", "/services/service-1"), RequestIdentity(server.Requests[1]));
        Assert.Equal(("GET", "/nodes"), RequestIdentity(server.Requests[2]));
        Assert.Equal(("GET", "/info"), RequestIdentity(server.Requests[3]));
    }

    [Fact]
    public async Task Diagnostics_RejectsEmptyServiceIdWithoutNetworkCall()
    {
        await using var server = new ScriptedDockerApiServer();
        using var service = CreateService(server);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSwarmTaskDiagnosticsAsync(" "));

        Assert.Empty(server.Requests);
    }

    private static string ManagerInfo() => """
        {"Swarm":{"LocalNodeState":"active","ControlAvailable":true,"NodeID":"manager-1","NodeAddr":"10.0.0.1","Error":""}}
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
}
