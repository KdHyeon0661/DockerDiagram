using System.Reflection;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceCreationPreflightTests
{
    [Fact]
    public async Task MissingIngressWhenRepairIsDeclinedDoesNotCreateNodeOrService()
    {
        using ISwarmCreationTestService service = DispatchProxy.Create<ISwarmCreationTestService, SwarmCreationDockerServiceProxy>();
        var docker = (SwarmCreationDockerServiceProxy)(object)service;
        IDialogService dialog = DispatchProxy.Create<IDialogService, DeclineRepairDialogProxy>();
        using DockerServiceFactory factory = CreateDockerServiceFactory();
        using MainViewModel main = CreateSwarmMainViewModel(service, dialog, factory);

        await main.CreateSwarmServiceNodeAsync(CreateOptions(SwarmPublishMode.Ingress), 100, 100);

        Assert.Equal(1, docker.NetworkQueryCount);
        Assert.Equal(0, docker.NetworkCreateCount);
        Assert.Equal(0, docker.ServiceCreateCount);
        Assert.Empty(main.ActiveSheet!.Nodes);
    }

    [Fact]
    public async Task MissingIngressWhenRepairIsConfirmedRepairsBeforeCreatingService()
    {
        using ISwarmCreationTestService service = DispatchProxy.Create<ISwarmCreationTestService, SwarmCreationDockerServiceProxy>();
        var docker = (SwarmCreationDockerServiceProxy)(object)service;
        docker.FailServiceCreation = true;
        IDialogService dialog = DispatchProxy.Create<IDialogService, ConfirmRepairDialogProxy>();
        using DockerServiceFactory factory = CreateDockerServiceFactory();
        using MainViewModel main = CreateSwarmMainViewModel(service, dialog, factory);

        await main.CreateSwarmServiceNodeAsync(CreateOptions(SwarmPublishMode.Ingress), 100, 100);

        Assert.Equal(2, docker.NetworkQueryCount);
        Assert.Equal(1, docker.NetworkCreateCount);
        Assert.True(docker.LastNetworkOptions?.Ingress);
        Assert.Equal(1, docker.ServiceCreateCount);
        NodeViewModel failedNode = Assert.Single(main.ActiveSheet!.Nodes);
        Assert.True(failedNode.IsCreationFailed);
        Assert.Same(main.ActiveSheet, failedNode.ParentSheet);
    }

    [Fact]
    public async Task HostPublishingDoesNotRequireOrRepairIngress()
    {
        using ISwarmCreationTestService service = DispatchProxy.Create<ISwarmCreationTestService, SwarmCreationDockerServiceProxy>();
        var docker = (SwarmCreationDockerServiceProxy)(object)service;
        docker.FailServiceCreation = true;
        IDialogService dialog = DispatchProxy.Create<IDialogService, DeclineRepairDialogProxy>();
        using DockerServiceFactory factory = CreateDockerServiceFactory();
        using MainViewModel main = CreateSwarmMainViewModel(service, dialog, factory);

        await main.CreateSwarmServiceNodeAsync(CreateOptions(SwarmPublishMode.Host), 100, 100);

        Assert.Equal(0, docker.NetworkQueryCount);
        Assert.Equal(0, docker.NetworkCreateCount);
        Assert.Equal(1, docker.ServiceCreateCount);
    }

    [Fact]
    public async Task RetryFailedServiceCreationReusesTheFailedNode()
    {
        using ISwarmCreationTestService service = DispatchProxy.Create<ISwarmCreationTestService, SwarmCreationDockerServiceProxy>();
        var docker = (SwarmCreationDockerServiceProxy)(object)service;
        docker.FailServiceCreation = true;
        IDialogService dialog = DispatchProxy.Create<IDialogService, DeclineRepairDialogProxy>();
        var dialogProxy = (DeclineRepairDialogProxy)(object)dialog;
        using DockerServiceFactory factory = CreateDockerServiceFactory();
        using MainViewModel main = CreateSwarmMainViewModel(service, dialog, factory);

        await main.CreateSwarmServiceNodeAsync(CreateOptions(SwarmPublishMode.Host), 100, 100);
        NodeViewModel failedNode = Assert.Single(main.ActiveSheet!.Nodes);
        Assert.True(failedNode.IsCreationFailed);
        Assert.Same(service, main.ActiveSheet.DockerService);
        Assert.IsAssignableFrom<ISwarmServiceMutationService>(main.ActiveSheet.DockerService);

        docker.FailServiceCreation = false;
        var retryCommand = Assert.IsType<AsyncRelayCommand>(failedNode.RetryFailedCreationCommand);
        await retryCommand.ExecuteAsync(null);

        Assert.Same(service, main.ActiveSheet.DockerService);
        Assert.True(
            docker.ServiceCreateCount == 2,
            $"Expected retry to call service creation twice. Last dialog error: {dialogProxy.LastError}");
        Assert.Same(failedNode, Assert.Single(main.ActiveSheet.Nodes));
        Assert.False(failedNode.IsCreationFailed);
        Assert.Equal(RuntimeBindingState.Bound, failedNode.BindingState);
    }

    [Fact]
    public async Task RetryFailedSecretCreationReusesTheFailedNode()
    {
        using ISwarmCreationTestService service = DispatchProxy.Create<ISwarmCreationTestService, SwarmCreationDockerServiceProxy>();
        var docker = (SwarmCreationDockerServiceProxy)(object)service;
        docker.FailDataResourceCreation = true;
        IDialogService dialog = DispatchProxy.Create<IDialogService, DeclineRepairDialogProxy>();
        using DockerServiceFactory factory = CreateDockerServiceFactory();
        using MainViewModel main = CreateSwarmMainViewModel(service, dialog, factory);

        var options = new SwarmDataResourceCreateOptions
        {
            Kind = SwarmDataResourceKind.Secret,
            Name = "api-token",
            Data = "secret-value"
        };
        await main.CreateSwarmDataResourceNodeAsync(options, 100, 100);
        NodeViewModel failedNode = Assert.Single(main.ActiveSheet!.Nodes);
        Assert.True(failedNode.IsCreationFailed);

        docker.FailDataResourceCreation = false;
        var retryCommand = Assert.IsType<AsyncRelayCommand>(failedNode.RetryFailedCreationCommand);
        await retryCommand.ExecuteAsync(null);

        Assert.Equal(2, docker.DataResourceCreateCount);
        Assert.Same(failedNode, Assert.Single(main.ActiveSheet.Nodes));
        Assert.False(failedNode.IsCreationFailed);
        Assert.Equal(RuntimeBindingState.Bound, failedNode.BindingState);
        Assert.Equal("secret-1", failedNode.ContainerId);
    }

    private static MainViewModel CreateSwarmMainViewModel(
        IDockerService service,
        IDialogService dialog,
        IDockerServiceFactory factory)
    {
        var main = new MainViewModel(service, dialog, factory);
        ConnectionWorkspaceViewModel source = main.SheetManager.ActiveWorkspace!;
        main.SheetManager.CreateRuntimeWorkspace(source, RuntimeKind.DockerSwarm);
        return main;
    }

    private static DockerServiceFactory CreateDockerServiceFactory() =>
        new(_ => DispatchProxy.Create<ISwarmCreationTestService, SwarmCreationDockerServiceProxy>());

    private static SwarmServiceCreateOptions CreateOptions(SwarmPublishMode publishMode) => new()
    {
        Spec = new SwarmServiceSpecOptions
        {
            Name = "web",
            Image = "nginx:alpine",
            Replicas = 1,
            PublishedPorts = new[]
            {
                new SwarmPublishedPortOptions(80, 8080, SwarmPortProtocol.Tcp, publishMode)
            }
        }
    };

}

public interface ISwarmCreationTestService :
    IDockerService,
    ISwarmServiceMutationService,
    ISwarmDataResourceMutationService
{
}

public class SwarmCreationDockerServiceProxy : DispatchProxy
{
    private readonly List<DockerNetworkGroup> _networks = new();

    public int NetworkQueryCount { get; private set; }
    public int NetworkCreateCount { get; private set; }
    public int ServiceCreateCount { get; private set; }
    public bool FailServiceCreation { get; set; }
    public bool FailDataResourceCreation { get; set; }
    public NetworkCreateOptions? LastNetworkOptions { get; private set; }
    public int DataResourceCreateCount { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(INetworkService.GetNetworksAsync):
                NetworkQueryCount++;
                return Task.FromResult(_networks.ToList());
            case nameof(INetworkService.CreateNetworkAsync) when args?.Length == 1 && args[0] is NetworkCreateOptions options:
                NetworkCreateCount++;
                LastNetworkOptions = options;
                _networks.Add(new DockerNetworkGroup
                {
                    Id = "ingress-1",
                    Name = options.Name,
                    Driver = options.Driver,
                    Scope = "swarm",
                    Ingress = options.Ingress
                });
                return Task.FromResult("ingress-1");
            case nameof(ISwarmServiceMutationService.CreateSwarmServiceAsync):
                ServiceCreateCount++;
                return FailServiceCreation
                    ? Task.FromException<SwarmServiceMutationResult>(new InvalidOperationException("planned failure"))
                    : Task.FromResult(SwarmServiceMutationResult.Create("service-1"));
            case nameof(ISwarmDataResourceMutationService.CreateSwarmDataResourceAsync):
                DataResourceCreateCount++;
                var dataOptions = (SwarmDataResourceCreateOptions)args![0]!;
                return FailDataResourceCreation
                    ? Task.FromException<SwarmDataResourceMutationResult>(new InvalidOperationException("planned failure"))
                    : Task.FromResult(new SwarmDataResourceMutationResult(
                        "secret-1",
                        dataOptions.Kind,
                        dataOptions.Name));
            case nameof(IDisposable.Dispose):
                return null;
            default:
                throw new NotSupportedException($"Unexpected Docker service call: {targetMethod?.Name}");
        }
    }
}

public class DeclineRepairDialogProxy : DispatchProxy
{
    public string LastError { get; private set; } = string.Empty;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDialogService.ShowError))
            LastError = args?[0]?.ToString() ?? string.Empty;
        Type returnType = targetMethod?.ReturnType ?? typeof(void);
        if (returnType == typeof(void)) return null;
        if (returnType == typeof(bool)) return false;
        if (returnType == typeof(DialogChoice)) return DialogChoice.Cancel;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

public class ConfirmRepairDialogProxy : DeclineRepairDialogProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDialogService.ShowConfirm))
            return true;
        return base.Invoke(targetMethod, args);
    }
}
