using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceContractTests
{
    [Fact]
    public void ReplicatedSpec_WithValidOptions_PassesValidation()
    {
        CreateValidSpec().Validate();
    }

    [Fact]
    public void GlobalSpec_RejectsReplicaCount()
    {
        var spec = new SwarmServiceSpecOptions
        {
            Name = "global-agent",
            Image = "example/agent:latest",
            Mode = SwarmServiceModeKind.Global,
            Replicas = 1
        };

        Assert.Throws<ArgumentException>(spec.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("-starts-with-dash")]
    public void Spec_RejectsInvalidServiceName(string name)
    {
        Assert.Throws<ArgumentException>(() => CreateValidSpec(name).Validate());
    }

    [Fact]
    public void Spec_RejectsDuplicatePublishedPort()
    {
        var spec = new SwarmServiceSpecOptions
        {
            Name = "web-api",
            Image = "example/web-api:1.0",
            PublishedPorts = new[]
            {
                new SwarmPublishedPortOptions(80, 8080),
                new SwarmPublishedPortOptions(8080, 8080)
            }
        };

        Assert.Throws<ArgumentException>(spec.Validate);
    }

    [Fact]
    public void UpdateOptions_RequireServiceIdAndVersion()
    {
        var options = new SwarmServiceUpdateOptions { Spec = CreateValidSpec() };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void MutationResult_NormalizesIdAndUsesEmptyWarnings()
    {
        SwarmServiceMutationResult result = SwarmServiceMutationResult.Create(" service-id ");

        Assert.Equal("service-id", result.ServiceId);
        Assert.Empty(result.Warnings);
    }

    private static SwarmServiceSpecOptions CreateValidSpec(string name = "web-api") => new()
    {
        Name = name,
        Image = "example/web-api:1.0",
        Mode = SwarmServiceModeKind.Replicated,
        Replicas = 2,
        EnvironmentVariables = new[] { "ASPNETCORE_ENVIRONMENT=Production" },
        PublishedPorts = new[] { new SwarmPublishedPortOptions(8080, 8080) },
        Networks = new[] { new SwarmNetworkAttachmentOptions("frontend") },
        Mounts = new[] { new SwarmMountOptions(SwarmMountKind.Volume, "web-data", "/data") }
    };
}
