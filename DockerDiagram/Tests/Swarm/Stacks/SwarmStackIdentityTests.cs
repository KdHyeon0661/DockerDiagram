using DockerDiagram.ApplicationServices;

namespace DockerDiagram.Tests;

public sealed class SwarmStackIdentityTests
{

    private static readonly string[] SuggestUniqueIsScopedByCallerAndCaseInsensitiveExpectedValues1 = new[] { "dev", "PROD", "prod-2" };
    private static readonly string[] SuggestUniqueIsScopedByCallerAndCaseInsensitiveExpectedValues2 = new[] { "other-workspace-stack" };

    [Fact]
    public void SuggestUniqueIsScopedByCallerAndCaseInsensitive()
    {
        string suggestion = SwarmStackIdentityPolicy.SuggestUnique(
            "prod",
            SuggestUniqueIsScopedByCallerAndCaseInsensitiveExpectedValues1);

        Assert.Equal("prod-3", suggestion);
        Assert.True(SwarmStackIdentityPolicy.IsUnique("prod", SuggestUniqueIsScopedByCallerAndCaseInsensitiveExpectedValues2));
    }
}
