using System.Reflection;
using Docker.DotNet.Models;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class DiagramHistoryIsolationTests
{

    private static readonly string[] DockerHistoryUsesRecordedSheetServiceAndRecreatesFromInspectSnapshotExpectedValues = new[] { "important-data:/data:ro" };

    [Fact]
    public void SwitchingSheetsClearsGlobalHistoryToPreventCrossSheetReplay()
    {
        using IDockerService service = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
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
    public async Task DockerHistoryUsesRecordedSheetServiceAndRecreatesFromInspectSnapshot()
    {
        using IDockerService firstService = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
        using IDockerService secondService = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
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
        Assert.Equal(DockerHistoryUsesRecordedSheetServiceAndRecreatesFromInspectSnapshotExpectedValues, first.LastRecreateBinds);
        Assert.Equal("recreated-id", node.ContainerId);
        Assert.Contains(node, firstSheet.Nodes);
    }

    [Fact]
    public async Task ExplicitPermanentDeleteRemovesProtectedExistingVolume()
    {
        using IDockerService service = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
        var docker = (HistoryDockerServiceProxy)(object)service;
        docker.Volumes.Add(new DockerVolume { Id = "existing-data", Name = "existing-data" });
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>());
        using var main = new MainViewModel(service, dialogService, factory);
        SheetViewModel sheet = main.ActiveSheet!;
        var node = CreateExistingVolumeNode(service, dialogService, sheet, "existing-data");
        sheet.Nodes.Add(node);

        IHistoryCommand command = main.CreateNodeDeleteCommand(
            sheet,
            node,
            deleteDocker: true,
            deleteExternalVolume: true);

        await command.RedoAsync();

        Assert.Equal(1, docker.RemoveVolumeCount);
        Assert.DoesNotContain(docker.Volumes, volume => volume.Name == "existing-data");
        Assert.DoesNotContain(node, sheet.Nodes);
    }

    [Fact]
    public async Task InspectorPermanentDeletePassesExplicitOverrideForExistingVolume()
    {
        using IDockerService service = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
        var docker = (HistoryDockerServiceProxy)(object)service;
        docker.Volumes.Add(new DockerVolume { Id = "inspector-data", Name = "inspector-data" });
        IDialogService dialogService = DispatchProxy.Create<IDialogService, PermanentDeleteDialogServiceProxy>();
        var dialogs = (PermanentDeleteDialogServiceProxy)(object)dialogService;
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>());
        using var main = new MainViewModel(service, dialogService, factory);
        SheetViewModel sheet = main.ActiveSheet!;
        var node = CreateExistingVolumeNode(service, dialogService, sheet, "inspector-data");
        sheet.Nodes.Add(node);
        main.Inspector.SelectedElement = node;

        await main.Inspector.DeleteSelectedAsync();

        Assert.Equal(1, dialogs.PermanentDeleteChoiceCount);
        Assert.Equal(1, dialogs.ExternalVolumeConfirmationCount);
        Assert.Equal(0, dialogs.ErrorCount);
        Assert.Equal(1, docker.RemoveVolumeCount);
        Assert.DoesNotContain(docker.Volumes, volume => volume.Name == "inspector-data");
        Assert.DoesNotContain(node, sheet.Nodes);
    }

    [Fact]
    public async Task AutomaticDeleteKeepsProtectedExistingVolume()
    {
        using IDockerService service = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
        var docker = (HistoryDockerServiceProxy)(object)service;
        docker.Volumes.Add(new DockerVolume { Id = "shared-data", Name = "shared-data" });
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>());
        using var main = new MainViewModel(service, dialogService, factory);
        SheetViewModel sheet = main.ActiveSheet!;
        var node = CreateExistingVolumeNode(service, dialogService, sheet, "shared-data");
        sheet.Nodes.Add(node);

        IHistoryCommand command = main.CreateNodeDeleteCommand(sheet, node, deleteDocker: true);

        await command.RedoAsync();

        Assert.Equal(0, docker.RemoveVolumeCount);
        Assert.Contains(docker.Volumes, volume => volume.Name == "shared-data");
        Assert.DoesNotContain(node, sheet.Nodes);
    }

    [Fact]
    public async Task FailedVolumeRemovalKeepsDiagramNode()
    {
        using IDockerService service = DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>();
        var docker = (HistoryDockerServiceProxy)(object)service;
        docker.Volumes.Add(new DockerVolume { Id = "stubborn-data", Name = "stubborn-data" });
        docker.KeepVolumeAfterRemove = true;
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, HistoryDockerServiceProxy>());
        using var main = new MainViewModel(service, dialogService, factory);
        SheetViewModel sheet = main.ActiveSheet!;
        var node = CreateExistingVolumeNode(service, dialogService, sheet, "stubborn-data");
        sheet.Nodes.Add(node);
        IHistoryCommand command = main.CreateNodeDeleteCommand(
            sheet,
            node,
            deleteDocker: true,
            deleteExternalVolume: true);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(command.RedoAsync);

        Assert.Contains("삭제를 완료하지 않았습니다", exception.Message);
        Assert.Equal(1, docker.RemoveVolumeCount);
        Assert.Contains(node, sheet.Nodes);
    }

    private static NodeViewModel CreateExistingVolumeNode(
        IDockerService service,
        IDialogService dialogService,
        SheetViewModel sheet,
        string volumeName) =>
        new(service, service, dialogService)
        {
            Name = volumeName,
            DockerVolumeName = volumeName,
            Type = NodeType.Volume,
            VolumeExternal = true,
            IsDockerConnected = true,
            ParentSheet = sheet
        };
}

public class HistoryDockerServiceProxy : DispatchProxy
{
    public int RemoveCount { get; private set; }
    public int RemoveVolumeCount { get; private set; }
    public int RecreateCount { get; private set; }
    public bool KeepVolumeAfterRemove { get; set; }
    public List<DockerVolume> Volumes { get; } = [];
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
                return Task.FromResult(Volumes.ToList());
            case nameof(IVolumeService.InspectVolumeAsync):
                return Task.FromResult(new VolumeResponse
                {
                    Name = (string)args![0]!,
                    Driver = "local",
                    Mountpoint = string.Empty,
                    CreatedAt = string.Empty,
                    Labels = new Dictionary<string, string>(),
                    Options = new Dictionary<string, string>()
                });
            case nameof(IVolumeService.GetContainersUsingVolumeAsync):
                return Task.FromResult(new List<string>());
            case nameof(IVolumeService.GetVolumeUsageDetailsAsync):
                return Task.FromResult(new List<VolumeUsageInfo>());
            case nameof(IVolumeService.RemoveVolumeAsync):
                RemoveVolumeCount++;
                if (!KeepVolumeAfterRemove)
                {
                    string volumeName = (string)args![0]!;
                    Volumes.RemoveAll(volume => string.Equals(
                        volume.Name,
                        volumeName,
                        StringComparison.OrdinalIgnoreCase));
                }
                return Task.CompletedTask;
            case nameof(INetworkService.GetNetworksAsync):
                return Task.FromResult(new List<DockerNetworkGroup>());
            case nameof(IImageService.GetImagesAsync):
                return Task.FromResult(new List<DockerImage>());
            case nameof(ISystemService.PingAsync):
                return Task.FromResult(true);
            case nameof(ISystemService.GetSystemDiskUsageAsync):
                return Task.FromResult(new SystemDiskUsage());
            case nameof(ISystemService.MonitorDockerEventsAsync):
                return Task.Delay(Timeout.Infinite, (CancellationToken)args![1]!);
            case nameof(IDisposable.Dispose):
                return null;
            default:
                throw new NotSupportedException($"Unexpected Docker service call: {targetMethod?.Name}");
        }
    }
}

public class PermanentDeleteDialogServiceProxy : DispatchProxy
{
    public int PermanentDeleteChoiceCount { get; private set; }
    public int ExternalVolumeConfirmationCount { get; private set; }
    public int ErrorCount { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IDialogService.ShowYesNoCancel):
                PermanentDeleteChoiceCount++;
                return DialogChoice.Yes;
            case nameof(IDialogService.ShowConfirm):
                ExternalVolumeConfirmationCount++;
                return true;
            case nameof(IDialogService.ShowError):
                ErrorCount++;
                return null;
            case nameof(IDialogService.InvokeOnUiThreadAsync):
                if (args![0] is Action action)
                {
                    action();
                    return Task.CompletedTask;
                }
                throw new NotSupportedException("Generic UI invocation is not used by this test.");
            case nameof(IDialogService.BeginInvokeOnUiThread):
                ((Action)args![0]!)();
                return null;
            case nameof(IDisposable.Dispose):
                return null;
            default:
                Type returnType = targetMethod?.ReturnType ?? typeof(void);
                if (returnType == typeof(void)) return null;
                if (returnType == typeof(bool)) return true;
                if (returnType == typeof(Task)) return Task.CompletedTask;
                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
