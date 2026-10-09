using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceLifecycleTests
{
    [Theory]
    [InlineData(RuntimeBindingState.Draft, false, false, true, false, SwarmServiceLifecycleState.Draft)]
    [InlineData(RuntimeBindingState.Applying, true, false, true, false, SwarmServiceLifecycleState.Creating)]
    [InlineData(RuntimeBindingState.Error, false, true, true, false, SwarmServiceLifecycleState.Failed)]
    [InlineData(RuntimeBindingState.Bound, false, false, false, true, SwarmServiceLifecycleState.Offline)]
    [InlineData(RuntimeBindingState.Missing, false, false, true, false, SwarmServiceLifecycleState.Offline)]
    [InlineData(RuntimeBindingState.Bound, false, false, true, true, SwarmServiceLifecycleState.Bound)]
    public void Resolve_MapsExistingRuntimeState(
        RuntimeBindingState bindingState,
        bool isCreating,
        bool isCreationFailed,
        bool isRuntimeAvailable,
        bool isBoundToEngine,
        SwarmServiceLifecycleState expected)
    {
        SwarmServiceLifecycleState actual = SwarmServiceLifecycle.Resolve(
            bindingState,
            isCreating,
            isCreationFailed,
            isRuntimeAvailable,
            isBoundToEngine);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(SwarmServiceLifecycleState.Draft, SwarmServiceLifecycleState.Creating)]
    [InlineData(SwarmServiceLifecycleState.Creating, SwarmServiceLifecycleState.Bound)]
    [InlineData(SwarmServiceLifecycleState.Creating, SwarmServiceLifecycleState.Failed)]
    [InlineData(SwarmServiceLifecycleState.Failed, SwarmServiceLifecycleState.Creating)]
    [InlineData(SwarmServiceLifecycleState.Bound, SwarmServiceLifecycleState.Offline)]
    [InlineData(SwarmServiceLifecycleState.Offline, SwarmServiceLifecycleState.Bound)]
    public void CanTransition_AllowsSupportedTransitions(
        SwarmServiceLifecycleState current,
        SwarmServiceLifecycleState next)
    {
        Assert.True(SwarmServiceLifecycle.CanTransition(current, next));
    }

    [Fact]
    public void CanTransition_DoesNotSkipCreationFromDraftToBound()
    {
        Assert.False(SwarmServiceLifecycle.CanTransition(
            SwarmServiceLifecycleState.Draft,
            SwarmServiceLifecycleState.Bound));
    }
}
