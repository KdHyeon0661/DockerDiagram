using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmResourceReferenceInputParserTests
{
    [Fact]
    public void Parse_AcceptsAbsoluteTargetNamedOwnersAndOctalMode()
    {
        SwarmResourceTargetOptions result = SwarmResourceReferenceInputParser.Parse(
            " /etc/app/config.yml ",
            " appuser ",
            " appgroup ",
            "0400");

        Assert.Equal("/etc/app/config.yml", result.FileName);
        Assert.Equal("appuser", result.Uid);
        Assert.Equal("appgroup", result.Gid);
        Assert.Equal(256u, result.Mode);
        Assert.Equal("0400", SwarmResourceReferenceInputParser.FormatMode(result.Mode));
    }

    [Theory]
    [InlineData("", "0", "0", "0444", "Target")]
    [InlineData("secret", "bad user", "0", "0444", "UID")]
    [InlineData("secret", "0", "bad group", "0444", "GID")]
    [InlineData("secret", "0", "0", "0488", "Mode")]
    [InlineData("secret", "0", "0", "1000", "Mode")]
    public void TryParse_RejectsInvalidReferenceSettings(
        string target,
        string uid,
        string gid,
        string mode,
        string expectedError)
    {
        bool parsed = SwarmResourceReferenceInputParser.TryParse(
            target,
            uid,
            gid,
            mode,
            out _,
            out string error);

        Assert.False(parsed);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
    }
}
