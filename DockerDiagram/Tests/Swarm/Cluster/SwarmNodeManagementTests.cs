using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;
using Xunit;

namespace DockerDiagram.Tests
{
    public sealed class SwarmNodeManagementTests
    {
        [Theory]
        [InlineData("active")]
        [InlineData("pause")]
        [InlineData("drain")]
        public void UpdateOptions_Validate_AcceptsAvailability(string availability)
        {
            ValidUpdate(availability: availability).Validate();
        }

        [Fact]
        public void UpdateOptions_Validate_RejectsInvalidRole()
        {
            SwarmNodeUpdateOptions options = ValidUpdate(role: "leader");

            Assert.Throws<ArgumentException>(options.Validate);
        }

        [Fact]
        public void SafetyPolicy_BlocksLastManagerDemotion()
        {
            SwarmNodeEditSnapshot current = Snapshot(role: "manager");
            SwarmNodeUpdateOptions desired = ValidUpdate(role: "worker");

            Assert.Throws<InvalidOperationException>(() =>
                SwarmNodeSafetyPolicy.ValidateUpdate(current, desired, managerCount: 1));
        }

        [Fact]
        public void SafetyPolicy_AllowsManagerDemotionWhenAnotherManagerExists()
        {
            SwarmNodeEditSnapshot current = Snapshot(role: "manager");
            SwarmNodeUpdateOptions desired = ValidUpdate(role: "worker");

            SwarmNodeSafetyPolicy.ValidateUpdate(current, desired, managerCount: 3);
        }

        [Fact]
        public void SafetyPolicy_BlocksDemotionWhenRemainingManagersLoseQuorum()
        {
            SwarmNodeEditSnapshot current = Snapshot(role: "manager");
            SwarmNodeUpdateOptions desired = ValidUpdate(role: "worker");

            Assert.Throws<InvalidOperationException>(() =>
                SwarmNodeSafetyPolicy.ValidateUpdate(
                    current,
                    desired,
                    new SwarmManagerQuorumState(
                        ManagerCount: 3,
                        ReachableManagerCount: 2,
                        TargetManagerReachable: true)));
        }

        [Fact]
        public void SafetyPolicy_AllowsDemotionWhenRemainingManagersKeepQuorum()
        {
            SwarmNodeEditSnapshot current = Snapshot(role: "manager");
            SwarmNodeUpdateOptions desired = ValidUpdate(role: "worker");

            SwarmNodeSafetyPolicy.ValidateUpdate(
                current,
                desired,
                new SwarmManagerQuorumState(
                    ManagerCount: 3,
                    ReachableManagerCount: 3,
                    TargetManagerReachable: true));
        }

        [Fact]
        public void SafetyPolicy_BlocksLocalNodeRemoval()
        {
            SwarmNodeEditSnapshot current = Snapshot(isLocal: true, status: "down");

            Assert.Throws<InvalidOperationException>(() =>
                SwarmNodeSafetyPolicy.ValidateRemoval(current, force: true));
        }

        [Fact]
        public void SafetyPolicy_BlocksManagerRemoval()
        {
            SwarmNodeEditSnapshot current = Snapshot(role: "manager", status: "down");

            Assert.Throws<InvalidOperationException>(() =>
                SwarmNodeSafetyPolicy.ValidateRemoval(current, force: true));
        }

        [Fact]
        public void SafetyPolicy_RequiresForceForReadyWorker()
        {
            SwarmNodeEditSnapshot current = Snapshot(status: "ready");

            Assert.Throws<InvalidOperationException>(() =>
                SwarmNodeSafetyPolicy.ValidateRemoval(current, force: false));
            SwarmNodeSafetyPolicy.ValidateRemoval(current, force: true);
        }

        [Fact]
        public void SafetyPolicy_AllowsDownWorkerRemovalWithoutForce()
        {
            SwarmNodeSafetyPolicy.ValidateRemoval(Snapshot(status: "down"), force: false);
        }

        [Fact]
        public void SafetyPolicy_WarnsForDrainAndManagerPromotion()
        {
            SwarmNodeEditSnapshot current = Snapshot(role: "worker", availability: "active");
            SwarmNodeUpdateOptions desired = ValidUpdate(role: "manager", availability: "drain");

            IReadOnlyList<string> warnings = SwarmNodeSafetyPolicy.GetUpdateWarnings(current, desired, 1);

            Assert.Equal(2, warnings.Count);
        }

        private static SwarmNodeUpdateOptions ValidUpdate(
            string role = "worker",
            string availability = "active") =>
            new()
            {
                NodeId = "node-id",
                Version = 7,
                Role = role,
                Availability = availability,
                Labels = new Dictionary<string, string> { ["zone"] = "seoul" }
            };

        private static SwarmNodeEditSnapshot Snapshot(
            string role = "worker",
            string availability = "active",
            string status = "ready",
            bool isLocal = false) =>
            new()
            {
                NodeId = "node-id",
                Version = 7,
                Hostname = "worker-1",
                Role = role,
                Availability = availability,
                Status = status,
                IsLocalNode = isLocal
            };
    }
}
