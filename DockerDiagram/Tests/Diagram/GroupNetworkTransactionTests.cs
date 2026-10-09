using System.Reflection;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class GroupNetworkTransactionTests
{
    [Fact]
    public async Task AddNodeAsync_ConnectFailureDoesNotAddVisualMembership()
    {
        INetworkService networkService = DispatchProxy.Create<INetworkService, FailingNetworkMutationProxy>();
        IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        var sheet = new SheetViewModel(
            "Context",
            new ConnectionProfile { Type = EndpointType.DockerContext, DockerEndpoint = "npipe://test" },
            dockerService,
            dialogService);
        var group = new GroupViewModel(0, 0, 300, 200, networkService, dialogService, "app-net", GroupType.Network)
        {
            IsDockerConnected = true
        };
        var node = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Name = "web",
            Type = NodeType.Container,
            ContainerId = "container-1"
        };
        sheet.AddGroup(group);
        sheet.Nodes.Add(node);

        await Assert.ThrowsAsync<InvalidOperationException>(() => group.AddNodeAsync(node));

        Assert.DoesNotContain(node, group.ContainedNodes);
    }

    [Fact]
    public async Task RemoveNodeAsync_DisconnectFailurePreservesVisualMembership()
    {
        INetworkService networkService = DispatchProxy.Create<INetworkService, FailingNetworkMutationProxy>();
        IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        var sheet = new SheetViewModel(
            "Context",
            new ConnectionProfile { Type = EndpointType.DockerContext, DockerEndpoint = "npipe://test" },
            dockerService,
            dialogService);
        var group = new GroupViewModel(0, 0, 300, 200, networkService, dialogService, "app-net", GroupType.Network)
        {
            IsDockerConnected = true
        };
        var node = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Name = "web",
            Type = NodeType.Container,
            ContainerId = "container-1"
        };
        sheet.AddGroup(group);
        sheet.Nodes.Add(node);
        await group.AddNodeAsync(node, isRestoring: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => group.RemoveNodeAsync(node));

        Assert.Contains(node, group.ContainedNodes);
    }
}

public class FailingNetworkMutationProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(INetworkService.ConnectNetworkAsync) => Task.FromException(
            new InvalidOperationException("connect failed")),
        nameof(INetworkService.DisconnectNetworkAsync) => Task.FromException(
            new InvalidOperationException("disconnect failed")),
        _ => throw new NotSupportedException($"Unexpected network service call: {targetMethod?.Name}")
    };
}
