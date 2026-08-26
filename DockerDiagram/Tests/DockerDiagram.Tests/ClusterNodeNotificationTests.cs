using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class ClusterNodeNotificationTests
{
    [Fact]
    public void ClusterNodeSnapshotRaisesDisplayPropertyNotifications()
    {
        var swarmNode = new DockerSwarmNode { Id = "swarm", Role = "worker" };
        var swarmChanges = new HashSet<string?>();
        swarmNode.PropertyChanged += (_, args) => swarmChanges.Add(args.PropertyName);

        DockerResourceCollectionSynchronizer.ApplySnapshot(
            swarmNode,
            new DockerSwarmNode
            {
                Id = "swarm",
                Hostname = "manager-1",
                Role = "manager",
                ManagerStatus = "leader",
                Status = "ready"
            });

        Assert.Contains(nameof(DockerSwarmNode.Hostname), swarmChanges);
        Assert.Contains(nameof(DockerSwarmNode.Role), swarmChanges);
        Assert.Contains(nameof(DockerSwarmNode.RoleLabel), swarmChanges);
        Assert.Contains(nameof(DockerSwarmNode.Status), swarmChanges);

        var kubernetesNode = new DockerKubernetesNode { Id = "kube", Role = "worker" };
        var kubernetesChanges = new HashSet<string?>();
        kubernetesNode.PropertyChanged += (_, args) => kubernetesChanges.Add(args.PropertyName);

        DockerResourceCollectionSynchronizer.ApplySnapshot(
            kubernetesNode,
            new DockerKubernetesNode
            {
                Id = "kube",
                Role = "control-plane",
                Status = "Ready",
                Version = "v1.34",
                InternalIp = "10.0.0.2"
            });

        Assert.Contains(nameof(DockerKubernetesNode.Role), kubernetesChanges);
        Assert.Contains(nameof(DockerKubernetesNode.RoleLabel), kubernetesChanges);
        Assert.Contains(nameof(DockerKubernetesNode.Status), kubernetesChanges);
        Assert.Contains(nameof(DockerKubernetesNode.Version), kubernetesChanges);
        Assert.Contains(nameof(DockerKubernetesNode.InternalIp), kubernetesChanges);
    }
}
