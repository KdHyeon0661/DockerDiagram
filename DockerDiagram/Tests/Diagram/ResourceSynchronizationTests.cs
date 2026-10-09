using System.Collections.ObjectModel;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class ResourceSynchronizationTests
{
    [Fact]
    public void ContainerSnapshotUpdatesExistingObjectWithoutReplacingIt()
    {
        var existing = new DockerContainer
        {
            Id = "container-1",
            Name = "old-name",
            Image = "nginx:old",
            State = "exited",
            StateColor = "#FF0000",
            Ports = "80/tcp"
        };
        var latest = new DockerContainer
        {
            Id = "container-1",
            Name = "new-name",
            Image = "nginx:new",
            State = "running",
            StateColor = "#00FF00",
            Ports = "18080->80/tcp",
            ComposeProjectName = "web",
            ComposeResourceName = "frontend",
            ProjectSource = "Compose",
            Labels = new Dictionary<string, string> { ["tier"] = "frontend" },
            KubernetesKind = "Deployment",
            KubernetesReadyReplicas = 2
        };
        var collection = new ObservableCollection<DockerContainer> { existing };

        DockerResourceCollectionSynchronizer.Sync(collection, new[] { latest }, item => item.Id);

        Assert.Same(existing, collection[0]);
        Assert.Equal("new-name", existing.Name);
        Assert.Equal("nginx:new", existing.Image);
        Assert.Equal("running", existing.State);
        Assert.Equal("#00FF00", existing.StateColor);
        Assert.Equal("18080->80/tcp", existing.Ports);
        Assert.Equal("web", existing.ComposeProjectName);
        Assert.Equal("frontend", existing.ComposeResourceName);
        Assert.Equal("Compose", existing.ProjectSource);
        Assert.Equal("frontend", existing.Labels["tier"]);
        Assert.Equal("Deployment", existing.KubernetesKind);
        Assert.Equal(2, existing.KubernetesReadyReplicas);
    }

    [Fact]
    public void SynchronizationRemovesMissingItemsAndAddsNewItems()
    {
        var removed = new DockerVolume { Id = "old", Name = "old" };
        var kept = new DockerVolume { Id = "keep", Name = "keep" };
        var latestKept = new DockerVolume { Id = "keep", Name = "keep", ProjectSource = "Compose" };
        var added = new DockerVolume { Id = "new", Name = "new" };
        var collection = new ObservableCollection<DockerVolume> { removed, kept };

        DockerResourceCollectionSynchronizer.Sync(collection, new[] { latestKept, added }, item => item.Id);

        Assert.DoesNotContain(removed, collection);
        Assert.Same(kept, collection[0]);
        Assert.Same(added, collection[1]);
        Assert.Equal("Compose", kept.ProjectSource);
    }

    [Fact]
    public void TypeSpecificSnapshotsUpdateNetworkImageAndClusterNodes()
    {
        var network = new DockerNetworkGroup { Id = "network", Name = "network", Driver = "bridge" };
        DockerResourceCollectionSynchronizer.ApplySnapshot(
            network,
            new DockerNetworkGroup { Id = "network", Name = "renamed", Driver = "overlay" });
        Assert.Equal("renamed", network.Name);
        Assert.Equal("overlay", network.Driver);

        var image = new DockerImage { Id = "nginx:old", Repository = "nginx", Tag = "old", Size = 1 };
        DockerResourceCollectionSynchronizer.ApplySnapshot(
            image,
            new DockerImage { Id = "nginx:new", Repository = "nginx", Tag = "new", Size = 42 });
        Assert.Equal("nginx:new", image.Id);
        Assert.Equal("new", image.Tag);
        Assert.Equal(42, image.Size);

        var swarmNode = new DockerSwarmNode { Id = "swarm", Role = "worker" };
        DockerResourceCollectionSynchronizer.ApplySnapshot(
            swarmNode,
            new DockerSwarmNode { Id = "swarm", Hostname = "manager-1", Role = "manager", ManagerStatus = "leader" });
        Assert.Equal("manager-1", swarmNode.Hostname);
        Assert.Equal("manager / leader", swarmNode.RoleLabel);

        var kubernetesNode = new DockerKubernetesNode { Id = "kube", Status = "NotReady" };
        DockerResourceCollectionSynchronizer.ApplySnapshot(
            kubernetesNode,
            new DockerKubernetesNode { Id = "kube", Role = "control-plane", Status = "Ready", InternalIp = "10.0.0.2" });
        Assert.Equal("Ready", kubernetesNode.Status);
        Assert.Equal("10.0.0.2", kubernetesNode.InternalIp);
    }
}
