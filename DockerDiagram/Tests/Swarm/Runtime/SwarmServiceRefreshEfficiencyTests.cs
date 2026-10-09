using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Reflection;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceRefreshEfficiencyTests
{
    [Fact]
    public async Task RefreshService_UsesCombinedTaskSnapshotWithoutSecondNodeQuery()
    {
        IDockerService dockerService = DispatchProxy.Create<IDockerService, RefreshDockerServiceProxy>();
        var tracker = (RefreshDockerServiceProxy)(object)dockerService;
        IDialogService dialogService = DispatchProxy.Create<IDialogService, UnexpectedDialogServiceProxy>();
        var sheet = new SheetViewModel(
            "Swarm",
            new ConnectionProfile { RuntimeKind = RuntimeKind.DockerSwarm },
            dockerService,
            dialogService,
            RuntimeKind.DockerSwarm);
        var service = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Name = "api",
            ContainerId = "service-1",
            Type = NodeType.Container,
            ParentSheet = sheet,
            RuntimeKind = RuntimeKind.DockerSwarm,
            ResourceKind = RuntimeResourceKind.SwarmService,
            BindingState = RuntimeBindingState.Bound,
            IsSwarmService = true
        };
        sheet.Nodes.Add(service);

        await service.RefreshSwarmServiceAsync();

        Assert.Equal(1, tracker.TaskSnapshotCalls);
        Assert.Equal(0, tracker.DirectNodeCalls);
        Assert.Single(service.SwarmTasks);
        Assert.Single(service.SwarmTaskPlacements);
        Assert.Equal("worker-a", service.SwarmTaskPlacements[0].NodeName);
    }
}

public class RefreshDockerServiceProxy : DispatchProxy
{
    public int TaskSnapshotCalls { get; private set; }
    public int DirectNodeCalls { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            nameof(ISwarmService.GetSwarmServicesAsync) => Task.FromResult(new List<DockerContainer>
            {
                new()
                {
                    Id = "service-1",
                    Name = "api",
                    Image = "example/api:1",
                    State = "running",
                    StateColor = "#28a745",
                    SwarmMode = "replicated",
                    SwarmDesiredReplicas = 1,
                    SwarmRunningReplicas = 1
                }
            }),
            nameof(ISwarmService.InspectSwarmServiceRawAsync) => Task.FromResult<object>("{}"),
            nameof(ISwarmService.GetSwarmServiceTaskSnapshotAsync) => CreateTaskSnapshot(),
            nameof(ISwarmService.GetSwarmNodesAsync) => CountUnexpectedNodeQuery(),
            nameof(IDisposable.Dispose) => null,
            _ => throw new NotSupportedException($"Unexpected Docker service call: {targetMethod?.Name}")
        };
    }

    private Task<SwarmServiceTaskSnapshot> CreateTaskSnapshot()
    {
        TaskSnapshotCalls++;
        var node = new DockerSwarmNode
        {
            Id = "node-1",
            Name = "worker-a",
            Hostname = "worker-a",
            Role = "worker",
            Status = "ready"
        };
        var task = new DockerSwarmTask
        {
            Id = "task-1",
            NodeId = node.Id,
            NodeName = node.Hostname,
            DesiredState = "running",
            CurrentState = "running"
        };
        return Task.FromResult(new SwarmServiceTaskSnapshot(new[] { task }, new[] { node }));
    }

    private Task<List<DockerSwarmNode>> CountUnexpectedNodeQuery()
    {
        DirectNodeCalls++;
        return Task.FromResult(new List<DockerSwarmNode>());
    }
}
