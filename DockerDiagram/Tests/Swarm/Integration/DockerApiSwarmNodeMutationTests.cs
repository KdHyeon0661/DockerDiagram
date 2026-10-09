using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiSwarmNodeMutationTests
{
    [Fact]
    public async Task InspectNode_MapsVersionLabelsLeaderAndLocalIdentity()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(Node("manager-1", 4, "manager", "active", "ready", leader: true)),
            ScriptedDockerApiServer.Json(ManagerInfo()));
        using var service = CreateService(server);

        SwarmNodeEditSnapshot snapshot = await service.InspectSwarmNodeAsync("manager-1");

        Assert.Equal("manager-1", snapshot.NodeId);
        Assert.Equal(4ul, snapshot.Version);
        Assert.True(snapshot.IsLeader);
        Assert.True(snapshot.IsLocalNode);
        Assert.Equal("seoul", snapshot.Labels["zone"]);
        Assert.Equal(
            new[] { ("GET", "/info"), ("GET", "/nodes/manager-1"), ("GET", "/info") },
            server.Requests.Select(RequestIdentity));
    }

    [Fact]
    public async Task UpdateNode_UsesVersionQueryAndReplacesEditableSpec()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(Node("worker-1", 4, "worker", "active", "ready")),
            ScriptedDockerApiServer.Json("""
                [
                  {"ID":"manager-1","Spec":{"Role":"manager","Availability":"active"}},
                  {"ID":"worker-1","Spec":{"Role":"worker","Availability":"active"}}
                ]
                """),
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Empty(),
            ScriptedDockerApiServer.Json(Node("worker-1", 5, "worker", "drain", "ready")));
        using var service = CreateService(server);

        SwarmNodeMutationResult result = await service.UpdateSwarmNodeAsync(new SwarmNodeUpdateOptions
        {
            NodeId = "worker-1",
            Version = 4,
            Role = "worker",
            Availability = "drain",
            Labels = new Dictionary<string, string> { ["zone"] = "busan", ["storage"] = "ssd" }
        });

        Assert.Equal(5ul, result.Version);
        ScriptedDockerApiServer.Request update = server.Requests[4];
        Assert.Equal("POST", update.Method);
        Assert.Equal("/nodes/worker-1/update?version=4", update.Target);
        JObject body = JObject.Parse(update.Body);
        Assert.Equal("worker", body.Value<string>("Role"));
        Assert.Equal("drain", body.Value<string>("Availability"));
        Assert.Equal("busan", body["Labels"]!.Value<string>("zone"));
        Assert.Equal("ssd", body["Labels"]!.Value<string>("storage"));
    }

    [Fact]
    public async Task RemoveDownWorker_UsesExplicitForceQueryAfterSafetyChecks()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(Node("worker-1", 4, "worker", "drain", "down")),
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        await service.RemoveSwarmNodeAsync("worker-1", force: false);

        ScriptedDockerApiServer.Request remove = server.Requests[^1];
        Assert.Equal("DELETE", remove.Method);
        Assert.Equal("/nodes/worker-1?force=false", remove.Target);
    }

    private static string Node(
        string id,
        ulong version,
        string role,
        string availability,
        string state,
        bool leader = false) => $$$"""
        {
          "ID":"{{{id}}}",
          "Version":{"Index":{{{version}}}},
          "Spec":{"Role":"{{{role}}}","Availability":"{{{availability}}}","Labels":{"zone":"seoul"}},
          "Description":{"Hostname":"{{{id}}}-host"},
          "Status":{"State":"{{{state}}}","Addr":"10.0.0.2"},
          "ManagerStatus":{"Leader":{{{leader.ToString().ToLowerInvariant()}}},"Reachability":"reachable"}
        }
        """;

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
