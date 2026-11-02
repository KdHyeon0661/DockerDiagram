using System.Reflection;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class NodeLifetimeTests
{
    [Fact]
    public void RemovingSelectedContainerDetachesAndStopsMonitoring()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ => dockerService);
        using var main = new MainViewModel(dockerService, dialogService, factory);
        SheetViewModel sheet = main.ActiveSheet!;
        var node = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            ContainerId = "container-1",
            Name = "web"
        };

        sheet.Nodes.Add(node);
        node.IsSelected = true;

        Assert.True(node.IsMonitoring);

        sheet.Nodes.Remove(node);

        Assert.Null(node.ParentSheet);
        Assert.False(node.IsSelected);
        Assert.False(node.IsMonitoring);
    }

    [Fact]
    public void DeletingSheetDetachesItsNodesAndStopsMonitoring()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ => dockerService);
        using var main = new MainViewModel(dockerService, dialogService, factory);
        main.SheetManager.AddSheet();
        SheetViewModel sheet = main.ActiveSheet!;
        var node = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            ContainerId = "container-2",
            Name = "worker"
        };
        sheet.Nodes.Add(node);
        node.IsSelected = true;

        main.SheetManager.DeleteSheet(sheet);

        Assert.DoesNotContain(sheet, main.AllSheets);
        Assert.Null(node.ParentSheet);
        Assert.False(node.IsSelected);
        Assert.False(node.IsMonitoring);
    }
}
