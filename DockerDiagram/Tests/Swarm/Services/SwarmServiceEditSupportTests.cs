using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceEditSupportTests
{
    [Fact]
    public void Reader_MapsCompleteInspectResponse()
    {
        JObject inspect = JObject.Parse("""
        {
          "ID": "service-123",
          "Version": { "Index": 17 },
          "Spec": {
            "Name": "web-api",
            "Labels": { "team": "platform" },
            "TaskTemplate": {
              "ContainerSpec": {
                "Image": "example/web:2.0",
                "Command": ["/app/start", "--safe"],
                "Args": ["--listen", "8080"],
                "Env": ["APP_ENV=production"],
                "Mounts": [
                  {
                    "Type": "volume",
                    "Source": "web-data",
                    "Target": "/data",
                    "ReadOnly": true,
                    "VolumeOptions": {
                      "Labels": { "purpose": "database" },
                      "DriverConfig": { "Name": "local", "Options": { "type": "nfs" } }
                    }
                  }
                ]
              },
              "RestartPolicy": {
                "Condition": "on-failure",
                "Delay": 2500000000,
                "MaxAttempts": 4,
                "Window": 30000000000
              }
            },
            "Mode": { "Replicated": { "Replicas": 3 } },
            "UpdateConfig": {
              "Parallelism": 2,
              "Delay": 1000000000,
              "Monitor": 10000000000,
              "MaxFailureRatio": 0.25,
              "FailureAction": "rollback",
              "Order": "start-first"
            },
            "RollbackConfig": {
              "Parallelism": 1,
              "Delay": 0,
              "FailureAction": "continue",
              "Order": "stop-first"
            },
            "Networks": [
              { "Target": "frontend", "Aliases": ["web", "public"], "DriverOpts": { "encrypted": "true" } }
            ],
            "EndpointSpec": {
              "Ports": [
                { "TargetPort": 443, "PublishedPort": 8443, "Protocol": "tcp", "PublishMode": "ingress" }
              ]
            }
          }
        }
        """);

        SwarmServiceEditSnapshot result = SwarmServiceSpecReader.Read(inspect);

        Assert.Equal("service-123", result.ServiceId);
        Assert.Equal(17UL, result.Version);
        Assert.Equal(3UL, result.Spec.Replicas);
        Assert.Equal(new[] { "/app/start", "--safe" }, result.Spec.Command);
        Assert.Equal(TimeSpan.FromSeconds(2.5), result.Spec.RestartPolicy.Delay);
        Assert.Equal(SwarmUpdateFailureAction.Rollback, result.Spec.UpdatePolicy.FailureAction);
        Assert.Equal(SwarmUpdateOrder.StartFirst, result.Spec.UpdatePolicy.Order);
        Assert.Equal("local", result.Spec.Mounts[0].VolumeDriver);
        Assert.Equal("nfs", result.Spec.Mounts[0].VolumeDriverOptions!["type"]);
        Assert.Equal("database", result.Spec.Mounts[0].VolumeLabels!["purpose"]);
        Assert.Equal("true", result.Spec.Networks[0].DriverOptions!["encrypted"]);
        Assert.Equal(8443U, result.Spec.PublishedPorts[0].PublishedPort);
    }

    [Fact]
    public void Reader_GlobalMode_UsesNullReplicasAndDefaults()
    {
        JObject inspect = JObject.Parse("""
        {
          "ID": "agent",
          "Version": { "Index": 2 },
          "Spec": {
            "Name": "agent",
            "TaskTemplate": { "ContainerSpec": { "Image": "example/agent" } },
            "Mode": { "Global": {} }
          }
        }
        """);

        SwarmServiceEditSnapshot result = SwarmServiceSpecReader.Read(inspect);

        Assert.Equal(SwarmServiceModeKind.Global, result.Spec.Mode);
        Assert.Null(result.Spec.Replicas);
        Assert.Equal(1UL, result.Spec.UpdatePolicy.Parallelism);
    }

    [Fact]
    public void Reader_RequiresVersionForOptimisticConcurrency()
    {
        JObject inspect = JObject.Parse("""
        {
          "ID": "web",
          "Spec": {
            "Name": "web",
            "TaskTemplate": { "ContainerSpec": { "Image": "nginx" } },
            "Mode": { "Replicated": { "Replicas": 1 } }
          }
        }
        """);

        Assert.Throws<InvalidOperationException>(() => SwarmServiceSpecReader.Read(inspect));
    }

    [Fact]
    public void FormRoundTrip_PreservesHiddenCommandNetworkAndVolumeOptions()
    {
        var original = new SwarmServiceSpecOptions
        {
            Name = "web",
            Image = "nginx:latest",
            Replicas = 2,
            Command = new[] { "/docker-entrypoint.sh", "nginx" },
            Networks = new[]
            {
                new SwarmNetworkAttachmentOptions(
                    "frontend",
                    new[] { "web" },
                    new Dictionary<string, string> { ["encrypted"] = "true" })
            },
            Mounts = new[]
            {
                new SwarmMountOptions(
                    SwarmMountKind.Volume,
                    "web-data",
                    "/data",
                    false,
                    "local",
                    new Dictionary<string, string> { ["type"] = "nfs" },
                    new Dictionary<string, string> { ["purpose"] = "database" })
            }
        };

        SwarmServiceCreateFormInput form = SwarmServiceEditFormatter.ToFormInput(original);
        SwarmServiceSpecOptions parsed = SwarmServiceCreateInputParser.Parse(form).Spec;
        SwarmServiceSpecOptions preserved = SwarmServiceEditFormatter.PreserveHiddenSettings(original, parsed);

        Assert.Equal(original.Command, preserved.Command);
        Assert.Equal("true", preserved.Networks[0].DriverOptions!["encrypted"]);
        Assert.Equal("local", preserved.Mounts[0].VolumeDriver);
        Assert.Equal("nfs", preserved.Mounts[0].VolumeDriverOptions!["type"]);
        Assert.Equal("database", preserved.Mounts[0].VolumeLabels!["purpose"]);
    }

    [Fact]
    public void FormRoundTrip_UsesEditedCommandWhenExecutableChanges()
    {
        var original = new SwarmServiceSpecOptions
        {
            Name = "web",
            Image = "nginx",
            Command = new[] { "/old", "--hidden" }
        };
        var edited = new SwarmServiceSpecOptions
        {
            Name = "web",
            Image = "nginx",
            Command = new[] { "/new" }
        };

        SwarmServiceSpecOptions result = SwarmServiceEditFormatter.PreserveHiddenSettings(original, edited);

        Assert.Equal(new[] { "/new" }, result.Command);
    }
}
