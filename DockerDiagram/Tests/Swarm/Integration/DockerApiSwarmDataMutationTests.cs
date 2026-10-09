using System.Text;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiSwarmDataMutationTests
{
    [Theory]
    [InlineData(SwarmDataResourceKind.Secret, "/secrets/create")]
    [InlineData(SwarmDataResourceKind.Config, "/configs/create")]
    public async Task CreateDataResource_EncodesPayloadAndReturnsEngineIdentity(
        SwarmDataResourceKind kind,
        string expectedTarget)
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json("""{"ID":"resource-1"}""", 201));
        using var service = CreateService(server);

        SwarmDataResourceMutationResult result = await service.CreateSwarmDataResourceAsync(
            new SwarmDataResourceCreateOptions
            {
                Kind = kind,
                Name = " app-settings ",
                Data = "한글=value",
                Labels = new Dictionary<string, string> { ["owner"] = "diagram" }
            });

        Assert.Equal("resource-1", result.ResourceId);
        Assert.Equal(kind, result.Kind);
        Assert.Equal("app-settings", result.Name);
        Assert.Equal(("POST", expectedTarget), RequestIdentity(server.Requests[1]));
        JObject body = JObject.Parse(server.Requests[1].Body);
        Assert.Equal("app-settings", body.Value<string>("Name"));
        Assert.Equal("diagram", body["Labels"]!.Value<string>("owner"));
        Assert.Equal("한글=value", Encoding.UTF8.GetString(Convert.FromBase64String(body.Value<string>("Data")!)));
    }

    [Theory]
    [InlineData(SwarmDataResourceKind.Secret, "/secrets/secret%2Fone")]
    [InlineData(SwarmDataResourceKind.Config, "/configs/secret%2Fone")]
    public async Task RemoveDataResource_UsesKindAndEscapedIdentity(
        SwarmDataResourceKind kind,
        string expectedTarget)
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        await service.RemoveSwarmDataResourceAsync(kind, "secret/one");

        Assert.Equal(("DELETE", expectedTarget), RequestIdentity(server.Requests[1]));
    }

    [Fact]
    public async Task UpdateTopology_ReplacesRequestedReferencesAndUsesServiceVersion()
    {
        string current = Service(6, """
            "Secrets":[{"SecretID":"old-secret","SecretName":"old","File":{"Name":"old.txt"}}],
            "Configs":[{"ConfigID":"old-config","ConfigName":"old","File":{"Name":"old.conf"}}]
            """);
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(current),
            ScriptedDockerApiServer.Json("""{"Warnings":[]}"""),
            ScriptedDockerApiServer.Json(Service(7, string.Empty)));
        using var service = CreateService(server);

        SwarmServiceMutationResult result = await service.UpdateSwarmServiceTopologyAsync(
            new SwarmServiceTopologyUpdateOptions
            {
                Service = new SwarmServiceUpdateOptions
                {
                    ServiceId = "service-1",
                    Version = 6,
                    Spec = Spec()
                },
                ApplySecrets = true,
                Secrets = new[]
                {
                    new SwarmServiceResourceReferenceOptions(
                        "secret-1", "db-password", "run/secrets/db-password", "1000", "1000", 256)
                },
                ApplyConfigs = true,
                Configs = new[]
                {
                    new SwarmServiceResourceReferenceOptions(
                        "config-1", "app-config", "etc/app/config.json")
                }
            });

        Assert.Equal(7ul, result.Version);
        Assert.Equal("/services/service-1/update?version=6", server.Requests[2].Target);
        JObject body = JObject.Parse(server.Requests[2].Body);
        JObject secret = Assert.IsType<JObject>(body["TaskTemplate"]!["ContainerSpec"]!["Secrets"]!.Single());
        Assert.Equal("secret-1", secret.Value<string>("SecretID"));
        Assert.Equal("run/secrets/db-password", secret["File"]!.Value<string>("Name"));
        Assert.Equal(256u, secret["File"]!.Value<uint>("Mode"));
        JObject config = Assert.IsType<JObject>(body["TaskTemplate"]!["ContainerSpec"]!["Configs"]!.Single());
        Assert.Equal("config-1", config.Value<string>("ConfigID"));
    }

    [Fact]
    public async Task PlacementLoadAndUpdate_MapAndReplaceConstraints()
    {
        string current = Service(11, string.Empty, "node.role==worker", "node.labels.zone==seoul");
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(current),
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Json(current),
            ScriptedDockerApiServer.Json("{}"),
            ScriptedDockerApiServer.Json(Service(12, string.Empty, "node.labels.storage==ssd")));
        using var service = CreateService(server);

        SwarmServicePlacementSnapshot loaded = await service.LoadSwarmServicePlacementAsync("service-1");
        SwarmServiceMutationResult updated = await service.UpdateSwarmServicePlacementAsync(
            new SwarmServicePlacementUpdateOptions
            {
                ServiceId = "service-1",
                Version = 11,
                Constraints = new[] { " node.labels.storage==ssd " }
            });

        Assert.Equal(new[] { "node.role==worker", "node.labels.zone==seoul" }, loaded.Constraints);
        Assert.Equal(12ul, updated.Version);
        Assert.Equal("/services/service-1/update?version=11", server.Requests[4].Target);
        JObject body = JObject.Parse(server.Requests[4].Body);
        Assert.Equal(
            new[] { "node.labels.storage==ssd" },
            body["TaskTemplate"]!["Placement"]!["Constraints"]!.Values<string>());
        Assert.Equal("example/api:2", body["TaskTemplate"]!["ContainerSpec"]!.Value<string>("Image"));
    }

    private static SwarmServiceSpecOptions Spec() => new()
    {
        Name = "api",
        Image = "example/api:2",
        Replicas = 2
    };

    private static string Service(ulong version, string referenceMembers, params string[] constraints)
    {
        var service = new JObject
        {
            ["ID"] = "service-1",
            ["Version"] = new JObject { ["Index"] = version },
            ["Spec"] = new JObject
            {
                ["Name"] = "api",
                ["TaskTemplate"] = new JObject
                {
                    ["ContainerSpec"] = new JObject
                    {
                        ["Image"] = "example/api:2"
                    },
                    ["Placement"] = new JObject
                    {
                        ["Constraints"] = new JArray(constraints)
                    }
                },
                ["Mode"] = new JObject
                {
                    ["Replicated"] = new JObject { ["Replicas"] = 2 }
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(referenceMembers))
        {
            JObject members = JObject.Parse("{" + referenceMembers + "}");
            JObject container = (JObject)service["Spec"]!["TaskTemplate"]!["ContainerSpec"]!;
            foreach (JProperty property in members.Properties())
                container[property.Name] = property.Value;
        }
        return service.ToString(Newtonsoft.Json.Formatting.None);
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
