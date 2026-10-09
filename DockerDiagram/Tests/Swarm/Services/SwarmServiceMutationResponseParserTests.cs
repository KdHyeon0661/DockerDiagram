using DockerDiagram.Infrastructure;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceMutationResponseParserTests
{
    [Fact]
    public void ParseCreate_ReadsIdAndNonEmptyWarnings()
    {
        SwarmServiceCreateApiResponse result = SwarmServiceMutationResponseParser.ParseCreate(
            """{ "ID": " service-123 ", "Warnings": [" first warning ", "", null] }""");

        Assert.Equal("service-123", result.ServiceId);
        Assert.Equal(new[] { "first warning" }, result.Warnings);
    }

    [Fact]
    public void ParseCreate_RejectsMissingId()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SwarmServiceMutationResponseParser.ParseCreate("""{ "Warnings": [] }"""));
    }

    [Fact]
    public void ParseUpdateWarnings_AllowsEmptyResponse()
    {
        Assert.Empty(SwarmServiceMutationResponseParser.ParseUpdateWarnings(string.Empty));
    }

    [Theory]
    [InlineData("""{ "ID": "service-123", "Warnings": null }""")]
    [InlineData("""{ "ID": "service-123" }""")]
    public void ParseCreate_TreatsMissingOrNullWarningsAsEmpty(string response)
    {
        SwarmServiceCreateApiResponse result =
            SwarmServiceMutationResponseParser.ParseCreate(response);

        Assert.Equal("service-123", result.ServiceId);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void VersionConflictException_RecordsExpectedAndActualVersions()
    {
        var exception = new DockerDiagram.Models.SwarmServiceVersionConflictException("service-1", 7, 8);

        Assert.Equal(7UL, exception.ExpectedVersion);
        Assert.Equal(8UL, exception.ActualVersion);
        Assert.Contains("새로고침", exception.Message, StringComparison.Ordinal);
    }
}
