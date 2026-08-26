using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.Infrastructure
{
    public sealed class DockerServiceFactory : IDockerServiceFactory
    {
        private readonly object _sync = new();
        private readonly HashSet<IDockerService> _activeServices = new(ReferenceEqualityComparer.Instance);
        private readonly Func<ConnectionProfile, IDockerService> _createService;
        private bool _disposed;

        public DockerServiceFactory()
            : this(profile => new DockerApiService(profile))
        {
        }

        public DockerServiceFactory(Func<ConnectionProfile, IDockerService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        public IDockerService Create(ConnectionProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ThrowIfDisposed();

            IDockerService service = _createService(profile);
            try
            {
                Register(service);
                return service;
            }
            catch
            {
                service.Dispose();
                throw;
            }
        }

        public void Register(IDockerService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            lock (_sync)
            {
                ThrowIfDisposed();
                _activeServices.Add(service);
            }
        }

        public bool Release(IDockerService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            lock (_sync)
            {
                if (!_activeServices.Remove(service)) return false;
            }

            service.Dispose();
            return true;
        }

        public void ReleaseAll()
        {
            IDockerService[] services;
            lock (_sync)
            {
                services = _activeServices.ToArray();
                _activeServices.Clear();
            }

            foreach (IDockerService service in services)
            {
                service.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }

            ReleaseAll();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
