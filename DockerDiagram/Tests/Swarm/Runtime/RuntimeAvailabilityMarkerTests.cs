using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Reflection;

namespace DockerDiagram.Tests;

public sealed class RuntimeAvailabilityMarkerTests
{
    [Fact]
    public async Task SwarmOverlayMembership_DoesNotCallContainerNetworkAttachOrDetach()
    {
        IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, UnexpectedDialogServiceProxy>();
        var sheet = new SheetViewModel(
            "Swarm",
            new ConnectionProfile { RuntimeKind = RuntimeKind.DockerSwarm },
            dockerService,
            dialogService,
            RuntimeKind.DockerSwarm);
        sheet.CreateNodeAt(new DockerContainer { Name = "api", Id = "service-id" }, 0, 0);
        NodeViewModel serviceNode = sheet.Nodes[^1];
        serviceNode.RuntimeKind = RuntimeKind.DockerSwarm;
        serviceNode.ResourceKind = RuntimeResourceKind.SwarmService;

        var overlay = new GroupViewModel(0, 0, 300, 200, dockerService, dialogService, "overlay", GroupType.Network)
        {
            ParentSheet = sheet,
            RuntimeKind = RuntimeKind.DockerSwarm,
            ResourceKind = RuntimeResourceKind.SwarmOverlayNetwork,
            BindingState = RuntimeBindingState.Bound
        };

        await overlay.AddNodeAsync(serviceNode);
        Assert.Contains(serviceNode, overlay.ContainedNodes);
        await overlay.RemoveNodeAsync(serviceNode);
        Assert.DoesNotContain(serviceNode, overlay.ContainedNodes);
    }

    [Fact]
    public void SwarmVolumeDeclaration_IsNotReportedAsDisconnectedManagerVolume()
    {
        IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, UnexpectedDialogServiceProxy>();
        var node = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Volume,
            RuntimeKind = RuntimeKind.DockerSwarm,
            ResourceKind = RuntimeResourceKind.SwarmVolume,
            BindingState = RuntimeBindingState.Bound,
            IsDockerConnected = false
        };

        Assert.False(node.IsDockerDisconnected);
    }

    [Fact]
    public void MarkUnavailable_MarksEverySwarmResourceAndOverlayOffline()
    {
        IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, UnexpectedDialogServiceProxy>();
        var profile = new ConnectionProfile { RuntimeKind = RuntimeKind.DockerSwarm };
        var sheet = new SheetViewModel(
            "Swarm",
            profile,
            dockerService,
            dialogService,
            RuntimeKind.DockerSwarm);

        RuntimeResourceKind[] nodeKinds =
        {
            RuntimeResourceKind.SwarmService,
            RuntimeResourceKind.SwarmVolume,
            RuntimeResourceKind.SwarmExternalTraffic,
            RuntimeResourceKind.SwarmSecret,
            RuntimeResourceKind.SwarmConfig
        };
        foreach (RuntimeResourceKind kind in nodeKinds)
        {
            sheet.CreateNodeAt(new DockerContainer { Name = kind.ToString(), Id = kind.ToString() }, 0, 0);
            NodeViewModel node = sheet.Nodes[^1];
            node.RuntimeKind = RuntimeKind.DockerSwarm;
            node.ResourceKind = kind;
            node.IsDockerConnected = true;
            node.IsRunning = true;
        }

        var overlay = new GroupViewModel(
            0,
            0,
            300,
            200,
            dockerService,
            dialogService,
            "overlay",
            GroupType.Network)
        {
            RuntimeKind = RuntimeKind.DockerSwarm,
            ResourceKind = RuntimeResourceKind.SwarmOverlayNetwork,
            IsDockerConnected = true
        };
        sheet.Groups.Add(overlay);

        RuntimeAvailabilityMarker.MarkUnavailable(sheet, "offline");

        Assert.True(sheet.IsRuntimeUnavailable);
        Assert.Equal("offline", sheet.RuntimeStatusMessage);
        Assert.All(sheet.Nodes, node =>
        {
            Assert.False(node.IsDockerConnected);
            Assert.False(node.IsRunning);
            Assert.Equal("#808080", node.StatusColor);
        });
        Assert.False(overlay.IsDockerConnected);
    }
}
