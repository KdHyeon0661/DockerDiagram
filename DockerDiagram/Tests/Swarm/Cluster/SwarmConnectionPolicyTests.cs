using DockerDiagram.Diagram;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmConnectionPolicyTests
{
    [Theory]
    [InlineData(RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmVolume, RelationType.VolumeMount, false)]
    [InlineData(RuntimeResourceKind.SwarmVolume, RuntimeResourceKind.SwarmService, RelationType.VolumeMount, true)]
    [InlineData(RuntimeResourceKind.SwarmExternalTraffic, RuntimeResourceKind.SwarmService, RelationType.SwarmPublishedPort, false)]
    [InlineData(RuntimeResourceKind.SwarmSecret, RuntimeResourceKind.SwarmService, RelationType.SwarmSecretReference, true)]
    [InlineData(RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmConfig, RelationType.SwarmConfigReference, false)]
    public void Resolve_AllowsSupportedPairs(
        RuntimeResourceKind source,
        RuntimeResourceKind target,
        RelationType relation,
        bool reverse)
    {
        SwarmConnectionDecision decision = SwarmConnectionPolicy.Resolve(source, target);

        Assert.True(decision.IsAllowed);
        Assert.Equal(relation, decision.RelationType);
        Assert.Equal(reverse, decision.ReverseDirection);
    }

    [Fact]
    public void Resolve_DeniesOverlayNetworkConnectors()
    {
        SwarmConnectionDecision decision = SwarmConnectionPolicy.Resolve(
            RuntimeResourceKind.SwarmService,
            RuntimeResourceKind.SwarmOverlayNetwork);

        Assert.False(decision.IsAllowed);
        Assert.NotEmpty(decision.ErrorMessage);
    }

    [Theory]
    [InlineData(RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmService)]
    [InlineData(RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmVisualGroup)]
    public void Resolve_DeniesRelationshipsThatCannotBeAppliedToServiceSpec(
        RuntimeResourceKind source,
        RuntimeResourceKind target)
    {
        SwarmConnectionDecision decision = SwarmConnectionPolicy.Resolve(source, target);

        Assert.False(decision.IsAllowed);
        Assert.NotEmpty(decision.ErrorMessage);
    }
}
