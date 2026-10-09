using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiSwarmServiceMutationTests
{
    [Fact]
    public async Task CreateService_RequiresManagerPostsSpecAndReadsCreatedVersion()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json("""{"ID":"service-1","Warnings":["registry digest unresolved"]}""", 201),
            ScriptedDockerApiServer.Json(Service("service-1", 2, "api", "example/api:2", 3)));
        using var service = CreateService(server);

        SwarmServiceMutationResult result = await service.CreateSwarmServiceAsync(
            new SwarmServiceCreateOptions { Spec = Spec("api", "example/api:2", 3) });

        Assert.Equal("service-1", result.ServiceId);
        Assert.Equal(2ul, result.Version);
        Assert.Equal(new[] { "registry digest unresolved" }, result.Warnings);
        Assert.Equal(
            new[]
            {
                ("GET", "/info"),
                ("POST", "/services/create"),
                ("GET", "/services/service-1")
            },
            server.Requests.Select(RequestIdentity));
        JObject body = JObject.Parse(server.Requests[1].Body);
        Assert.Equal("api", body.Value<string>("Name"));
        Assert.Equal("example/api:2", body["TaskTemplate"]!["ContainerSpec"]!.Value<string>("Image"));
        Assert.Equal(3ul, body["Mode"]!["Replicated"]!.Value<ulong>("Replicas"));
    }

    [Fact]
    public async Task UpdateService_UsesOptimisticVersionAndPreservesUnsupportedFields()
    {
        string current = """
            {
              "ID":"service-1",
              "Version":{"Index":7},
              "Spec":{
                "Name":"api",
                "TaskTemplate":{
                  "ContainerSpec":{"Image":"example/api:1","Healthcheck":{"Test":["CMD","true"]}},
                  "Placement":{"Constraints":["node.role==worker"]},
                  "ForceUpdate":9
                },
                "Mode":{"Replicated":{"Replicas":2}},
                "EndpointSpec":{"Mode":"vip","Ports":[]},
                "FutureField":{"Keep":true}
              }
            }
            """;
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(current),
            ScriptedDockerApiServer.Json("{}"),
            ScriptedDockerApiServer.Json(Service("service-1", 8, "api", "example/api:2", 4)));
        using var service = CreateService(server);

        SwarmServiceMutationResult result = await service.UpdateSwarmServiceAsync(
            new SwarmServiceUpdateOptions
            {
                ServiceId = "service-1",
                Version = 7,
                Spec = Spec("api", "example/api:2", 4)
            });

        Assert.Equal(8ul, result.Version);
        Assert.Equal("/services/service-1/update?version=7", server.Requests[2].Target);
        JObject body = JObject.Parse(server.Requests[2].Body);
        Assert.Equal("example/api:2", body["TaskTemplate"]!["ContainerSpec"]!.Value<string>("Image"));
        Assert.NotNull(body["TaskTemplate"]!["ContainerSpec"]!["Healthcheck"]);
        Assert.NotNull(body["TaskTemplate"]!["Placement"]);
        Assert.Equal(9, body["TaskTemplate"]!.Value<int>("ForceUpdate"));
        Assert.True(body["FutureField"]!.Value<bool>("Keep"));
    }

    [Fact]
    public async Task UpdateService_ForwardsAutomaticHostUdpPublishedPort()
    {
        string current = """
            {
              "ID":"service-1",
              "Version":{"Index":7},
              "Spec":{
                "Name":"dns",
                "TaskTemplate":{"ContainerSpec":{"Image":"example/dns:1"}},
                "Mode":{"Replicated":{"Replicas":1}},
                "EndpointSpec":{"Ports":[]}
              }
            }
            """;
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(current),
            ScriptedDockerApiServer.Json("{}"),
            ScriptedDockerApiServer.Json(Service("service-1", 8, "dns", "example/dns:1", 1)));
        using var service = CreateService(server);

        await service.UpdateSwarmServiceAsync(new SwarmServiceUpdateOptions
        {
            ServiceId = "service-1",
            Version = 7,
            Spec = new SwarmServiceSpecOptions
            {
                Name = "dns",
                Image = "example/dns:1",
                Replicas = 1,
                PublishedPorts = new[]
                {
                    new SwarmPublishedPortOptions(
                        53,
                        null,
                        SwarmPortProtocol.Udp,
                        SwarmPublishMode.Host)
                }
            }
        });

        JObject body = JObject.Parse(server.Requests[2].Body);
        JObject port = Assert.IsType<JObject>(Assert.Single(body["EndpointSpec"]!["Ports"]!));
        Assert.Equal(53U, port.Value<uint>("TargetPort"));
        Assert.Equal("udp", port.Value<string>("Protocol"));
        Assert.Equal("host", port.Value<string>("PublishMode"));
        Assert.Null(port["PublishedPort"]);
    }

    [Fact]
    public async Task UpdateService_RejectsStaleVersionBeforePostingMutation()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(Service("service-1", 9, "api", "example/api:1", 2)));
        using var service = CreateService(server);

        SwarmServiceVersionConflictException error = await Assert.ThrowsAsync<SwarmServiceVersionConflictException>(
            () => service.UpdateSwarmServiceAsync(new SwarmServiceUpdateOptions
            {
                ServiceId = "service-1",
                Version = 7,
                Spec = Spec("api", "example/api:2", 2)
            }));

        Assert.Equal(7ul, error.ExpectedVersion);
        Assert.Equal(9ul, error.ActualVersion);
        Assert.Equal(2, server.Requests.Count);
        Assert.DoesNotContain(server.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task CreateService_RejectsWorkerConnectionBeforeMutation()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(WorkerInfo()));
        using var service = CreateService(server);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateSwarmServiceAsync(
                new SwarmServiceCreateOptions { Spec = Spec("api", "example/api:2", 1) }));

        Assert.Contains("Worker", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(server.Requests);
    }

    private static SwarmServiceSpecOptions Spec(string name, string image, ulong replicas) => new()
    {
        Name = name,
        Image = image,
        Replicas = replicas,
        EnvironmentVariables = new[] { "MODE=production" },
        Labels = new Dictionary<string, string> { ["owner"] = "diagram" }
    };

    private static string Service(string id, ulong version, string name, string image, ulong replicas) => $$$"""
        {
          "ID":"{{{id}}}",
          "Version":{"Index":{{{version}}}},
          "Spec":{
            "Name":"{{{name}}}",
            "TaskTemplate":{"ContainerSpec":{"Image":"{{{image}}}"}},
            "Mode":{"Replicated":{"Replicas":{{{replicas}}}}}
          }
        }
        """;

    private static string ManagerInfo() => """
        {"Swarm":{"LocalNodeState":"active","ControlAvailable":true,"NodeID":"manager-1","NodeAddr":"10.0.0.1","Error":""}}
        """;

    private static string WorkerInfo() => """
        {"Swarm":{"LocalNodeState":"active","ControlAvailable":false,"NodeID":"worker-1","NodeAddr":"10.0.0.2","Error":""}}
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
