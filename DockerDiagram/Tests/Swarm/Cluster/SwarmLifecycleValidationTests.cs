using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmLifecycleValidationTests
{

    private static readonly string[] JoinOptionsNormalizesAndDeduplicatesManagerAddressesExpectedValues = new[] { "10.0.0.1:2377", "10.0.0.2:2377" };

    [Fact]
    public void InitializeOptionsRejectInvalidDataPathPort()
    {
        var options = new SwarmInitializeOptions
        {
            AdvertiseAddress = "10.0.0.10:2377",
            DataPathPort = 80
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void JoinOptionsNormalizesAndDeduplicatesManagerAddresses()
    {
        var options = new SwarmJoinOptions
        {
            RemoteManagerAddresses = new[] { " 10.0.0.1:2377 ", "10.0.0.1:2377", "10.0.0.2:2377" },
            JoinToken = "secret"
        };

        options.Validate();

        Assert.Equal(JoinOptionsNormalizesAndDeduplicatesManagerAddressesExpectedValues, options.GetNormalizedManagerAddresses());
        Assert.DoesNotContain("secret", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JoinTokensDoNotExposeSecretsFromToString()
    {
        var tokens = new SwarmJoinTokens("worker-secret", "manager-secret");

        Assert.DoesNotContain("worker-secret", tokens.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("manager-secret", tokens.ToString(), StringComparison.Ordinal);
    }
}
