using DockerDiagram.Contracts;
using DockerDiagram.Common;
using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public sealed class SwarmReadinessAuditService : ISwarmReadinessAuditService
    {
        private readonly IDockerCliProbe _cliProbe;

        public SwarmReadinessAuditService(IDockerCliProbe cliProbe)
        {
            _cliProbe = cliProbe ?? throw new ArgumentNullException(nameof(cliProbe));
        }

        public async Task<SwarmReadinessReport> AuditAsync(
            IDockerService service,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(service);
            Task<DockerCliProbeResult> cliTask = _cliProbe.ProbeAsync(cancellationToken);

            bool engineReachable;
            try
            {
                engineReachable = await ((ISystemService)service)
                    .PingAsync()
                    .WaitAsync(SwarmOperationTiming.ReadinessQueryTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                engineReachable = false;
            }

            DockerCliProbeResult cli = await cliTask;
            if (!engineReachable)
            {
                return new SwarmReadinessReport(
                    DateTimeOffset.Now,
                    SwarmReadinessPolicy.Evaluate(false, cli, null, null, null));
            }

            SwarmClusterState cluster;
            try
            {
                cluster = await ((ISwarmService)service)
                    .GetSwarmStateAsync(cancellationToken)
                    .WaitAsync(SwarmOperationTiming.ReadinessQueryTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failedCluster = SwarmClusterState.Create("error", false, errorMessage: ex.GetBaseException().Message);
                return new SwarmReadinessReport(
                    DateTimeOffset.Now,
                    SwarmReadinessPolicy.Evaluate(true, cli, failedCluster, null, null));
            }

            IReadOnlyList<DockerSwarmNode>? nodes = null;
            int? serviceCount = null;
            bool? ingressNetworkAvailable = null;
            string nodeError = string.Empty;
            string serviceError = string.Empty;
            string networkError = string.Empty;
            if (cluster.IsManager)
            {
                try
                {
                    nodes = await ((ISwarmService)service)
                        .GetSwarmNodesAsync()
                        .WaitAsync(SwarmOperationTiming.ReadinessQueryTimeout, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    nodeError = ex.GetBaseException().Message;
                }

                try
                {
                    serviceCount = (await ((ISwarmService)service)
                            .GetSwarmServicesAsync()
                            .WaitAsync(SwarmOperationTiming.ReadinessQueryTimeout, cancellationToken))
                        .Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    serviceError = ex.GetBaseException().Message;
                }

                try
                {
                    List<DockerNetworkGroup> networks = await ((INetworkService)service)
                        .GetNetworksAsync()
                        .WaitAsync(SwarmOperationTiming.ReadinessQueryTimeout, cancellationToken);
                    ingressNetworkAvailable = SwarmIngressNetworkSupport.HasIngressNetwork(networks);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    networkError = ex.GetBaseException().Message;
                }
            }

            IReadOnlyList<SwarmReadinessCheck> checks = SwarmReadinessPolicy.Evaluate(
                true,
                cli,
                cluster,
                nodes,
                serviceCount,
                nodeError,
                serviceError,
                ingressNetworkAvailable,
                networkError);
            return new SwarmReadinessReport(DateTimeOffset.Now, checks);
        }
    }
}
