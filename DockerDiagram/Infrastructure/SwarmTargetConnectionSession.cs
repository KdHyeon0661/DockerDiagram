using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// Swarm Join 전 단계에서 검증된 원격 Docker 연결의 소유권을 보관합니다.
    /// Dispose 시 Docker 서비스와 SSH 터널 참조를 항상 함께 해제합니다.
    /// </summary>
    public sealed class SwarmTargetConnectionSession : IDisposable
    {
        private readonly IDockerServiceFactory _serviceFactory;
        private bool _disposed;

        public SwarmTargetConnectionSession(
            SwarmTargetConnectionOptions options,
            ConnectionProfile profile,
            IDockerService dockerService,
            SwarmClusterState verifiedState,
            IDockerServiceFactory serviceFactory)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            DockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
            VerifiedState = verifiedState ?? throw new ArgumentNullException(nameof(verifiedState));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        public SwarmTargetConnectionOptions Options { get; }
        public ConnectionProfile Profile { get; }
        public IDockerService DockerService { get; }
        public SwarmClusterState VerifiedState { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (!_serviceFactory.Release(DockerService))
                    DockerService.Dispose();
            }
            finally
            {
                SshTunnelManager.ReleaseTunnel(
                    Options.Host,
                    Options.SshPort,
                    Options.Username,
                    Profile.RemoteDockerSocketPath);
            }
        }
    }
}
