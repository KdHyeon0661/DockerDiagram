using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmLifecycleValidationTests
{
    [Fact]
    public void InitializeOptions_RejectInvalidDataPathPort()
    {
        var options = new SwarmInitializeOptions
        {
            AdvertiseAddress = "10.0.0.10:2377",
            DataPathPort = 80
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void JoinOptions_NormalizesAndDeduplicatesManagerAddresses()
    {
        var options = new SwarmJoinOptions
        {
            RemoteManagerAddresses = new[] { " 10.0.0.1:2377 ", "10.0.0.1:2377", "10.0.0.2:2377" },
            JoinToken = "secret"
        };

        options.Validate();

        Assert.Equal(new[] { "10.0.0.1:2377", "10.0.0.2:2377" }, options.GetNormalizedManagerAddresses());
        Assert.DoesNotContain("secret", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JoinTokens_DoNotExposeSecretsFromToString()
    {
        var tokens = new SwarmJoinTokens("worker-secret", "manager-secret");

        Assert.DoesNotContain("worker-secret", tokens.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("manager-secret", tokens.ToString(), StringComparison.Ordinal);
    }
}
