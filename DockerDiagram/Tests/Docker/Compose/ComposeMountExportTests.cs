using System.Reflection;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class ComposeMountExportTests
{
    [Fact]
    public void ExportReplacesImportedVolumeAndTmpfsKeysWithCurrentMountState()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var sheet = new SheetViewModel("Compose", new ConnectionProfile(), dockerService, dialogService)
        {
            ComposeRawYaml = "name: preserved-project\nservices: {}\n"
        };
        var container = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            Name = "app",
            ImageName = "alpine:latest",
            ComposeServiceName = "app",
            ComposeRawServiceYaml = "image: alpine:old\nvolumes:\n  - old-data:/old\ntmpfs:\n  - /old-cache\nx-preserved: keep-me\n",
            ParentSheet = sheet
        };
        sheet.Nodes.Add(container);
        sheet.SynchronizeContainerMounts(
            container,
            [
                new ContainerMountSpec { Kind = MountKind.Volume, Source = "current-data", Target = "/data" },
                new ContainerMountSpec { Kind = MountKind.Tmpfs, Target = "/cache", TmpfsSizeBytes = 4096 }
            ]);

        Dictionary<object, object> service = ExportService(sheet, "app");
        List<VolumeMountInfo> mounts = ComposeYamlHelper.ToVolumeMounts(ComposeYamlHelper.GetValue(service, "volumes"));

        Assert.Null(ComposeYamlHelper.GetValue(service, "tmpfs"));
        Assert.Equal("keep-me", ComposeYamlHelper.GetValue(service, "x-preserved")?.ToString());
        Assert.Collection(
            mounts.OrderBy(mount => mount.Target, StringComparer.Ordinal),
            tmpfs =>
            {
                Assert.Equal(MountKind.Tmpfs, tmpfs.Kind);
                Assert.Equal("/cache", tmpfs.Target);
            },
            volume =>
            {
                Assert.Equal(MountKind.Volume, volume.Kind);
                Assert.Equal("current-data", volume.Source);
                Assert.Equal("/data", volume.Target);
            });
        Assert.DoesNotContain(mounts, mount => mount.Target is "/old" or "/old-cache");
    }

    [Fact]
    public void ExportRemovesImportedMountKeysWhenCurrentContainerHasNoMounts()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var sheet = new SheetViewModel("Compose", new ConnectionProfile(), dockerService, dialogService);
        var container = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            Name = "app",
            ImageName = "alpine:latest",
            ComposeServiceName = "app",
            ComposeRawServiceYaml = "volumes:\n  - old-data:/old\ntmpfs:\n  - /old-cache\n",
            ParentSheet = sheet
        };
        sheet.Nodes.Add(container);

        Dictionary<object, object> service = ExportService(sheet, "app");

        Assert.Null(ComposeYamlHelper.GetValue(service, "volumes"));
        Assert.Null(ComposeYamlHelper.GetValue(service, "tmpfs"));
    }

    [Fact]
    public void ExportAddsNewNetworkDefinitionWhenRawComposeHasNoNetworksMap()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var sheet = new SheetViewModel("Compose", new ConnectionProfile(), dockerService, dialogService)
        {
            ComposeRawYaml = "services: {}\n"
        };
        var network = new GroupViewModel(
            100,
            100,
            300,
            200,
            dockerService,
            dialogService,
            "frontend",
            GroupType.Network)
        {
            Driver = "bridge"
        };
        sheet.AddGroup(network);

        string yaml = GenerateYaml(sheet);
        Dictionary<object, object> root = ComposeYamlHelper.ParseMapping(yaml)!;

        Assert.NotNull(ComposeYamlHelper.GetNetworkMap(root, "frontend"));
    }

    [Fact]
    public void ExportRemovesTopLevelDefinitionsThatWereDeletedFromDiagram()
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var sheet = new SheetViewModel("Compose", new ConnectionProfile(), dockerService, dialogService)
        {
            ComposeRawYaml = "services: {}\nvolumes:\n  old-data: {}\nnetworks:\n  old-net: {}\nx-preserved: keep-me\n"
        };

        string yaml = GenerateYaml(sheet);
        Dictionary<object, object> root = ComposeYamlHelper.ParseMapping(yaml)!;

        Assert.Null(ComposeYamlHelper.GetValue(root, "volumes"));
        Assert.Null(ComposeYamlHelper.GetValue(root, "networks"));
        Assert.Equal("keep-me", ComposeYamlHelper.GetValue(root, "x-preserved")?.ToString());
    }

    private static Dictionary<object, object> ExportService(SheetViewModel sheet, string serviceName)
    {
        string yaml = GenerateYaml(sheet);
        Dictionary<object, object> root = ComposeYamlHelper.ParseMapping(yaml)!;
        return ComposeYamlHelper.GetServiceMap(root, serviceName)!;
    }

    private static string GenerateYaml(SheetViewModel sheet)
    {
        MethodInfo generateYaml = typeof(ComposeExportService).GetMethod(
            "GenerateYaml",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)generateYaml.Invoke(null, [sheet])!;
    }
}
