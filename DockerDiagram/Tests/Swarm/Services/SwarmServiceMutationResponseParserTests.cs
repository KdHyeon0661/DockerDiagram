using DockerDiagram.Infrastructure;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceMutationResponseParserTests
{

    private static readonly string[] ParseCreateReadsIdAndNonEmptyWarningsExpectedValues = new[] { "first warning" };

    [Fact]
    public void ParseCreateReadsIdAndNonEmptyWarnings()
    {
        SwarmServiceCreateApiResponse result = SwarmServiceMutationResponseParser.ParseCreate(
            """{ "ID": " service-123 ", "Warnings": [" first warning ", "", null] }""");

        Assert.Equal("service-123", result.ServiceId);
        Assert.Equal(ParseCreateReadsIdAndNonEmptyWarningsExpectedValues, result.Warnings);
    }

    [Fact]
    public void ParseCreateRejectsMissingId()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SwarmServiceMutationResponseParser.ParseCreate("""{ "Warnings": [] }"""));
    }

    [Fact]
    public void ParseUpdateWarningsAllowsEmptyResponse()
    {
        Assert.Empty(SwarmServiceMutationResponseParser.ParseUpdateWarnings(string.Empty));
    }

    [Theory]
    [InlineData("""{ "ID": "service-123", "Warnings": null }""")]
    [InlineData("""{ "ID": "service-123" }""")]
    public void ParseCreateTreatsMissingOrNullWarningsAsEmpty(string response)
    {
        SwarmServiceCreateApiResponse result =
            SwarmServiceMutationResponseParser.ParseCreate(response);

        Assert.Equal("service-123", result.ServiceId);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void VersionConflictExceptionRecordsExpectedAndActualVersions()
    {
        var exception = new DockerDiagram.Models.SwarmServiceVersionConflictException("service-1", 7, 8);

        Assert.Equal(7UL, exception.ExpectedVersion);
        Assert.Equal(8UL, exception.ActualVersion);
        Assert.Contains("새로고침", exception.Message, StringComparison.Ordinal);
    }
}
