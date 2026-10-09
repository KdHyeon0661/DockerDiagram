using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmStackDeploymentTests
{
    [Theory]
    [InlineData("web")]
    [InlineData("prod-stack")]
    [InlineData("a.b_c-1")]
    public void StackNamePolicy_AcceptsCliSafeNames(string name)
    {
        SwarmStackNamePolicy.Validate(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UpperCase")]
    [InlineData("-starts-with-dash")]
    [InlineData("contains space")]
    public void StackNamePolicy_RejectsUnsafeNames(string name)
    {
        Assert.Throws<ArgumentException>(() => SwarmStackNamePolicy.Validate(name));
    }

    [Fact]
    public void StackNamePolicy_SuggestsStableSlug()
    {
        Assert.Equal("my-production-stack", SwarmStackNamePolicy.Suggest("My Production Stack"));
    }

    [Fact]
    public void StackDocument_RoundTripsMetadataWithoutChangingYaml()
    {
        const string yaml = "services:\n  api:\n    image: example/api:1\n";
        var metadata = new SwarmStackDocumentMetadata(
            "prod",
            "C:\\deploy\\stack.yml",
            SwarmStackDeployState.Deployed,
            new DateTimeOffset(2026, 9, 21, 10, 30, 0, TimeSpan.Zero),
            string.Empty);

        string serialized = SwarmStackDocument.Serialize(yaml, metadata);
        SwarmStackDocumentData parsed = SwarmStackDocument.Parse(serialized);

        Assert.True(SwarmStackDocument.HasMetadata(serialized));
        Assert.Equal("prod", parsed.Metadata.StackName);
        Assert.Equal(SwarmStackDeployState.Deployed, parsed.Metadata.DeployState);
        Assert.Equal(yaml, parsed.Yaml);
    }

    [Fact]
    public void StackDocument_DoesNotPassCorruptMetadataToDockerYaml()
    {
        SwarmStackDocumentData parsed = SwarmStackDocument.Parse(
            "# dockerdiagram.swarm-stack: not-base64\nservices:\n  web:\n    image: nginx\n");

        Assert.Equal(string.Empty, parsed.Metadata.StackName);
        Assert.StartsWith("services:", parsed.Yaml);
    }

    [Fact]
    public void ServiceSelector_UsesStackNamespaceLabelProjection()
    {
        var services = new[]
        {
            Service("one", "prod_api", "prod"),
            Service("two", "test_api", "test"),
            Service("three", "standalone", string.Empty)
        };

        IReadOnlyList<DockerContainer> selected = SwarmStackServiceSelector.Select(services, "PROD");

        Assert.Single(selected);
        Assert.Equal("one", selected[0].Id);
    }

    [Fact]
    public void DeployCommand_UsesSeparateArgumentsForUntrustedValues()
    {
        var options = new SwarmStackDeploymentOptions
        {
            StackName = "prod-stack",
            Yaml = "services: {}",
            Prune = true,
            WithRegistryAuth = true,
            ResolveImage = "changed"
        };

        IReadOnlyList<string> arguments = SwarmStackCommandBuilder.BuildDeployArguments(
            options,
            "C:\\temp folder\\stack.yml");

        Assert.Equal("stack", arguments[0]);
        Assert.Equal("deploy", arguments[1]);
        Assert.Contains("--prune", arguments);
        Assert.Contains("--with-registry-auth", arguments);
        Assert.Contains("C:\\temp folder\\stack.yml", arguments);
        Assert.Equal("prod-stack", arguments[^1]);
    }

    [Fact]
    public async Task DeploymentService_WritesYamlAndRunsDockerStackDeploy()
    {
        var runner = new RecordingRunner();
        var service = new DockerCliSwarmStackService(runner);
        var options = new SwarmStackDeploymentOptions
        {
            StackName = "prod",
            Yaml = "services:\n  web:\n    image: nginx\n"
        };

        SwarmStackCommandResult result = await service.DeployAsync(options, new ConnectionProfile());

        Assert.True(result.Success);
        Assert.Equal(options.Yaml, runner.YamlSeenDuringRun);
        Assert.Equal("stack", runner.Arguments[0]);
        Assert.Equal("deploy", runner.Arguments[1]);
        Assert.Equal("prod", runner.Arguments[^1]);
        Assert.False(File.Exists(runner.ComposePath));
    }

    [Fact]
    public async Task DeploymentService_UsesStackRemoveCommand()
    {
        var runner = new RecordingRunner();
        var service = new DockerCliSwarmStackService(runner);

        await service.RemoveAsync("prod", new ConnectionProfile());

        Assert.Equal(new[] { "stack", "rm", "prod" }, runner.Arguments);
    }

    private static DockerContainer Service(string id, string name, string stack) => new()
    {
        Id = id,
        Name = name,
        IsSwarmService = true,
        ComposeProjectName = stack
    };

    private sealed class RecordingRunner : ISwarmStackCommandRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public string ComposePath { get; private set; } = string.Empty;
        public string YamlSeenDuringRun { get; private set; } = string.Empty;

        public async Task<SwarmStackCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            ConnectionProfile profile,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            Arguments = arguments.ToArray();
            int composeFlag = arguments.ToList().IndexOf("-c");
            if (composeFlag >= 0)
            {
                ComposePath = arguments[composeFlag + 1];
                YamlSeenDuringRun = await File.ReadAllTextAsync(ComposePath, cancellationToken);
            }
            return new SwarmStackCommandResult(true, 0, "ok", string.Empty);
        }
    }
}

