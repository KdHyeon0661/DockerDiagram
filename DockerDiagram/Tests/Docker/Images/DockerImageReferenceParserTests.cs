using DockerDiagram.Infrastructure;

namespace DockerDiagram.Tests;

public sealed class DockerImageReferenceParserTests
{
    [Theory]
    [InlineData("nginx", "latest", "nginx", "latest")]
    [InlineData("nginx:1.27", "latest", "nginx", "1.27")]
    [InlineData("registry.example:5000/team/api:2", "latest", "registry.example:5000/team/api", "2")]
    [InlineData("registry.example:5000/team/api", "stable", "registry.example:5000/team/api", "stable")]
    [InlineData("  alpine:3.20  ", "latest", "alpine", "3.20")]
    public void Split_HandlesDockerRepositoryAndTagForms(
        string reference,
        string defaultTag,
        string expectedRepository,
        string expectedTag)
    {
        (string repository, string tag) = DockerImageReferenceParser.Split(reference, defaultTag);

        Assert.Equal(expectedRepository, repository);
        Assert.Equal(expectedTag, tag);
    }
}
