using DockerDiagram.Models;
using Xunit;

namespace DockerDiagram.Tests
{
    public sealed class SwarmDataResourceModelsTests
    {
        [Theory]
        [InlineData(SwarmDataResourceKind.Secret)]
        [InlineData(SwarmDataResourceKind.Config)]
        public void CreateOptionsValidateAcceptsValidResource(SwarmDataResourceKind kind)
        {
            var options = new SwarmDataResourceCreateOptions
            {
                Kind = kind,
                Name = "app-settings_v1",
                Data = "value",
                Labels = new Dictionary<string, string> { ["environment"] = "test" }
            };

            options.Validate();
        }

        [Fact]
        public void CreateOptionsValidateRejectsEmptyData()
        {
            var options = new SwarmDataResourceCreateOptions
            {
                Kind = SwarmDataResourceKind.Secret,
                Name = "api-key",
                Data = string.Empty
            };

            Assert.Throws<ArgumentException>(options.Validate);
        }

        [Fact]
        public void CreateOptionsValidateRejectsPayloadOverDockerLimit()
        {
            var options = new SwarmDataResourceCreateOptions
            {
                Kind = SwarmDataResourceKind.Config,
                Name = "large-config",
                Data = new string('x', (500 * 1024) + 1)
            };

            Assert.Throws<ArgumentException>(options.Validate);
        }

        [Theory]
        [InlineData("")]
        [InlineData("bad name")]
        [InlineData("name/with/slash")]
        public void CreateOptionsValidateRejectsInvalidName(string name)
        {
            var options = new SwarmDataResourceCreateOptions
            {
                Kind = SwarmDataResourceKind.Secret,
                Name = name,
                Data = "value"
            };

            Assert.Throws<ArgumentException>(options.Validate);
        }

        [Fact]
        public void ResourceReferenceValidateAcceptsIdAndTargetName()
        {
            var reference = new SwarmServiceResourceReferenceOptions("resource-id", "api-key", "api_key");

            reference.Validate();
        }

        [Fact]
        public void ResourceReferenceValidateRejectsMissingId()
        {
            var reference = new SwarmServiceResourceReferenceOptions(string.Empty, "api-key", "api_key");

            Assert.Throws<ArgumentException>(reference.Validate);
        }

        [Fact]
        public void TopologyUpdateValidateRejectsDuplicateSecretTargets()
        {
            var referenceA = new SwarmServiceResourceReferenceOptions("one", "first", "shared");
            var referenceB = new SwarmServiceResourceReferenceOptions("two", "second", "shared");
            var options = new SwarmServiceTopologyUpdateOptions
            {
                Service = ValidServiceUpdate(),
                ApplySecrets = true,
                Secrets = new[] { referenceA, referenceB }
            };

            Assert.Throws<ArgumentException>(options.Validate);
        }

        private static SwarmServiceUpdateOptions ValidServiceUpdate()
        {
            return new SwarmServiceUpdateOptions
            {
                ServiceId = "service-id",
                Version = 1,
                Spec = new SwarmServiceSpecOptions
                {
                    Name = "service",
                    Image = "nginx:latest"
                }
            };
        }
    }
}
