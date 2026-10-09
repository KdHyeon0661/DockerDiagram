using System.Reflection;
using Docker.DotNet.Models;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class VolumeReplacementTests
{
    [Fact]
    public async Task ConnectVolume_UsesEffectiveDockerNameAndPreservesInspectConfiguration()
    {
        IDockerService service = DispatchProxy.Create<IDockerService, VolumeReplacementDockerProxy>();
        var docker = (VolumeReplacementDockerProxy)(object)service;
        IDialogService dialog = DispatchProxy.Create<IDialogService, MountDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, VolumeReplacementDockerProxy>());
        using var main = new MainViewModel(service, dialog, factory);
        NodeViewModel container = CreateContainer(main.ActiveSheet!, service, dialog);
        NodeViewModel volume = CreateVolume(main.ActiveSheet!, service, dialog);

        bool success = await main.ConnectVolumeToContainerAsync(container, volume);

        Assert.True(success);
        Assert.Equal(1, docker.RecreateCount);
        Assert.Contains("actual-volume:/data", docker.LastBinds);
        Assert.DoesNotContain("display-volume:/data", docker.LastBinds);
        Assert.Equal("replacement-1", container.ContainerId);
    }

    [Fact]
    public async Task ConnectVolume_CopyFailureRecreatesOriginalContainer()
    {
        IDockerService service = DispatchProxy.Create<IDockerService, VolumeReplacementDockerProxy>();
        var docker = (VolumeReplacementDockerProxy)(object)service;
        docker.FailCopyTo = true;
        IDialogService dialog = DispatchProxy.Create<IDialogService, MountDialogServiceProxy>();
        using var factory = new DockerServiceFactory(_ =>
            DispatchProxy.Create<IDockerService, VolumeReplacementDockerProxy>());
        using var main = new MainViewModel(service, dialog, factory);
        NodeViewModel container = CreateContainer(main.ActiveSheet!, service, dialog);
        NodeViewModel volume = CreateVolume(main.ActiveSheet!, service, dialog);

        bool success = await main.ConnectVolumeToContainerAsync(container, volume);

        Assert.False(success);
        Assert.Equal(2, docker.RecreateCount);
        Assert.Equal("replacement-2", container.ContainerId);
        Assert.Equal(new[] { "existing:/existing" }, docker.LastBinds);
    }

    private static NodeViewModel CreateContainer(
        SheetViewModel sheet,
        IDockerService service,
        IDialogService dialog)
    {
        var node = new NodeViewModel(service, service, dialog)
        {
            Name = "web",
            Type = NodeType.Container,
            ContainerId = "original-id",
            ParentSheet = sheet
        };
        sheet.Nodes.Add(node);
        return node;
    }

    private static NodeViewModel CreateVolume(
        SheetViewModel sheet,
        IDockerService service,
        IDialogService dialog)
    {
        var node = new NodeViewModel(service, service, dialog)
        {
            Name = "display-volume",
            DockerVolumeName = "actual-volume",
            Type = NodeType.Volume,
            ParentSheet = sheet
        };
        sheet.Nodes.Add(node);
        return node;
    }
}

public class MountDialogServiceProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDialogService.TryShowMountDialog))
        {
            args![0] = "/data";
            args[1] = string.Empty;
            return true;
        }

        Type returnType = targetMethod?.ReturnType ?? typeof(void);
        if (returnType == typeof(void)) return null;
        if (returnType == typeof(bool)) return true;
        if (returnType == typeof(DialogChoice)) return DialogChoice.Yes;
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

public class VolumeReplacementDockerProxy : DispatchProxy
{
    public bool FailCopyTo { get; set; }
    public int RecreateCount { get; private set; }
    public IReadOnlyList<string> LastBinds { get; private set; } = Array.Empty<string>();

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
            Env = new List<string> { "MODE=prod" }
        },
        HostConfig = new HostConfig
        {
            Binds = new List<string> { "existing:/existing" },
            Memory = 134217728,
            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
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
            case nameof(IContainerService.CopyFromContainerAsync):
            case nameof(IContainerService.RemoveContainerAsync):
            case nameof(IContainerService.StopContainerAsync):
            case nameof(IContainerService.StartContainerAsync):
                return Task.CompletedTask;
            case nameof(IContainerService.CopyToContainerAsync):
                return FailCopyTo
                    ? Task.FromException(new InvalidOperationException("copy failed"))
                    : Task.CompletedTask;
            case nameof(IContainerService.RecreateContainerFromInspectAsync):
                RecreateCount++;
                LastBinds = ((IList<string>)args![2]!).ToList();
                return Task.FromResult($"replacement-{RecreateCount}");
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
