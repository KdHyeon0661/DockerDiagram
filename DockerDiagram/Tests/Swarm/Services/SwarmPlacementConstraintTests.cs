using DockerDiagram.Models;
using Xunit;

namespace DockerDiagram.Tests
{
    public sealed class SwarmPlacementConstraintTests
    {
        [Theory]
        [InlineData("node.labels.zone==seoul")]
        [InlineData("node.role==worker")]
        [InlineData("node.hostname!=worker-3")]
        [InlineData("engine.labels.storage==ssd")]
        public void ValidateAcceptsDockerConstraintSyntax(string constraint)
        {
            Options(constraint).Validate();
        }

        [Theory]
        [InlineData("node.labels.zone=seoul")]
        [InlineData("node.labels.zone")]
        [InlineData("==worker")]
        public void ValidateRejectsInvalidConstraintSyntax(string constraint)
        {
            Assert.Throws<ArgumentException>(() => Options(constraint).Validate());
        }

        [Fact]
        public void ValidateAllowsEmptyListToClearConstraints()
        {
            Options().Validate();
        }

        [Fact]
        public void ValidateRejectsDuplicateConstraints()
        {
            var options = new SwarmServicePlacementUpdateOptions
            {
                ServiceId = "service-id",
                Version = 4,
                Constraints = new[] { "node.role==worker", "node.role==worker" }
            };

            Assert.Throws<ArgumentException>(options.Validate);
        }

        private static SwarmServicePlacementUpdateOptions Options(params string[] constraints) =>
            new()
            {
                ServiceId = "service-id",
                Version = 4,
                Constraints = constraints
            };
    }
}
