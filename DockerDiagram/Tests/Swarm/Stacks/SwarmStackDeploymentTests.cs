using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using System.Text;
using System.Text.Json;

namespace DockerDiagram.Tests;

public sealed class SwarmStackDeploymentTests
{

    private static readonly string[] DeploymentServiceUsesStackRemoveCommandExpectedValues = new[] { "stack", "rm", "prod" };

    [Theory]
    [InlineData("web")]
    [InlineData("prod-stack")]
    [InlineData("a.b_c-1")]
    public void StackNamePolicyAcceptsCliSafeNames(string name)
    {
        SwarmStackNamePolicy.Validate(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UpperCase")]
    [InlineData("-starts-with-dash")]
    [InlineData("contains space")]
    public void StackNamePolicyRejectsUnsafeNames(string name)
    {
        Assert.Throws<ArgumentException>(() => SwarmStackNamePolicy.Validate(name));
    }

    [Fact]
    public void StackNamePolicySuggestsStableSlug()
    {
        Assert.Equal("my-production-stack", SwarmStackNamePolicy.Suggest("My Production Stack"));
    }

    [Fact]
    public void StackDocumentRoundTripsMetadataWithoutChangingYaml()
    {
        const string yaml = "services:\n  api:\n    image: example/api:1\n";
        var metadata = new SwarmStackDocumentMetadata(
            "prod",
            "C:\\deploy\\stack.yml",
            SwarmStackDeployState.Deployed,
            new DateTimeOffset(2026, 9, 21, 10, 30, 0, TimeSpan.Zero),
            string.Empty,
            prune: false,
            withRegistryAuth: true);

        string serialized = SwarmStackDocument.Serialize(yaml, metadata);
        SwarmStackDocumentData parsed = SwarmStackDocument.Parse(serialized);

        Assert.True(SwarmStackDocument.HasMetadata(serialized));
        Assert.Equal("prod", parsed.Metadata.StackName);
        Assert.Equal(SwarmStackDeployState.Deployed, parsed.Metadata.DeployState);
        Assert.False(parsed.Metadata.Prune);
        Assert.True(parsed.Metadata.WithRegistryAuth);
        Assert.Equal(yaml, parsed.Yaml);
    }

    [Fact]
    public void StackDocumentLoadsLegacyMetadataWithSafeDeploymentDefaults()
    {
        string json = JsonSerializer.Serialize(new
        {
            StackName = "legacy",
            SourcePath = string.Empty,
            DeployState = SwarmStackDeployState.Draft,
            LastDeployedAt = (DateTimeOffset?)null,
            LastError = string.Empty
        });
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        SwarmStackDocumentData parsed = SwarmStackDocument.Parse(
            $"# dockerdiagram.swarm-stack: {encoded}\nservices:\n  web:\n    image: nginx\n");

        Assert.Equal("legacy", parsed.Metadata.StackName);
        Assert.True(parsed.Metadata.Prune);
        Assert.False(parsed.Metadata.WithRegistryAuth);
    }

    [Fact]
    public void StackDocumentDoesNotPassCorruptMetadataToDockerYaml()
    {
        SwarmStackDocumentData parsed = SwarmStackDocument.Parse(
            "# dockerdiagram.swarm-stack: not-base64\nservices:\n  web:\n    image: nginx\n");

        Assert.Equal(string.Empty, parsed.Metadata.StackName);
        Assert.StartsWith("services:", parsed.Yaml);
    }

    [Fact]
    public void ServiceSelectorUsesStackNamespaceLabelProjection()
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
    public void DeployCommandUsesSeparateArgumentsForUntrustedValues()
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
    public void ComposeConfigCommandUsesExplicitEnvFileAndPreservesRelativePaths()
    {
        IReadOnlyList<string> arguments = SwarmStackCommandBuilder.BuildComposeConfigArguments(
            "C:\\deploy folder\\stack.yml",
            "C:\\deploy folder\\.env");

        Assert.Equal("compose", arguments[0]);
        Assert.Contains("--env-file", arguments);
        Assert.Contains("C:\\deploy folder\\.env", arguments);
        Assert.Contains("--no-normalize", arguments);
        Assert.Contains("--no-path-resolution", arguments);
    }

    [Fact]
    public async Task DeploymentServiceWritesYamlAndRunsDockerStackDeploy()
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
    public async Task DeploymentServiceUsesOriginalYamlAndSourceDirectoryWhenUnchanged()
    {
        string directory = CreateTestDirectory();
        string sourcePath = Path.Combine(directory, "stack.yml");
        const string yaml = "services:\n  web:\n    image: nginx\nconfigs:\n  app:\n    file: ./config/app.conf\n";
        await File.WriteAllTextAsync(sourcePath, yaml);

        try
        {
            var runner = new RecordingRunner();
            var service = new DockerCliSwarmStackService(runner);
            var options = new SwarmStackDeploymentOptions
            {
                StackName = "prod",
                SourcePath = sourcePath,
                Yaml = yaml
            };

            await service.DeployAsync(options, new ConnectionProfile());

            Assert.Equal(Path.GetFullPath(sourcePath), runner.ComposePath);
            Assert.Equal(Path.GetFullPath(directory), runner.WorkingDirectory);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeploymentServiceKeepsEditedYamlBesideSourceDuringDeploymentAndCleansItUp()
    {
        string directory = CreateTestDirectory();
        string sourcePath = Path.Combine(directory, "stack.yml");
        const string sourceYaml = "services:\n  web:\n    image: nginx:1\n";
        const string editedYaml = "services:\n  web:\n    image: nginx:2\n";
        await File.WriteAllTextAsync(sourcePath, sourceYaml);

        try
        {
            var runner = new RecordingRunner();
            var service = new DockerCliSwarmStackService(runner);
            var options = new SwarmStackDeploymentOptions
            {
                StackName = "prod",
                SourcePath = sourcePath,
                Yaml = editedYaml
            };

            await service.DeployAsync(options, new ConnectionProfile());

            Assert.Equal(Path.GetFullPath(directory), Path.GetDirectoryName(runner.ComposePath));
            Assert.Equal(Path.GetFullPath(directory), runner.WorkingDirectory);
            Assert.NotEqual(Path.GetFullPath(sourcePath), runner.ComposePath);
            Assert.Equal(editedYaml, runner.YamlSeenDuringRun);
            Assert.False(File.Exists(runner.ComposePath));
            Assert.Equal(sourceYaml, await File.ReadAllTextAsync(sourcePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeploymentServiceResolvesDotEnvBeforeStackDeployAndRemovesComposeProjectName()
    {
        string directory = CreateTestDirectory();
        string sourcePath = Path.Combine(directory, "stack.yml");
        string envPath = Path.Combine(directory, ".env");
        const string yaml = "services:\n  web:\n    image: nginx:${TAG}\n";
        await File.WriteAllTextAsync(sourcePath, yaml);
        await File.WriteAllTextAsync(envPath, "TAG=alpine\n");

        try
        {
            var runner = new InterpolationRunner();
            var service = new DockerCliSwarmStackService(runner);
            var options = new SwarmStackDeploymentOptions
            {
                StackName = "prod",
                SourcePath = sourcePath,
                Yaml = yaml
            };

            SwarmStackCommandResult result = await service.DeployAsync(options, new ConnectionProfile());

            Assert.True(result.Success);
            Assert.Equal(2, runner.Calls.Count);
            Assert.Equal("compose", runner.Calls[0][0]);
            Assert.Contains(envPath, runner.Calls[0]);
            Assert.Equal("stack", runner.Calls[1][0]);
            Assert.DoesNotContain("name:", runner.DeployedYaml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("image: nginx:alpine", runner.DeployedYaml, StringComparison.Ordinal);
            Assert.False(File.Exists(runner.DeployedPath));
            Assert.Equal(yaml, await File.ReadAllTextAsync(sourcePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeploymentServiceDetectsComposeVariablesButNotEscapedDollarSigns()
    {
        Assert.True(DockerCliSwarmStackService.RequiresComposeInterpolation("image: nginx:${TAG}"));
        Assert.True(DockerCliSwarmStackService.RequiresComposeInterpolation("image: nginx:$TAG"));
        Assert.False(DockerCliSwarmStackService.RequiresComposeInterpolation("command: echo $$HOSTNAME"));
    }

    [Fact]
    public void DeploymentServiceStripsOnlyComposeTopLevelProjectName()
    {
        const string rendered = "name: project\nservices:\n  api:\n    image: example/name:1\n";

        string stackYaml = DockerCliSwarmStackService.RemoveComposeProjectName(rendered);

        Assert.DoesNotContain("name: project", stackYaml, StringComparison.Ordinal);
        Assert.Contains("image: example/name:1", stackYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentServiceRejectsMissingRememberedSourceInsteadOfChangingPathBase()
    {
        var runner = new RecordingRunner();
        var service = new DockerCliSwarmStackService(runner);
        var options = new SwarmStackDeploymentOptions
        {
            StackName = "prod",
            SourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.yml"),
            Yaml = "services:\n  web:\n    image: nginx\n"
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.DeployAsync(options, new ConnectionProfile()));

        Assert.Empty(runner.Arguments);
    }

    [Fact]
    public async Task DeploymentServiceUsesStackRemoveCommand()
    {
        var runner = new RecordingRunner();
        var service = new DockerCliSwarmStackService(runner);

        await service.RemoveAsync("prod", new ConnectionProfile());

        Assert.Equal(DeploymentServiceUsesStackRemoveCommandExpectedValues, runner.Arguments);
    }

    private static DockerContainer Service(string id, string name, string stack) => new()
    {
        Id = id,
        Name = name,
        IsSwarmService = true,
        ComposeProjectName = stack
    };

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "DockerDiagram.Stack.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingRunner : ISwarmStackCommandRunner
    {
        public string[] Arguments { get; private set; } = Array.Empty<string>();
        public string ComposePath { get; private set; } = string.Empty;
        public string YamlSeenDuringRun { get; private set; } = string.Empty;
        public string WorkingDirectory { get; private set; } = string.Empty;

        public async Task<SwarmStackCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            ConnectionProfile profile,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            Arguments = arguments.ToArray();
            WorkingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
            int composeFlag = arguments.ToList().IndexOf("-c");
            if (composeFlag >= 0)
            {
                ComposePath = arguments[composeFlag + 1];
                YamlSeenDuringRun = await File.ReadAllTextAsync(ComposePath, cancellationToken);
            }
            return new SwarmStackCommandResult(true, 0, "ok", string.Empty);
        }
    }

    private sealed class InterpolationRunner : ISwarmStackCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public string DeployedPath { get; private set; } = string.Empty;
        public string DeployedYaml { get; private set; } = string.Empty;

        public async Task<SwarmStackCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            ConnectionProfile profile,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            if (arguments[0] == "compose")
            {
                return new SwarmStackCommandResult(
                    true,
                    0,
                    "name: source-folder\nservices:\n  web:\n    image: nginx:alpine\n",
                    string.Empty);
            }

            int composeFlag = arguments.ToList().IndexOf("-c");
            DeployedPath = arguments[composeFlag + 1];
            DeployedYaml = await File.ReadAllTextAsync(DeployedPath, cancellationToken);
            return new SwarmStackCommandResult(true, 0, "ok", string.Empty);
        }
    }
}

