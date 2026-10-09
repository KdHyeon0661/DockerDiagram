using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Tests;

public sealed class LiveDockerRuntimeTests
{
    [LiveDockerFact]
    public async Task DockerEngine_VolumeAndNetworkLifecycle_RoundTrips()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string volumeName = $"dockerdiagram-live-volume-{suffix}";
        string networkName = $"dockerdiagram-live-network-{suffix}";
        bool volumeCreated = false;
        string? networkId = null;
        using var service = CreateService(RuntimeKind.DockerEngine);

        try
        {
            await service.CreateVolumeAsync(new VolumeCreateOptions
            {
                Name = volumeName,
                DockerVolumeName = volumeName,
                Driver = "local",
                Labels = new Dictionary<string, string> { ["dockerdiagram.live-test"] = suffix }
            });
            volumeCreated = true;
            networkId = await service.CreateNetworkAsync(new NetworkCreateOptions
            {
                Name = networkName,
                Driver = "bridge",
                Labels = new Dictionary<string, string> { ["dockerdiagram.live-test"] = suffix }
            });

            Assert.Contains(await service.GetVolumesAsync(), volume => volume.Name == volumeName);
            Assert.Contains(await service.GetNetworksAsync(), network => network.Name == networkName);
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            if (!string.IsNullOrWhiteSpace(networkId))
            {
                try { await service.RemoveNetworkAsync(networkId); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            if (volumeCreated)
            {
                try { await service.RemoveVolumeAsync(volumeName, force: true); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            if (cleanupErrors.Count > 0)
                throw new AggregateException("Live Docker cleanup failed.", cleanupErrors);
        }
    }

    [LiveDockerFact]
    public async Task DockerSwarm_SecretConfigOverlayAndZeroReplicaServiceLifecycle_RoundTrips()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string secretName = $"dockerdiagram-live-secret-{suffix}";
        string configName = $"dockerdiagram-live-config-{suffix}";
        string networkName = $"dockerdiagram-live-overlay-{suffix}";
        string serviceName = $"dockerdiagram-live-service-{suffix}";
        string? secretId = null;
        string? configId = null;
        string? networkId = null;
        string? serviceId = null;
        using var service = CreateService(RuntimeKind.DockerSwarm);

        SwarmClusterState state = await service.GetSwarmStateAsync();
        Assert.True(state.IsManager, "Live Swarm test requires a manager connection.");

        try
        {
            SwarmDataResourceMutationResult secret = await service.CreateSwarmDataResourceAsync(
                new SwarmDataResourceCreateOptions
                {
                    Kind = SwarmDataResourceKind.Secret,
                    Name = secretName,
                    Data = "isolated-live-test",
                    Labels = new Dictionary<string, string> { ["dockerdiagram.live-test"] = suffix }
                });
            secretId = secret.ResourceId;

            SwarmDataResourceMutationResult config = await service.CreateSwarmDataResourceAsync(
                new SwarmDataResourceCreateOptions
                {
                    Kind = SwarmDataResourceKind.Config,
                    Name = configName,
                    Data = "live.enabled=true",
                    Labels = new Dictionary<string, string> { ["dockerdiagram.live-test"] = suffix }
                });
            configId = config.ResourceId;

            networkId = await service.CreateNetworkAsync(new NetworkCreateOptions
            {
                Name = networkName,
                Driver = "overlay",
                Attachable = true,
                Labels = new Dictionary<string, string> { ["dockerdiagram.live-test"] = suffix }
            });

            var serviceSpec = new SwarmServiceSpecOptions
            {
                Name = serviceName,
                Image = "alpine:3.20",
                Replicas = 0,
                Networks = new[] { new SwarmNetworkAttachmentOptions(networkId) },
                PublishedPorts = new[]
                {
                    new SwarmPublishedPortOptions(
                        8080,
                        null,
                        SwarmPortProtocol.Tcp,
                        SwarmPublishMode.Host)
                },
                Labels = new Dictionary<string, string> { ["dockerdiagram.live-test"] = suffix }
            };
            SwarmServiceMutationResult created = await service.CreateSwarmServiceAsync(
                new SwarmServiceCreateOptions
                {
                    Spec = serviceSpec
                });
            serviceId = created.ServiceId;

            await service.UpdateSwarmServiceTopologyAsync(
                new SwarmServiceTopologyUpdateOptions
                {
                    Service = new SwarmServiceUpdateOptions
                    {
                        ServiceId = serviceId,
                        Version = created.Version,
                        Spec = serviceSpec
                    },
                    ApplySecrets = true,
                    Secrets = new[]
                    {
                        new SwarmServiceResourceReferenceOptions(
                            secretId,
                            secretName,
                            "/run/secrets/live-token",
                            "0",
                            "0",
                            256)
                    },
                    ApplyConfigs = true,
                    Configs = new[]
                    {
                        new SwarmServiceResourceReferenceOptions(
                            configId,
                            configName,
                            "/etc/dockerdiagram/live.conf",
                            "0",
                            "0",
                            292)
                    }
                });

            Assert.Contains(await service.GetSwarmServicesAsync(), item => item.Name == serviceName);
            Assert.Contains(await service.GetNetworksAsync(), item => item.Name == networkName);
            JObject rawService = JObject.FromObject(
                await service.InspectSwarmServiceRawAsync(serviceId));
            JObject publishedPort = Assert.IsType<JObject>(
                Assert.Single(rawService["Spec"]!["EndpointSpec"]!["Ports"]!));
            Assert.Equal(8080U, publishedPort.Value<uint>("TargetPort"));
            Assert.Equal("tcp", publishedPort.Value<string>("Protocol"));
            Assert.Equal("host", publishedPort.Value<string>("PublishMode"));
            JObject secretReference = Assert.IsType<JObject>(
                Assert.Single(rawService["Spec"]!["TaskTemplate"]!["ContainerSpec"]!["Secrets"]!));
            Assert.Equal(secretId, secretReference.Value<string>("SecretID"));
            Assert.Equal("/run/secrets/live-token", secretReference["File"]!.Value<string>("Name"));
            Assert.Equal(256U, secretReference["File"]!.Value<uint>("Mode"));
            JObject configReference = Assert.IsType<JObject>(
                Assert.Single(rawService["Spec"]!["TaskTemplate"]!["ContainerSpec"]!["Configs"]!));
            Assert.Equal(configId, configReference.Value<string>("ConfigID"));
            Assert.Equal("/etc/dockerdiagram/live.conf", configReference["File"]!.Value<string>("Name"));
            Assert.Equal(292U, configReference["File"]!.Value<uint>("Mode"));
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            if (!string.IsNullOrWhiteSpace(serviceId))
            {
                try { await service.RemoveSwarmServiceAsync(serviceId); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            if (!string.IsNullOrWhiteSpace(networkId))
            {
                try { await service.RemoveNetworkAsync(networkId); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            if (!string.IsNullOrWhiteSpace(secretId))
            {
                try { await service.RemoveSwarmDataResourceAsync(SwarmDataResourceKind.Secret, secretId); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            if (!string.IsNullOrWhiteSpace(configId))
            {
                try { await service.RemoveSwarmDataResourceAsync(SwarmDataResourceKind.Config, configId); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            if (cleanupErrors.Count > 0)
                throw new AggregateException("Live Swarm cleanup failed.", cleanupErrors);
        }
    }

    private static DockerApiService CreateService(RuntimeKind runtimeKind) =>
        new(new ConnectionProfile
        {
            Name = "Live local Docker",
            Type = EndpointType.Local,
            RuntimeKind = runtimeKind
        });
}
