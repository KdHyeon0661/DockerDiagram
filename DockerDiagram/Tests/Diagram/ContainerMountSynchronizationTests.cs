using System.Reflection;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class ContainerMountSynchronizationTests
{
    [Fact]
    public void SynchronizationCreatesDistinctVolumeAndHostPathNodesButKeepsTmpfsInternal()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var sheet = new SheetViewModel("Mounts", new ConnectionProfile(), dockerService, dialogService);
        var container = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            Name = "api",
            ContainerId = "container-1",
            ParentSheet = sheet,
            X = 100,
            Y = 100
        };
        sheet.Nodes.Add(container);

        sheet.SynchronizeContainerMounts(
            container,
            [
                new ContainerMountSpec { Kind = MountKind.Volume, Source = "api-data", Target = "/data", VolumeNoCopy = true },
                new ContainerMountSpec { Kind = MountKind.Bind, Source = "C:\\host\\config", Target = "/config", ReadOnly = true, BindPropagation = "rshared" },
                new ContainerMountSpec { Kind = MountKind.Tmpfs, Target = "/run/cache", TmpfsSizeBytes = 1024 }
            ]);

        NodeViewModel volume = Assert.Single(sheet.Nodes, node => node.Type == NodeType.Volume);
        NodeViewModel hostPath = Assert.Single(sheet.Nodes, node => node.Type == NodeType.HostPath);
        Assert.Equal("api-data", volume.EffectiveVolumeName);
        Assert.Equal("C:\\host\\config", hostPath.HostPath);
        Assert.DoesNotContain(sheet.Nodes, node => node.Name.Contains("tmpfs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, container.MountCount);
        Assert.Equal(1, container.TmpfsMountCount);
        Assert.Equal(2, sheet.Connectors.Count(connector => connector.RelationType == RelationType.VolumeMount));

        ConnectorViewModel bindConnector = Assert.Single(sheet.Connectors, connector => connector.MountKind == MountKind.Bind);
        Assert.True(bindConnector.MountReadOnly);
        Assert.Contains("rshared", bindConnector.MountOptions, StringComparison.Ordinal);
    }

    [Fact]
    public void ResynchronizationRemovesStaleBindConnectorAndOrphanHostPathNode()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var sheet = new SheetViewModel("Mounts", new ConnectionProfile(), dockerService, dialogService);
        var container = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            Name = "api",
            ParentSheet = sheet
        };
        sheet.Nodes.Add(container);
        sheet.SynchronizeContainerMounts(
            container,
            [new ContainerMountSpec { Kind = MountKind.Bind, Source = "/host/config", Target = "/config" }]);

        sheet.SynchronizeContainerMounts(
            container,
            [new ContainerMountSpec { Kind = MountKind.Volume, Source = "api-data", Target = "/data" }]);

        Assert.DoesNotContain(sheet.Nodes, node => node.Type == NodeType.HostPath);
        Assert.Single(sheet.Nodes, node => node.Type == NodeType.Volume);
        ConnectorViewModel connector = Assert.Single(sheet.Connectors);
        Assert.Equal(MountKind.Volume, connector.MountKind);
        Assert.Equal("/data", connector.MountPath);
        Assert.Single(container.MountSpecifications);
    }

    [Fact]
    public void SynchronizationDoesNotChangeOwnershipOfExistingNamedVolume()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var sheet = new SheetViewModel("Mounts", new ConnectionProfile(), dockerService, dialogService);
        var volume = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Volume,
            Name = "shared-data",
            DockerVolumeName = "shared-data",
            VolumeExternal = false,
            ParentSheet = sheet
        };
        var container = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            Name = "worker",
            ParentSheet = sheet
        };
        sheet.Nodes.Add(volume);
        sheet.Nodes.Add(container);

        sheet.SynchronizeContainerMounts(
            container,
            [new ContainerMountSpec { Kind = MountKind.Volume, Source = "shared-data", Target = "/data" }]);

        Assert.False(volume.VolumeExternal);
        Assert.Same(volume, Assert.Single(sheet.Nodes, node => node.Type == NodeType.Volume));
    }

    [Fact]
    public void ComposeLayoutMovesHostPathAwayFromItsContainer()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var sheet = new SheetViewModel("Mounts", new ConnectionProfile(), dockerService, dialogService);
        var container = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            Name = "api",
            ParentSheet = sheet,
            X = 100,
            Y = 100
        };
        sheet.Nodes.Add(container);
        sheet.SynchronizeContainerMounts(
            container,
            [new ContainerMountSpec { Kind = MountKind.Bind, Source = "C:\\host\\config", Target = "/config" }]);
        NodeViewModel hostPath = Assert.Single(sheet.Nodes, node => node.Type == NodeType.HostPath);
        hostPath.X = container.X;
        hostPath.Y = container.Y;

        ComposeDiagramLayoutService.Arrange(
            sheet,
            new Dictionary<string, NodeViewModel> { ["api"] = container },
            new Dictionary<string, List<string>> { ["api"] = [] },
            originX: 100,
            originY: 100);

        bool overlaps = container.X < hostPath.X + hostPath.Width &&
                        container.X + container.Width > hostPath.X &&
                        container.Y < hostPath.Y + hostPath.Height &&
                        container.Y + container.Height > hostPath.Y;
        Assert.False(overlaps);
    }
}
