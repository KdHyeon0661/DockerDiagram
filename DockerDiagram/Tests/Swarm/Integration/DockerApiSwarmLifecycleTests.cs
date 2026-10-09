using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiSwarmLifecycleTests
{
    [Fact]
    public async Task StateQuery_MapsDockerInfoResponse()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()));
        using var service = CreateService(server);

        SwarmClusterState state = await service.GetSwarmStateAsync();

        Assert.Equal(SwarmMembershipState.Manager, state.Membership);
        Assert.Equal("manager-1", state.NodeId);
        Assert.Single(server.Requests);
        Assert.Equal(("GET", "/info"), RequestIdentity(server.Requests[0]));
    }

    [Fact]
    public async Task Initialize_RequiresInactiveStateAndPostsValidatedOptions()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(InactiveInfo()),
            ScriptedDockerApiServer.Json("\"manager-new\""));
        using var service = CreateService(server);

        string nodeId = await service.InitializeSwarmAsync(new SwarmInitializeOptions
        {
            ListenAddress = " 0.0.0.0:2377 ",
            AdvertiseAddress = " 10.0.0.10:2377 ",
            DataPathAddress = " 10.0.0.10 ",
            DataPathPort = 4789,
            AutoLockManagers = true,
            Availability = " Active "
        });

        Assert.Equal("manager-new", nodeId);
        Assert.Equal(("GET", "/info"), RequestIdentity(server.Requests[0]));
        Assert.Equal(("POST", "/swarm/init"), RequestIdentity(server.Requests[1]));
        JObject body = JObject.Parse(server.Requests[1].Body);
        Assert.Equal("10.0.0.10:2377", body.Value<string>("AdvertiseAddr"));
        Assert.Equal(4789u, body.Value<uint>("DataPathPort"));
        Assert.True(body.Value<bool>("AutoLockManagers"));
    }

    [Fact]
    public async Task Join_NormalizesManagersAndDoesNotExposeTokenOutsideRequestBody()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(InactiveInfo()),
            ScriptedDockerApiServer.Empty());
        using var service = CreateService(server);

        await service.JoinSwarmAsync(new SwarmJoinOptions
        {
            RemoteManagerAddresses = new[] { " 10.0.0.2:2377 ", "10.0.0.2:2377" },
            JoinToken = " SWMTKN-secret ",
            ListenAddress = "0.0.0.0:2377",
            AdvertiseAddress = "10.0.0.3:2377"
        });

        Assert.Equal(("POST", "/swarm/join"), RequestIdentity(server.Requests[1]));
        JObject body = JObject.Parse(server.Requests[1].Body);
        Assert.Equal(new[] { "10.0.0.2:2377" }, body["RemoteAddrs"]!.Values<string>());
        Assert.Equal("SWMTKN-secret", body.Value<string>("JoinToken"));
        Assert.DoesNotContain("SWMTKN-secret", service.CurrentProfile.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagerTokenQuery_RequiresManagerAndMapsBothTokens()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json("""
                {
                  "JoinTokens": {
                    "Worker": "worker-token",
                    "Manager": "manager-token"
                  }
                }
                """));
        using var service = CreateService(server);

        SwarmJoinTokens tokens = await service.GetJoinTokensAsync();

        Assert.Equal("worker-token", tokens.WorkerToken);
        Assert.Equal("manager-token", tokens.ManagerToken);
        Assert.Equal("Swarm join tokens (redacted)", tokens.ToString());
        Assert.Equal(("GET", "/swarm"), RequestIdentity(server.Requests[1]));
    }

    [Fact]
    public async Task WorkerLeave_UsesNonForcedDockerRequest()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(WorkerInfo()),
            ScriptedDockerApiServer.Empty());
        using var service = CreateService(server);

        await service.LeaveSwarmAsync(force: false);

        ScriptedDockerApiServer.Request request = server.Requests[1];
        Assert.Equal("POST", request.Method);
        Assert.Equal("/swarm/leave", request.Target);
        Assert.DoesNotContain("force=true", request.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreCanceledInitialize_DoesNotReachDocker()
    {
        await using var server = new ScriptedDockerApiServer();
        using var service = CreateService(server);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.InitializeSwarmAsync(
                new SwarmInitializeOptions
                {
                    AdvertiseAddress = "10.0.0.10:2377"
                },
                cancellation.Token));

        Assert.Empty(server.Requests);
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

    private static string InactiveInfo() => """
        {
          "Swarm": {
            "LocalNodeState": "inactive",
            "ControlAvailable": false,
            "NodeID": "",
            "NodeAddr": "",
            "Error": ""
          }
        }
        """;

    private static string ManagerInfo() => """
        {
          "Swarm": {
            "LocalNodeState": "active",
            "ControlAvailable": true,
            "NodeID": "manager-1",
            "NodeAddr": "10.0.0.1",
            "Error": ""
          }
        }
        """;

    private static string WorkerInfo() => """
        {
          "Swarm": {
            "LocalNodeState": "active",
            "ControlAvailable": false,
            "NodeID": "worker-1",
            "NodeAddr": "10.0.0.3",
            "Error": ""
          }
        }
        """;
}
