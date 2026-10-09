using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Reflection;

namespace DockerDiagram.Tests;

public sealed class RuntimeSyncContextTests
{
    [Fact]
    public async Task Sync_DiscardsPreviousConnectionSnapshot_WhenActiveSheetChangesMidQuery()
    {
        IDockerService defaultService = DispatchProxy.Create<IDockerService, ControlledSyncDockerServiceProxy>();
        IDockerService firstService = DispatchProxy.Create<IDockerService, ControlledSyncDockerServiceProxy>();
        IDockerService secondService = DispatchProxy.Create<IDockerService, ControlledSyncDockerServiceProxy>();
        var first = (ControlledSyncDockerServiceProxy)(object)firstService;
        var second = (ControlledSyncDockerServiceProxy)(object)secondService;
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, ControlledSyncDockerServiceProxy>());
        using var main = new MainViewModel(defaultService, dialogService, factory);

        main.SheetManager.AddWorkspace(
            CreateContextProfile("first"),
            firstService,
            activate: true);
        await main.RefreshRuntimeResourcesAsync().WaitAsync(TimeSpan.FromSeconds(5));

        first.Containers = new List<DockerContainer>
        {
            new() { Id = "old-id", Name = "old-service", State = "running" }
        };
        first.BlockNextContainerQuery();
        Task previousConnectionRefresh = main.RefreshRuntimeResourcesAsync();
        await first.ContainerQueryStarted.WaitAsync(TimeSpan.FromSeconds(5));

        second.Containers = new List<DockerContainer>
        {
            new() { Id = "new-id", Name = "new-service", State = "running" }
        };
        second.BlockNextContainerQuery();
        main.SheetManager.AddWorkspace(
            CreateContextProfile("second"),
            secondService,
            activate: true);

        first.ReleaseBlockedContainerQuery();
        await previousConnectionRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        await second.ContainerQueryStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(main.Explorer.ExistingContainers, container => container.Id == "old-id");

        Task currentConnectionRefresh = main.RefreshRuntimeResourcesAsync();
        second.ReleaseBlockedContainerQuery();
        await currentConnectionRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(main.Explorer.ExistingContainers, container => container.Id == "new-id");
        Assert.DoesNotContain(main.Explorer.ExistingContainers, container => container.Id == "old-id");
    }

    private static ConnectionProfile CreateContextProfile(string name) => new()
    {
        Name = name,
        Type = EndpointType.DockerContext,
        DockerEndpoint = $"npipe://{name}",
        RuntimeKind = RuntimeKind.DockerEngine
    };
}

public class ControlledSyncDockerServiceProxy : DispatchProxy
{
    private TaskCompletionSource<List<DockerContainer>>? _blockedContainerQuery;

    public List<DockerContainer> Containers { get; set; } = new();
    public Task ContainerQueryStarted => _containerQueryStarted.Task;
    private TaskCompletionSource _containerQueryStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void BlockNextContainerQuery()
    {
        _containerQueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _blockedContainerQuery = new TaskCompletionSource<List<DockerContainer>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleaseBlockedContainerQuery()
    {
        TaskCompletionSource<List<DockerContainer>>? blocked = _blockedContainerQuery;
        _blockedContainerQuery = null;
        blocked?.TrySetResult(Containers.ToList());
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(ISystemService.PingAsync):
                return Task.FromResult(true);
            case nameof(IContainerService.GetContainersAsync):
                if (_blockedContainerQuery != null)
                {
                    _containerQueryStarted.TrySetResult();
                    return _blockedContainerQuery.Task;
                }
                return Task.FromResult(Containers.ToList());
            case nameof(IVolumeService.GetVolumesAsync):
                return Task.FromResult(new List<DockerVolume>());
            case nameof(INetworkService.GetNetworksAsync):
                return Task.FromResult(new List<DockerNetworkGroup>());
            case nameof(IImageService.GetImagesAsync):
                return Task.FromResult(new List<DockerImage>());
            case nameof(ISystemService.MonitorDockerEventsAsync):
                return Task.Delay(Timeout.Infinite, (CancellationToken)args![1]!);
            case nameof(IDisposable.Dispose):
                return null;
            default:
                throw new NotSupportedException($"Unexpected Docker service call: {targetMethod?.Name}");
        }
    }
}
