using DockerDiagram.ApplicationServices;

namespace DockerDiagram.Tests;

public sealed class SwarmStackPreflightTests
{
    [Fact]
    public void BuildWithoutImageIsBlocked()
    {
        const string yaml = """
            services:
              api:
                build: ./api
            """;

        SwarmStackPreflightReport report = SwarmStackPreflightAnalyzer.Analyze(yaml, 1);

        Assert.Contains(report.BlockingIssues, issue =>
            issue.Message.Contains("Registry", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildWithImageWarnsThatSwarmUsesRegistryImage()
    {
        const string yaml = """
            services:
              api:
                build: ./api
                image: registry.example/team/api:1
            """;

        SwarmStackPreflightReport report = SwarmStackPreflightAnalyzer.Analyze(yaml, 1);

        Assert.Empty(report.BlockingIssues);
        Assert.Contains(report.Warnings, issue =>
            issue.Message.Contains("build", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MultiNodeBindAndLocalVolumeAreReportedBeforeDeploy()
    {
        const string yaml = """
            services:
              api:
                image: registry.example/team/api:1
                volumes:
                  - ./config:/app/config:ro
                  - app-data:/app/data
            volumes:
              app-data:
            """;

        SwarmStackPreflightReport report = SwarmStackPreflightAnalyzer.Analyze(yaml, 3);

        Assert.Contains(report.Warnings, issue =>
            issue.Message.Contains("bind mount", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, issue =>
            issue.Message.Contains("local volume", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, issue =>
            issue.Message.Contains("Registry", StringComparison.Ordinal));
    }

    [Fact]
    public void SingleNodeLocalMountsDoNotProduceMultiNodeWarnings()
    {
        const string yaml = """
            services:
              api:
                image: nginx:alpine
                volumes:
                  - ./config:/app/config:ro
                  - app-data:/app/data
            volumes:
              app-data:
            """;

        SwarmStackPreflightReport report = SwarmStackPreflightAnalyzer.Analyze(yaml, 1);

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void InvalidYamlIsBlocked()
    {
        SwarmStackPreflightReport report = SwarmStackPreflightAnalyzer.Analyze(
            "services: [unterminated",
            1);

        Assert.Single(report.BlockingIssues);
    }
}
