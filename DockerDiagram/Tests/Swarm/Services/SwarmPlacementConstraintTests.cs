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
        public void Validate_AcceptsDockerConstraintSyntax(string constraint)
        {
            Options(constraint).Validate();
        }

        [Theory]
        [InlineData("node.labels.zone=seoul")]
        [InlineData("node.labels.zone")]
        [InlineData("==worker")]
        public void Validate_RejectsInvalidConstraintSyntax(string constraint)
        {
            Assert.Throws<ArgumentException>(() => Options(constraint).Validate());
        }

        [Fact]
        public void Validate_AllowsEmptyListToClearConstraints()
        {
            Options().Validate();
        }

        [Fact]
        public void Validate_RejectsDuplicateConstraints()
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
