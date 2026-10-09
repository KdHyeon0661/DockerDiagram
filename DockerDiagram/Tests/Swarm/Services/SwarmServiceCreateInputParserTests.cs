using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceCreateInputParserTests
{
    [Fact]
    public void Parse_FullReplicatedForm_MapsEverySection()
    {
        SwarmServiceCreateOptions result = SwarmServiceCreateInputParser.Parse(new SwarmServiceCreateFormInput
        {
            Name = "web-api",
            Image = "example/web:1.2",
            Mode = "replicated",
            Replicas = "3",
            Command = "/app/start",
            Arguments = "--listen\n8080",
            EnvironmentVariables = "APP_ENV=production\nFEATURE_FLAG=1",
            Labels = "team=platform\npublic=true",
            PublishedPorts = "8443:443/tcp@ingress\n53:53/udp@host",
            Networks = "frontend|web,public\nbackend",
            Mounts = "volume|web-data|/data|rw\ntmpfs||/tmp|ro",
            RestartCondition = "on-failure",
            RestartDelaySeconds = "2.5",
            RestartMaxAttempts = "4",
            RestartWindowSeconds = "30",
            UpdateParallelism = "2",
            UpdateDelaySeconds = "1",
            UpdateMonitorSeconds = "10",
            UpdateMaxFailureRatio = "0.25",
            UpdateFailureAction = "rollback",
            UpdateOrder = "start-first",
            RollbackParallelism = "1",
            RollbackDelaySeconds = "0",
            RollbackMonitorSeconds = "5",
            RollbackMaxFailureRatio = "0.1",
            RollbackFailureAction = "continue",
            RollbackOrder = "stop-first"
        });

        Assert.Equal("web-api", result.Spec.Name);
        Assert.Equal(3UL, result.Spec.Replicas);
        Assert.Equal(new[] { "/app/start" }, result.Spec.Command);
        Assert.Equal(new[] { "--listen", "8080" }, result.Spec.Arguments);
        Assert.Equal("platform", result.Spec.Labels["team"]);
        Assert.Equal(SwarmPortProtocol.Udp, result.Spec.PublishedPorts[1].Protocol);
        Assert.Equal(SwarmPublishMode.Host, result.Spec.PublishedPorts[1].PublishMode);
        Assert.Equal(new[] { "web", "public" }, result.Spec.Networks[0].Aliases);
        Assert.True(result.Spec.Mounts[1].ReadOnly);
        Assert.Equal(TimeSpan.FromSeconds(2.5), result.Spec.RestartPolicy.Delay);
        Assert.Equal(SwarmUpdateFailureAction.Rollback, result.Spec.UpdatePolicy.FailureAction);
        Assert.Equal(SwarmUpdateOrder.StartFirst, result.Spec.UpdatePolicy.Order);
    }

    [Fact]
    public void Parse_GlobalMode_DiscardsReplicaInput()
    {
        SwarmServiceCreateOptions result = SwarmServiceCreateInputParser.Parse(ValidInput(mode: "global"));

        Assert.Equal(SwarmServiceModeKind.Global, result.Spec.Mode);
        Assert.Null(result.Spec.Replicas);
    }

    [Theory]
    [InlineData("0:80/tcp")]
    [InlineData("8080:70000/tcp")]
    [InlineData("8080:80/http")]
    [InlineData("8080:80/tcp@mesh")]
    public void Parse_InvalidPort_Throws(string value) =>
        Assert.Throws<ArgumentException>(() => SwarmServiceCreateInputParser.Parse(ValidInput(publishedPorts: value)));

    [Theory]
    [InlineData("bind||/data|rw")]
    [InlineData("volume|data|/data|invalid")]
    [InlineData("volume|data")]
    public void Parse_InvalidMount_Throws(string value) =>
        Assert.Throws<ArgumentException>(() => SwarmServiceCreateInputParser.Parse(ValidInput(mounts: value)));

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("not-a-number")]
    public void Parse_InvalidFailureRatio_Throws(string value) =>
        Assert.Throws<ArgumentException>(() => SwarmServiceCreateInputParser.Parse(ValidInput(updateRatio: value)));

    [Fact]
    public void Parse_DuplicateLabel_Throws() =>
        Assert.Throws<ArgumentException>(() => SwarmServiceCreateInputParser.Parse(ValidInput(labels: "team=one\nteam=two")));

    private static SwarmServiceCreateFormInput ValidInput(
        string mode = "replicated",
        string publishedPorts = "",
        string mounts = "",
        string updateRatio = "",
        string labels = "") => new()
        {
            Name = "web",
            Image = "nginx:latest",
            Mode = mode,
            Replicas = "2",
            PublishedPorts = publishedPorts,
            Mounts = mounts,
            UpdateMaxFailureRatio = updateRatio,
            Labels = labels
        };
}
