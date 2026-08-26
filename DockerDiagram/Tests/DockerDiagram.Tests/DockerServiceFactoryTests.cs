using System.Reflection;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerServiceFactoryTests
{
    [Fact]
    public void CreateAndRelease_DisposesRegisteredServiceOnce()
    {
        IDockerService service = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        var tracker = (TrackingDockerServiceProxy)(object)service;
        using var factory = new DockerServiceFactory(_ => service);

        IDockerService created = factory.Create(new ConnectionProfile());

        Assert.Same(service, created);
        Assert.True(factory.Release(created));
        Assert.Equal(1, tracker.DisposeCount);
        Assert.False(factory.Release(created));
        Assert.Equal(1, tracker.DisposeCount);
    }

    [Fact]
    public void RegisterAndReleaseAll_DisposesDuplicateRegistrationOnce()
    {
        IDockerService service = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        var tracker = (TrackingDockerServiceProxy)(object)service;
        using var factory = new DockerServiceFactory(_ => service);

        factory.Register(service);
        factory.Register(service);
        factory.ReleaseAll();

        Assert.Equal(1, tracker.DisposeCount);
        Assert.False(factory.Release(service));
    }

    [Fact]
    public void Create_WhenRegistrationIsRejected_DisposesNewService()
    {
        IDockerService service = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        var tracker = (TrackingDockerServiceProxy)(object)service;
        DockerServiceFactory? factory = null;
        factory = new DockerServiceFactory(_ =>
        {
            factory!.Dispose();
            return service;
        });

        Assert.Throws<ObjectDisposedException>(() => factory!.Create(new ConnectionProfile()));
        Assert.Equal(1, tracker.DisposeCount);
    }
}

public class TrackingDockerServiceProxy : DispatchProxy
{
    public int DisposeCount { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDisposable.Dispose))
        {
            DisposeCount++;
            return null;
        }

        throw new NotSupportedException($"Unexpected Docker service call: {targetMethod?.Name}");
    }
}
