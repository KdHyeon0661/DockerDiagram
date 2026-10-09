using System.Reflection;
using Docker.DotNet.Models;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class DiagramHistoryIsolationTests
{
    [Fact]
    public void SwitchingSheetsClearsGlobalHistoryToPreventCrossSheetReplay()
    {
        IDockerService service = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>());
        using var main = new MainViewModel(service, dialogService, factory);
        main.History.RecordExecuted(new DelegateHistoryCommand(
            "diagram change",
            affectsDocker: false,
            undo: () => Task.CompletedTask,
            redo: () => Task.CompletedTask));
        Assert.True(main.History.CanUndo);

        main.SheetManager.AddSheet();

        Assert.False(main.History.CanUndo);
        Assert.False(main.History.CanRedo);
    }

    [Fact]
    public async Task DockerHistory_UsesRecordedSheetServiceAndRecreatesFromInspectSnapshot()
    {
        IDockerService firstService = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
        IDockerService secondService = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
        var first = (HistoryDockerServiceProxy)(object)firstService;
        var second = (HistoryDockerServiceProxy)(object)secondService;
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>());
        using var main = new MainViewModel(firstService, dialogService, factory);
        SheetViewModel firstSheet = main.ActiveSheet!;
        var node = new NodeViewModel(firstService, firstService, dialogService)
        {
            Name = "web",
            Type = NodeType.Container,
            ContainerId = "original-id",
            ParentSheet = firstSheet
        };
        firstSheet.Nodes.Add(node);
        IHistoryCommand command = main.CreateNodeDeleteCommand(firstSheet, node, deleteDocker: true);

        ConnectionWorkspaceViewModel secondWorkspace = main.SheetManager.AddWorkspace(
            new ConnectionProfile
            {
                Name = "Second",
                Type = EndpointType.DockerContext,
                DockerEndpoint = "npipe://second"
            },
            secondService,
            activate: true);
        Assert.Same(secondWorkspace.ActiveSheet, main.ActiveSheet);

        await command.RedoAsync();
        await command.UndoAsync();

        Assert.Equal(1, first.RemoveCount);
        Assert.Equal(1, first.RecreateCount);
        Assert.Equal(0, second.RemoveCount);
        Assert.Equal(0, second.RecreateCount);
        Assert.Equal(new[] { "important-data:/data:ro" }, first.LastRecreateBinds);
        Assert.Equal("recreated-id", node.ContainerId);
        Assert.Contains(node, firstSheet.Nodes);
    }
}

public class HistoryDockerServiceProxy : DispatchProxy
{
    public int RemoveCount { get; private set; }
    public int RecreateCount { get; private set; }
    public IReadOnlyList<string> LastRecreateBinds { get; private set; } = Array.Empty<string>();

    private static ContainerInspectResponse CreateInspect() => new()
    {
        ID = "original-id",
        Name = "/web",
        Created = DateTime.UtcNow,
        State = new ContainerState
        {
            Status = "exited",
            Running = false,
            Paused = false,
            StartedAt = string.Empty,
            FinishedAt = string.Empty
        },
        Config = new Config
        {
            Image = "nginx:latest",
            Env = new List<string> { "MODE=prod" },
            Labels = new Dictionary<string, string> { ["app"] = "web" }
        },
        HostConfig = new HostConfig
        {
            Binds = new List<string> { "important-data:/data:ro" },
            Memory = 268435456,
            NanoCPUs = 500000000,
            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
        },
        Mounts = new List<MountPoint>(),
        NetworkSettings = new NetworkSettings
        {
            Networks = new Dictionary<string, EndpointSettings>()
        }
    };

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IContainerService.InspectContainerAsync):
                return Task.FromResult(CreateInspect());
            case nameof(IContainerService.RemoveContainerAsync):
                RemoveCount++;
                return Task.CompletedTask;
            case nameof(IContainerService.RecreateContainerFromInspectAsync):
                RecreateCount++;
                LastRecreateBinds = ((IList<string>)args![2]!).ToList();
                return Task.FromResult("recreated-id");
            case nameof(IContainerService.GetSystemInfoAsync):
                return Task.FromException<SystemInfoResponse>(new InvalidOperationException("not available"));
            case nameof(IContainerService.GetContainersAsync):
                return Task.FromResult(new List<DockerContainer>());
            case nameof(IVolumeService.GetVolumesAsync):
                return Task.FromResult(new List<DockerVolume>());
            case nameof(INetworkService.GetNetworksAsync):
                return Task.FromResult(new List<DockerNetworkGroup>());
            case nameof(IImageService.GetImagesAsync):
                return Task.FromResult(new List<DockerImage>());
            case nameof(ISystemService.PingAsync):
                return Task.FromResult(true);
            case nameof(ISystemService.MonitorDockerEventsAsync):
                return Task.Delay(Timeout.Infinite, (CancellationToken)args![1]!);
            case nameof(IDisposable.Dispose):
                return null;
            default:
                throw new NotSupportedException($"Unexpected Docker service call: {targetMethod?.Name}");
        }
    }
}
