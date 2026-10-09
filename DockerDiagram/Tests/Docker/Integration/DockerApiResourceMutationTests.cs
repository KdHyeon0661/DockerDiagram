using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class DockerApiResourceMutationTests
{
    [Fact]
    public async Task VolumeCreateAndRemove_ForwardIdentityDriverLabelsAndForce()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("{\"Name\":\"physical-data\",\"Driver\":\"local\"}", 201),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        await service.CreateVolumeAsync(new VolumeCreateOptions
        {
            Name = "diagram-data",
            DockerVolumeName = "physical-data",
            Driver = "local",
            Labels = new Dictionary<string, string> { ["owner"] = "diagram" },
            DriverOptions = new Dictionary<string, string> { ["type"] = "none" }
        });
        await service.RemoveVolumeAsync("physical-data", force: true);

        Assert.Equal(("POST", "/volumes/create"), RequestIdentity(server.Requests[0]));
        JObject create = JObject.Parse(server.Requests[0].Body);
        Assert.Equal("physical-data", create.Value<string>("Name"));
        Assert.Equal("local", create.Value<string>("Driver"));
        Assert.Equal("diagram", create["Labels"]!.Value<string>("owner"));
        Assert.Equal("none", create["DriverOpts"]!.Value<string>("type"));
        Assert.Equal("DELETE", server.Requests[1].Method);
        Assert.StartsWith("/volumes/physical-data", server.Requests[1].Target, StringComparison.Ordinal);
        Assert.Contains("force=true", server.Requests[1].Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkLifecycle_PreservesIpamAndEndpointOptions()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("{\"Id\":\"network-1\",\"Warning\":\"\"}", 201),
            ScriptedDockerApiServer.Empty(),
            ScriptedDockerApiServer.Empty(),
            ScriptedDockerApiServer.Empty(204));
        using var service = CreateService(server);

        string networkId = await service.CreateNetworkAsync(new NetworkCreateOptions
        {
            Name = "backend",
            Driver = "bridge",
            Internal = true,
            Attachable = true,
            Subnet = "10.20.0.0/16",
            Gateway = "10.20.0.1",
            Labels = new Dictionary<string, string> { ["owner"] = "diagram" }
        });
        await service.ConnectNetworkAsync(networkId, "container-1", new ContainerNetworkOptions
        {
            StaticIPv4 = "10.20.0.10",
            Aliases = new List<string> { " api ", string.Empty }
        });
        await service.DisconnectNetworkAsync(networkId, "container-1");
        await service.RemoveNetworkAsync(networkId);

        Assert.Equal("network-1", networkId);
        JObject create = JObject.Parse(server.Requests[0].Body);
        Assert.Equal("backend", create.Value<string>("Name"));
        Assert.True(create.Value<bool>("Internal"));
        Assert.Equal("10.20.0.0/16", create["IPAM"]!["Config"]![0]!.Value<string>("Subnet"));

        JObject connect = JObject.Parse(server.Requests[1].Body);
        Assert.Equal("container-1", connect.Value<string>("Container"));
        Assert.Equal("10.20.0.10", connect["EndpointConfig"]!["IPAMConfig"]!.Value<string>("IPv4Address"));
        Assert.Equal(new[] { "api" }, connect["EndpointConfig"]!["Aliases"]!.Values<string>());

        JObject disconnect = JObject.Parse(server.Requests[2].Body);
        Assert.True(disconnect.Value<bool>("Force"));
        Assert.Equal("DELETE", server.Requests[3].Method);
        Assert.Equal("/networks/network-1", server.Requests[3].Target);
    }

    [Fact]
    public async Task OverlayNetworkCreate_ForwardsSwarmNetworkOptions()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json("{\"Id\":\"overlay-1\",\"Warning\":\"\"}", 201));
        using var service = CreateService(server);

        string networkId = await service.CreateNetworkAsync(new NetworkCreateOptions
        {
            Name = "app-mesh",
            Driver = "overlay",
            Internal = true,
            Attachable = false,
            EnableIPv6 = true,
            Subnet = "10.40.0.0/16",
            Gateway = "10.40.0.1",
            IpRange = "10.40.8.0/21",
            Labels = new Dictionary<string, string> { ["owner"] = "swarm-diagram" },
            DriverOptions = new Dictionary<string, string> { ["encrypted"] = "" },
            AuxAddresses = new Dictionary<string, string> { ["gateway-backup"] = "10.40.0.2" }
        });

        Assert.Equal("overlay-1", networkId);
        JObject create = JObject.Parse(Assert.Single(server.Requests).Body);
        Assert.Equal("app-mesh", create.Value<string>("Name"));
        Assert.Equal("overlay", create.Value<string>("Driver"));
        Assert.True(create.Value<bool>("Internal"));
        Assert.False(create.Value<bool>("Attachable"));
        Assert.True(create.Value<bool>("EnableIPv6"));
        Assert.Equal("swarm-diagram", create["Labels"]!.Value<string>("owner"));
        Assert.Equal(string.Empty, create["Options"]!.Value<string>("encrypted"));
        Assert.Equal("10.40.0.0/16", create["IPAM"]!["Config"]![0]!.Value<string>("Subnet"));
        Assert.Equal("10.40.0.1", create["IPAM"]!["Config"]![0]!.Value<string>("Gateway"));
        Assert.Equal("10.40.8.0/21", create["IPAM"]!["Config"]![0]!.Value<string>("IPRange"));
        Assert.Equal("10.40.0.2", create["IPAM"]!["Config"]![0]!["AuxiliaryAddresses"]!.Value<string>("gateway-backup"));
    }

    [Fact]
    public async Task ImageTagAndDelete_ForwardRepositoryTagAndForce()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Empty(201),
            ScriptedDockerApiServer.Json("[]"));
        using var service = CreateService(server);

        await service.TagImageAsync("sha256:source", "example/api", "2", force: true);
        await service.DeleteImageAsync("sha256:source", force: true);

        Assert.Equal("POST", server.Requests[0].Method);
        Assert.StartsWith("/images/sha256:source/tag", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repo=example%2Fapi", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tag=2", server.Requests[0].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("DELETE", server.Requests[1].Method);
        Assert.StartsWith("/images/sha256:source", server.Requests[1].Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("force=1", server.Requests[1].Target, StringComparison.OrdinalIgnoreCase);
    }

    private static DockerApiService CreateService(ScriptedDockerApiServer server) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = RuntimeKind.DockerEngine,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });

    private static (string Method, string Target) RequestIdentity(ScriptedDockerApiServer.Request request) =>
        (request.Method, request.Target);
}
