using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmPublishedPortInputParserTests
{
    [Fact]
    public void Parse_AcceptsAutomaticPublishedPortAndHostUdp()
    {
        SwarmPublishedPortOptions result =
            SwarmPublishedPortInputParser.Parse("", "53", "udp", "host");

        Assert.Null(result.PublishedPort);
        Assert.Equal(53U, result.TargetPort);
        Assert.Equal(SwarmPortProtocol.Udp, result.Protocol);
        Assert.Equal(SwarmPublishMode.Host, result.PublishMode);
    }

    [Theory]
    [InlineData("0", "80", "tcp", "ingress", "Public Port")]
    [InlineData("65536", "80", "tcp", "ingress", "Public Port")]
    [InlineData("8080", "", "tcp", "ingress", "Target Port")]
    [InlineData("8080", "70000", "tcp", "ingress", "Target Port")]
    [InlineData("8080", "80", "icmp", "ingress", "Protocol")]
    [InlineData("8080", "80", "tcp", "invalid", "Publish Mode")]
    public void TryParse_RejectsInvalidInput(
        string published,
        string target,
        string protocol,
        string mode,
        string expectedMessage)
    {
        bool parsed = SwarmPublishedPortInputParser.TryParse(
            published,
            target,
            protocol,
            mode,
            out _,
            out string error);

        Assert.False(parsed);
        Assert.Contains(expectedMessage, error, StringComparison.OrdinalIgnoreCase);
    }
}
