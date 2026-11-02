using System.Reflection;
using Docker.DotNet.Models;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class CreationContextTests
{

    private static readonly string[] ParseDockerRunCommandAcceptsOnlyDockerRunExpectedValues = new[] { "docker", "run", "--name", "web", "nginx:latest" };

    [Fact]
    public void ParseDockerRunCommandAcceptsOnlyDockerRun()
    {
        List<string> tokens = MainViewModel.ParseDockerRunCommand(
            "docker run --name web nginx:latest");

        Assert.Equal(ParseDockerRunCommandAcceptsOnlyDockerRunExpectedValues, tokens);
        Assert.Throws<InvalidOperationException>(() =>
            MainViewModel.ParseDockerRunCommand("cmd.exe /c echo unsafe"));
    }

    [Fact]
    public async Task CreateContainerPinsInitiatingSheetAndServiceAcrossAwait()
    {
        using IDockerService firstService = DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>();
        using IDockerService secondService = DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>();
        var first = (CreationDockerServiceProxy)(object)firstService;
        var second = (CreationDockerServiceProxy)(object)secondService;
        first.BlockNextContainerQuery();

        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>());
        using var main = new MainViewModel(firstService, dialogService, factory);
        SheetViewModel firstSheet = main.ActiveSheet!;
        ConnectionWorkspaceViewModel secondWorkspace = main.SheetManager.AddWorkspace(
            new ConnectionProfile
            {
                Name = "Second",
                Type = EndpointType.DockerContext,
                DockerEndpoint = "npipe://second"
            },
            secondService,
            activate: false);
        SheetViewModel secondSheet = secondWorkspace.Sheets.Single();

        Task creation = main.CreateNewContainerNodeAsync(
            "web",
            "nginx",
            "latest",
            new List<string>(),
            new List<string>(),
            Array.Empty<ContainerMountSpec>(),
            "no",
            0,
            0,
            100,
            100);

        await first.ContainerQueryStarted.WaitAsync(TimeSpan.FromSeconds(5));
        main.ActiveSheet = secondSheet;
        first.ReleaseContainerQuery();
        await creation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, first.CreateContainerCount);
        Assert.Equal(0, second.CreateContainerCount);
        Assert.Contains(firstSheet.Nodes, node => node.ContainerId == "created-1");
        Assert.Empty(secondSheet.Nodes);
    }

    [Fact]
    public async Task CreateContainerMarksPreExistingNamedVolumeAsExternalReference()
    {
        using IDockerService service = DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>();
        var proxy = (CreationDockerServiceProxy)(object)service;
        proxy.Volumes.Add(new DockerVolume { Name = "important-data", Id = "volume-id" });

        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>());
        using var main = new MainViewModel(service, dialogService, factory);

        await main.CreateNewContainerNodeAsync(
            "web",
            "nginx",
            "latest",
            new List<string>(),
            new List<string>(),
            new[]
            {
                new ContainerMountSpec
                {
                    Kind = MountKind.Volume,
                    Source = "important-data",
                    Target = "/data"
                }
            },
            "no",
            0,
            0,
            100,
            100);

        NodeViewModel volume = Assert.Single(main.ActiveSheet!.Nodes, node => node.Type == NodeType.Volume);
        Assert.Equal("important-data", volume.EffectiveVolumeName);
        Assert.True(volume.VolumeExternal);
    }

    [Fact]
    public async Task CreateContainerDoesNotOverwriteOwnershipOfVolumeAlreadyOnSheet()
    {
        using IDockerService service = DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>());
        using var main = new MainViewModel(service, dialogService, factory);
        var existingVolume = new NodeViewModel(service, service, dialogService)
        {
            Type = NodeType.Volume,
            Name = "app-data",
            DockerVolumeName = "app-data",
            VolumeExternal = false,
            ParentSheet = main.ActiveSheet
        };
        main.ActiveSheet!.Nodes.Add(existingVolume);

        await main.CreateNewContainerNodeAsync(
            "worker",
            "nginx",
            "latest",
            [],
            [],
            [new ContainerMountSpec { Kind = MountKind.Volume, Source = "app-data", Target = "/data" }],
            "no",
            0,
            0,
            100,
            100);

        Assert.False(existingVolume.VolumeExternal);
        Assert.Same(existingVolume, Assert.Single(main.ActiveSheet.Nodes, node => node.Type == NodeType.Volume));
    }

    [Fact]
    public async Task CreateContainerUsesConservativeOwnershipWhenVolumeInventoryFails()
    {
        using IDockerService service = DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>();
        var proxy = (CreationDockerServiceProxy)(object)service;
        proxy.ThrowOnVolumeQuery = true;
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, CreationDockerServiceProxy>());
        using var main = new MainViewModel(service, dialogService, factory);

        await main.CreateNewContainerNodeAsync(
            "worker",
            "nginx",
            "latest",
            [],
            [],
            [new ContainerMountSpec { Kind = MountKind.Volume, Source = "uncertain-data", Target = "/data" }],
            "no",
            0,
            0,
            100,
            100);

        NodeViewModel volume = Assert.Single(main.ActiveSheet!.Nodes, node => node.Type == NodeType.Volume);
        Assert.True(volume.VolumeExternal);
    }
}

public class CreationDockerServiceProxy : DispatchProxy
{
    private TaskCompletionSource<List<DockerContainer>>? _blockedContainers;
    private TaskCompletionSource _containerQueryStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<DockerVolume> Volumes { get; } = new();
    public bool ThrowOnVolumeQuery { get; set; }
    public int CreateContainerCount { get; private set; }
    public Task ContainerQueryStarted => _containerQueryStarted.Task;

    public void BlockNextContainerQuery()
    {
        _containerQueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _blockedContainers = new TaskCompletionSource<List<DockerContainer>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleaseContainerQuery()
    {
        TaskCompletionSource<List<DockerContainer>>? blocked = _blockedContainers;
        _blockedContainers = null;
        blocked?.TrySetResult(new List<DockerContainer>());
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IContainerService.GetContainersAsync):
                if (_blockedContainers != null)
                {
                    _containerQueryStarted.TrySetResult();
                    return _blockedContainers.Task;
                }
                return Task.FromResult(new List<DockerContainer>());
            case nameof(IVolumeService.GetVolumesAsync):
                if (ThrowOnVolumeQuery)
                    throw new InvalidOperationException("volume inventory unavailable");
                return Task.FromResult(Volumes.ToList());
            case nameof(INetworkService.GetNetworksAsync):
                return Task.FromResult(new List<DockerNetworkGroup>());
            case nameof(IImageService.GetImagesAsync):
                return Task.FromResult(new List<DockerImage>());
            case nameof(IImageService.PullImageWithProgressAsync):
                return Task.CompletedTask;
            case nameof(IContainerService.CreateAndStartContainerAsync):
                CreateContainerCount++;
                return Task.FromResult($"created-{CreateContainerCount}");
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
