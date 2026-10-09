using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Text.Json;

namespace DockerDiagram.Tests;

public sealed class SwarmIntegrationContractTests
{
    [Fact]
    public void RuntimeProfiles_KeepStandaloneSwarmAndKubernetesCommandsSeparated()
    {
        RuntimeUiProfile docker = RuntimeUiProfiles.For(RuntimeKind.DockerEngine);
        RuntimeUiProfile swarm = RuntimeUiProfiles.For(RuntimeKind.DockerSwarm);
        RuntimeUiProfile kubernetes = RuntimeUiProfiles.For(RuntimeKind.Kubernetes);

        Assert.True(docker.ShowComposeCommands);
        Assert.True(docker.ShowStandaloneSidebar);
        Assert.False(docker.ShowSwarmCommands);
        Assert.True(docker.ShowTertiaryCanvasButton);
        Assert.False(docker.UseSwarmNodeButton);
        Assert.Equal("Undo / Redo 옵션", docker.TertiaryCanvasButtonToolTip);

        Assert.False(swarm.ShowComposeCommands);
        Assert.True(swarm.ShowSwarmSidebar);
        Assert.True(swarm.ShowSwarmCommands);
        Assert.False(swarm.ShowDockerHistoryOptions);
        Assert.True(swarm.ShowTertiaryCanvasButton);
        Assert.True(swarm.UseSwarmNodeButton);
        Assert.Equal("Swarm 노드 관리", swarm.TertiaryCanvasButtonToolTip);

        Assert.False(kubernetes.ShowComposeCommands);
        Assert.False(kubernetes.ShowSwarmCommands);
        Assert.False(kubernetes.ShowImageMenu);
        Assert.False(kubernetes.ShowTertiaryCanvasButton);
    }

    [Fact]
    public void LegacyDiagramWithoutStackMetadata_RemainsReadable()
    {
        const string legacyJson = """
            {
              "Version": "1.38",
              "Sheets": [
                {
                  "Title": "Stack 1",
                  "RuntimeKind": 1,
                  "ComposeRawYaml": "services:\n  web:\n    image: nginx\n",
                  "Nodes": [],
                  "Connections": [],
                  "Groups": []
                }
              ]
            }
            """;

        DiagramFile? diagram = JsonSerializer.Deserialize<DiagramFile>(legacyJson);
        SheetData sheet = Assert.Single(diagram!.Sheets);
        SwarmStackDocumentData parsed = SwarmStackDocument.Parse(sheet.ComposeRawYaml);

        Assert.Equal(RuntimeKind.DockerSwarm, sheet.RuntimeKind);
        Assert.Equal(string.Empty, parsed.Metadata.StackName);
        Assert.StartsWith("services:", parsed.Yaml);
    }

    [Fact]
    public void StackMetadata_RoundTripsThroughExistingSheetDataField()
    {
        const string yaml = "services:\n  api:\n    image: example/api:2\n";
        string document = SwarmStackDocument.Serialize(
            yaml,
            new SwarmStackDocumentMetadata(
                "prod",
                "stack.yml",
                SwarmStackDeployState.Deployed,
                DateTimeOffset.Parse("2026-09-21T12:00:00+09:00"),
                string.Empty));
        var file = new DiagramFile
        {
            Sheets =
            {
                new SheetData
                {
                    Title = "Production",
                    RuntimeKind = RuntimeKind.DockerSwarm,
                    ComposeRawYaml = document
                }
            }
        };

        DiagramFile? restored = JsonSerializer.Deserialize<DiagramFile>(JsonSerializer.Serialize(file));
        SwarmStackDocumentData parsed = SwarmStackDocument.Parse(restored!.Sheets.Single().ComposeRawYaml);

        Assert.Equal("prod", parsed.Metadata.StackName);
        Assert.Equal(SwarmStackDeployState.Deployed, parsed.Metadata.DeployState);
        Assert.Equal(yaml, parsed.Yaml);
    }

    [Fact]
    public void SecretNodePersistence_HasNoPayloadOrJoinTokenField()
    {
        var node = new NodeData
        {
            Name = "database-password",
            RuntimeKind = RuntimeKind.DockerSwarm,
            ResourceKind = RuntimeResourceKind.SwarmSecret,
            DockerId = "secret-id"
        };

        string json = JsonSerializer.Serialize(node);

        Assert.DoesNotContain("SecretData", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JoinToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SWMTKN-", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StackRemove_ForwardsCancellationWithoutRunningDocker()
    {
        var runner = new CancellationRecordingRunner();
        var service = new DockerCliSwarmStackService(runner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RemoveAsync("prod", new ConnectionProfile(), cancellation.Token));
        Assert.True(runner.CancellationObserved);
    }

    private sealed class CancellationRecordingRunner : ISwarmStackCommandRunner
    {
        public bool CancellationObserved { get; private set; }

        public Task<SwarmStackCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            ConnectionProfile profile,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            CancellationObserved = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Canceled calls must never reach Docker.");
        }
    }
}
