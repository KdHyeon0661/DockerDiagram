using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiSwarmResourceQueryTests
{
    [Fact]
    public async Task Services_MapModeReplicasPortsAndStackIdentity()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "ID":"service-1",
                    "Spec":{
                      "Name":"demo_web",
                      "Labels":{"com.docker.stack.namespace":"demo"},
                      "TaskTemplate":{"ContainerSpec":{"Image":"nginx:1.27@sha256:abc"}},
                      "Mode":{"Replicated":{"Replicas":3}}
                    },
                    "ServiceStatus":{"RunningTasks":2,"DesiredTasks":3},
                    "Endpoint":{"Spec":{"Ports":[{"Protocol":"tcp","TargetPort":80,"PublishedPort":8080,"PublishMode":"ingress"}]}}
                  },
                  {
                    "ID":"service-2",
                    "Spec":{
                      "Name":"agent",
                      "TaskTemplate":{"ContainerSpec":{"Image":"example/agent:2"}},
                      "Mode":{"Global":{}}
                    },
                    "ServiceStatus":{"RunningTasks":2,"DesiredTasks":2}
                  }
                ]
                """));
        using var service = CreateService(server);

        List<DockerContainer> services = await service.GetSwarmServicesAsync();

        DockerContainer web = Assert.Single(services, item => item.Id == "service-1");
        Assert.True(web.IsSwarmService);
        Assert.Equal("nginx:1.27", web.Image);
        Assert.Equal("replicated", web.SwarmMode);
        Assert.Equal(3ul, web.SwarmDesiredReplicas);
        Assert.Equal(2ul, web.SwarmRunningReplicas);
        Assert.Equal("demo", web.ComposeProjectName);
        Assert.Equal("Swarm Stack", web.ProjectSource);
        Assert.Contains("8080", web.Ports, StringComparison.Ordinal);

        DockerContainer global = Assert.Single(services, item => item.Id == "service-2");
        Assert.Equal("global", global.SwarmMode);
        Assert.Equal("Swarm", global.ProjectSource);
        Assert.Equal(("GET", "/services"), RequestIdentity(Assert.Single(server.Requests)));
    }

    [Fact]
    public async Task Nodes_MapManagerFirstAndPreserveRuntimeFacts()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "ID":"worker-1",
                    "Spec":{"Role":"worker","Availability":"drain"},
                    "Description":{"Hostname":"worker-a","Engine":{"EngineVersion":"27.5.1"}},
                    "Status":{"State":"down","Addr":"10.0.0.3"}
                  },
                  {
                    "ID":"manager-1",
                    "Spec":{"Role":"manager","Availability":"active"},
                    "Description":{"Hostname":"manager-a","Engine":{"EngineVersion":"27.5.1"}},
                    "Status":{"State":"ready","Addr":"10.0.0.2"},
                    "ManagerStatus":{"Leader":true,"Reachability":"reachable"}
                  }
                ]
                """));
        using var service = CreateService(server);

        List<DockerSwarmNode> nodes = await service.GetSwarmNodesAsync();

        Assert.Equal(new[] { "manager-1", "worker-1" }, nodes.Select(node => node.Id));
        Assert.Equal("leader", nodes[0].ManagerStatus);
        Assert.Equal("#28a745", nodes[0].StateColor);
        Assert.Equal("drain", nodes[1].Availability);
        Assert.Equal("#dc3545", nodes[1].StateColor);
        Assert.Equal(("GET", "/nodes"), RequestIdentity(Assert.Single(server.Requests)));
    }

    [Fact]
    public async Task InspectAndRemoveService_UseEscapedServiceIdentity()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                {"ID":"service/name","Version":{"Index":4},"Spec":{"Name":"api"}}
                """),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        var raw = Assert.IsType<JObject>(await service.InspectSwarmServiceRawAsync("service/name"));
        await service.RemoveSwarmServiceAsync("service/name");

        Assert.Equal(4ul, raw["Version"]!["Index"]!.Value<ulong>());
        Assert.Equal("GET", server.Requests[0].Method);
        Assert.Contains("/services/service%2Fname", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("DELETE", server.Requests[1].Method);
        Assert.Contains("/services/service%2Fname", server.Requests[1].Target, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SwarmDataResourceKind.Secret, "/secrets")]
    [InlineData(SwarmDataResourceKind.Config, "/configs")]
    public async Task DataResources_MapIdentityVersionLabelsAndSortByName(
        SwarmDataResourceKind kind,
        string expectedTarget)
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("""
                [
                  {
                    "ID":"resource-z",
                    "Version":{"Index":8},
                    "UpdatedAt":"2026-09-20T03:04:05Z",
                    "Spec":{"Name":"zeta","Labels":{"owner":"diagram"}}
                  },
                  {
                    "ID":"resource-a",
                    "Version":{"Index":2},
                    "Spec":{"Name":"alpha","Labels":{}}
                  },
                  {"Version":{"Index":99},"Spec":{"Name":"invalid-without-id"}}
                ]
                """));
        using var service = CreateService(server);

        IReadOnlyList<SwarmDataResourceSnapshot> resources =
            await service.GetSwarmDataResourcesAsync(kind);

        Assert.Equal(new[] { "alpha", "zeta" }, resources.Select(resource => resource.Name));
        Assert.Equal(kind, resources[0].Kind);
        Assert.Equal(8ul, resources[1].Version);
        Assert.Equal("diagram", resources[1].Labels["owner"]);
        Assert.NotNull(resources[1].UpdatedAt);
        Assert.Equal(("GET", expectedTarget), RequestIdentity(Assert.Single(server.Requests)));
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
