using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiSwarmQueryTests
{
    [Fact]
    public async Task ScaleService_SendsVersionAsQueryAndReplicasInSpec()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {
                  "Version":{"Index":7},
                  "Spec":{
                    "Name":"api",
                    "TaskTemplate":{"ContainerSpec":{"Image":"example/api:2"}},
                    "Mode":{"Replicated":{"Replicas":2}}
                  }
                }
                """),
            ScriptedDockerApiServer.Empty());
        using var service = CreateService(server);

        await service.ScaleSwarmServiceAsync("service-1", 5);

        Assert.Equal(("GET", "/services/service-1"), RequestIdentity(server.Requests[0]));
        Assert.Equal("POST", server.Requests[1].Method);
        Assert.Equal("/services/service-1/update?version=7", server.Requests[1].Target);
        JObject spec = JObject.Parse(server.Requests[1].Body);
        Assert.Equal(5ul, spec["Mode"]!["Replicated"]!.Value<ulong>("Replicas"));
    }

    [Fact]
    public async Task ServiceTasks_SendFilterAsQueryAndResolveNodeName()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "ID":"task-1",
                    "Slot":1,
                    "NodeID":"node-1",
                    "DesiredState":"running",
                    "Spec":{"ContainerSpec":{"Image":"nginx:1.27@sha256:abc"}},
                    "Status":{
                      "State":"running",
                      "ContainerStatus":{"ContainerID":"container-1"}
                    }
                  }
                ]
                """),
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "ID":"node-1",
                    "Spec":{"Role":"worker","Availability":"active"},
                    "Description":{"Hostname":"worker-a","Engine":{"EngineVersion":"27.5.1"}},
                    "Status":{"State":"ready","Addr":"10.0.0.2"}
                  }
                ]
                """));
        using var service = CreateService(server);

        DockerSwarmTask task = Assert.Single(await service.GetSwarmServiceTasksAsync("service-1"));

        Assert.Equal("worker-a", task.NodeName);
        Assert.Equal("nginx:1.27", task.Image);
        Assert.StartsWith("/tasks?filters=", server.Requests[0].Target, StringComparison.Ordinal);
        Assert.Contains("service-1", Uri.UnescapeDataString(server.Requests[0].Target), StringComparison.Ordinal);
        Assert.Equal(("GET", "/nodes"), RequestIdentity(server.Requests[1]));
    }

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
