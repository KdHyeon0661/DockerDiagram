using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Reflection;

namespace DockerDiagram.Tests;

public sealed class SwarmExplorerDeletionTests
{
    [Fact]
    public async Task SidebarDeleteCommands_RemoveEveryManagedSwarmResourceType()
    {
        IDockerService dockerService = DispatchProxy.Create<ISwarmDeletionTestService, SwarmDeletionDockerServiceProxy>();
        var tracker = (SwarmDeletionDockerServiceProxy)(object)dockerService;
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, SwarmDeletionDockerServiceProxy>());
        using var main = new MainViewModel(dockerService, dialogService, factory);
        main.ActiveSheet!.RuntimeKind = RuntimeKind.DockerSwarm;
        main.ActiveSheet.Profile.RuntimeKind = RuntimeKind.DockerSwarm;

        await main.Explorer.DeleteContainerItemAsync(new DockerContainer
        {
            Id = "service-1",
            Name = "api",
            IsSwarmService = true
        });
        await main.Explorer.DeleteSwarmDataResourceItemAsync(new SwarmDataResourceSnapshot(
            "secret-1",
            "db-password",
            SwarmDataResourceKind.Secret,
            1,
            null,
            new Dictionary<string, string>()));
        await main.Explorer.DeleteSwarmDataResourceItemAsync(new SwarmDataResourceSnapshot(
            "config-1",
            "app-config",
            SwarmDataResourceKind.Config,
            1,
            null,
            new Dictionary<string, string>()));
        await main.Explorer.DeleteNetworkItemAsync(new DockerNetworkGroup
        {
            Id = "network-1",
            Name = "frontend",
            Driver = "overlay",
            Scope = "swarm"
        });

        Assert.Equal(new[] { "service-1" }, tracker.RemovedServices);
        Assert.Equal(
            new[]
            {
                (SwarmDataResourceKind.Secret, "secret-1"),
                (SwarmDataResourceKind.Config, "config-1")
            },
            tracker.RemovedDataResources);
        Assert.Equal(new[] { "network-1" }, tracker.RemovedNetworks);
    }
}

public interface ISwarmDeletionTestService : IDockerService, ISwarmDataResourceMutationService;

public class SwarmDeletionDockerServiceProxy : DispatchProxy
{
    public List<string> RemovedServices { get; } = new();
    public List<(SwarmDataResourceKind Kind, string Id)> RemovedDataResources { get; } = new();
    public List<string> RemovedNetworks { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(ISwarmService.RemoveSwarmServiceAsync):
                RemovedServices.Add((string)args![0]!);
                return Task.CompletedTask;
            case nameof(ISwarmDataResourceMutationService.RemoveSwarmDataResourceAsync):
                RemovedDataResources.Add(((SwarmDataResourceKind)args![0]!, (string)args[1]!));
                return Task.CompletedTask;
            case nameof(INetworkService.RemoveNetworkAsync):
                RemovedNetworks.Add((string)args![0]!);
                return Task.CompletedTask;
            case nameof(ISystemService.PingAsync):
                return Task.FromResult(false);
            case nameof(ISystemService.MonitorDockerEventsAsync):
                return Task.Delay(Timeout.Infinite, (CancellationToken)args![1]!);
            case nameof(IDisposable.Dispose):
                return null;
            default:
                throw new NotSupportedException($"Unexpected Docker service call: {targetMethod?.Name}");
        }
    }
}
