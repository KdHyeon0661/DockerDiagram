using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceSpecMapperTests
{
    [Fact]
    public void BuildForCreate_MapsReplicatedServiceSpec()
    {
        SwarmServiceSpecOptions options = CreateSpec();

        JObject result = SwarmServiceSpecMapper.BuildForCreate(options);

        Assert.Equal("web-api", result["Name"]?.Value<string>());
        Assert.Equal("example/web:1.0", result["TaskTemplate"]?["ContainerSpec"]?["Image"]?.Value<string>());
        Assert.Equal(3UL, result["Mode"]?["Replicated"]?["Replicas"]?.Value<ulong>());
        Assert.Equal("on-failure", result["TaskTemplate"]?["RestartPolicy"]?["Condition"]?.Value<string>());
        Assert.Equal(500_000_000L, result["TaskTemplate"]?["RestartPolicy"]?["Delay"]?.Value<long>());
        Assert.Equal("rollback", result["UpdateConfig"]?["FailureAction"]?.Value<string>());
    }

    [Fact]
    public void BuildForCreate_MapsPortsMountsAndNetworks()
    {
        JObject result = SwarmServiceSpecMapper.BuildForCreate(CreateSpec());

        JObject port = Assert.IsType<JObject>(result["EndpointSpec"]?["Ports"]?.Single());
        Assert.Equal(8080U, port["PublishedPort"]?.Value<uint>());
        Assert.Equal(80U, port["TargetPort"]?.Value<uint>());
        Assert.Equal("host", port["PublishMode"]?.Value<string>());

        JObject mount = Assert.IsType<JObject>(result["TaskTemplate"]?["ContainerSpec"]?["Mounts"]?.Single());
        Assert.Equal("volume", mount["Type"]?.Value<string>());
        Assert.Equal("/data", mount["Target"]?.Value<string>());
        Assert.Equal("local", mount["VolumeOptions"]?["DriverConfig"]?["Name"]?.Value<string>());
        Assert.Equal("nfs", mount["VolumeOptions"]?["DriverConfig"]?["Options"]?["type"]?.Value<string>());
        Assert.Equal("test", mount["VolumeOptions"]?["Labels"]?["purpose"]?.Value<string>());

        JObject network = Assert.IsType<JObject>(result["Networks"]?.Single());
        Assert.Equal("frontend", network["Target"]?.Value<string>());
        Assert.Equal("web", network["Aliases"]?.Single()?.Value<string>());
    }

    [Fact]
    public void BuildForCreate_MapsGlobalModeWithoutReplicas()
    {
        var options = new SwarmServiceSpecOptions
        {
            Name = "node-agent",
            Image = "example/agent:latest",
            Mode = SwarmServiceModeKind.Global,
            Replicas = null
        };

        JObject result = SwarmServiceSpecMapper.BuildForCreate(options);

        Assert.IsType<JObject>(result["Mode"]?["Global"]);
        Assert.Null(result["Mode"]?["Replicated"]);
    }

    [Fact]
    public void BuildForCreate_MapsPlacementAndDataReferences()
    {
        var options = new SwarmServiceCreateOptions
        {
            Spec = CreateSpec(),
            PlacementConstraints = new[] { "node.role==worker" },
            Secrets = new[]
            {
                new SwarmServiceResourceReferenceOptions("secret-id", "db_password", "/run/secrets/db", "0", "0", 256)
            },
            Configs = new[]
            {
                new SwarmServiceResourceReferenceOptions("config-id", "nginx_conf", "/etc/nginx/nginx.conf")
            }
        };

        JObject result = SwarmServiceSpecMapper.BuildForCreate(options);

        Assert.Equal("node.role==worker", result["TaskTemplate"]?["Placement"]?["Constraints"]?.Single()?.Value<string>());
        Assert.Equal("secret-id", result["TaskTemplate"]?["ContainerSpec"]?["Secrets"]?.Single()?["SecretID"]?.Value<string>());
        Assert.Equal(256U, result["TaskTemplate"]?["ContainerSpec"]?["Secrets"]?.Single()?["File"]?["Mode"]?.Value<uint>());
        Assert.Equal("config-id", result["TaskTemplate"]?["ContainerSpec"]?["Configs"]?.Single()?["ConfigID"]?.Value<string>());
    }

    [Fact]
    public void MergeForUpdate_PreservesUnsupportedCurrentFields()
    {
        JObject current = JObject.Parse("""
        {
          "Name": "web-api",
          "TaskTemplate": {
            "ContainerSpec": {
              "Image": "example/web:old",
              "Healthcheck": { "Test": ["CMD", "curl", "-f", "http://localhost"] }
            },
            "Placement": { "Constraints": ["node.role==worker"] },
            "ForceUpdate": 7
          },
          "Mode": { "Replicated": { "Replicas": 1 } },
          "EndpointSpec": { "Mode": "vip", "Ports": [] },
          "CustomFutureField": { "Keep": true }
        }
        """);

        JObject updated = SwarmServiceSpecMapper.MergeForUpdate(current, CreateSpec());

        Assert.Equal("example/web:1.0", updated["TaskTemplate"]?["ContainerSpec"]?["Image"]?.Value<string>());
        Assert.NotNull(updated["TaskTemplate"]?["ContainerSpec"]?["Healthcheck"]);
        Assert.NotNull(updated["TaskTemplate"]?["Placement"]);
        Assert.Equal(7, updated["TaskTemplate"]?["ForceUpdate"]?.Value<int>());
        Assert.Equal("vip", updated["EndpointSpec"]?["Mode"]?.Value<string>());
        Assert.True(updated["CustomFutureField"]?["Keep"]?.Value<bool>());
        Assert.Equal("example/web:old", current["TaskTemplate"]?["ContainerSpec"]?["Image"]?.Value<string>());
    }

    [Fact]
    public void MergeForUpdate_RejectsNameOrModeChanges()
    {
        JObject current = JObject.Parse("""
        { "Name": "existing", "Mode": { "Replicated": { "Replicas": 1 } } }
        """);

        Assert.Throws<NotSupportedException>(() =>
            SwarmServiceSpecMapper.MergeForUpdate(current, CreateSpec()));
    }

    [Fact]
    public void ReadCurrentMode_RejectsJobModes()
    {
        JObject current = JObject.Parse("""{ "Mode": { "ReplicatedJob": {} } }""");

        Assert.Throws<NotSupportedException>(() => SwarmServiceSpecMapper.ReadCurrentMode(current));
    }

    [Fact]
    public void ToNanoseconds_UsesDockerDurationUnitsAndChecksOverflow()
    {
        Assert.Equal(1_250_000_000L, SwarmServiceSpecMapper.ToNanoseconds(TimeSpan.FromMilliseconds(1250)));
        Assert.Throws<OverflowException>(() => SwarmServiceSpecMapper.ToNanoseconds(TimeSpan.MaxValue));
    }

    private static SwarmServiceSpecOptions CreateSpec() => new()
    {
        Name = "web-api",
        Image = "example/web:1.0",
        Replicas = 3,
        Command = new[] { "/app/start" },
        Arguments = new[] { "--listen", "0.0.0.0" },
        EnvironmentVariables = new[] { "APP_ENV=production" },
        Labels = new Dictionary<string, string> { ["com.example.role"] = "api" },
        PublishedPorts = new[]
        {
            new SwarmPublishedPortOptions(80, 8080, SwarmPortProtocol.Tcp, SwarmPublishMode.Host)
        },
        Mounts = new[]
        {
            new SwarmMountOptions(
                SwarmMountKind.Volume,
                "web-data",
                "/data",
                true,
                "local",
                new Dictionary<string, string> { ["type"] = "nfs" },
                new Dictionary<string, string> { ["purpose"] = "test" })
        },
        Networks = new[]
        {
            new SwarmNetworkAttachmentOptions("frontend", new[] { "web" },
                new Dictionary<string, string> { ["encrypted"] = "true" })
        },
        RestartPolicy = new SwarmRestartPolicyOptions
        {
            Condition = SwarmRestartCondition.OnFailure,
            Delay = TimeSpan.FromMilliseconds(500),
            MaxAttempts = 3
        },
        UpdatePolicy = new SwarmUpdatePolicyOptions
        {
            Parallelism = 2,
            Delay = TimeSpan.FromSeconds(1),
            FailureAction = SwarmUpdateFailureAction.Rollback,
            Order = SwarmUpdateOrder.StartFirst,
            MaxFailureRatio = 0.25
        }
    };
}
