using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmClusterStateTests
{
    [Theory]
    [InlineData("inactive", false, SwarmMembershipState.Inactive)]
    [InlineData("pending", false, SwarmMembershipState.Pending)]
    [InlineData("active", true, SwarmMembershipState.Manager)]
    [InlineData("active", false, SwarmMembershipState.Worker)]
    [InlineData("locked", false, SwarmMembershipState.Locked)]
    [InlineData("error", false, SwarmMembershipState.Error)]
    [InlineData("unexpected", false, SwarmMembershipState.Unknown)]
    public void Create_MapsDockerStateAndControlFlag(
        string localState,
        bool controlAvailable,
        SwarmMembershipState expected)
    {
        SwarmClusterState state = SwarmClusterState.Create(localState, controlAvailable);

        Assert.Equal(expected, state.Membership);
    }

    [Fact]
    public void Create_UsesErrorMembershipWhenDockerReturnsAnError()
    {
        SwarmClusterState state = SwarmClusterState.Create(null, false, errorMessage: "daemon unavailable");

        Assert.Equal(SwarmMembershipState.Error, state.Membership);
        Assert.Equal("daemon unavailable", state.ErrorMessage);
    }
}
