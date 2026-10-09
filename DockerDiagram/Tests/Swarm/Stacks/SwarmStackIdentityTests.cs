using DockerDiagram.ApplicationServices;

namespace DockerDiagram.Tests;

public sealed class SwarmStackIdentityTests
{
    [Fact]
    public void SuggestUnique_IsScopedByCallerAndCaseInsensitive()
    {
        string suggestion = SwarmStackIdentityPolicy.SuggestUnique(
            "prod",
            new[] { "dev", "PROD", "prod-2" });

        Assert.Equal("prod-3", suggestion);
        Assert.True(SwarmStackIdentityPolicy.IsUnique("prod", new[] { "other-workspace-stack" }));
    }
}
