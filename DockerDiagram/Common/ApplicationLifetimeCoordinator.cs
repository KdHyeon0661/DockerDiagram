namespace DockerDiagram.Common
{
    /// <summary>
    /// 애플리케이션 범위 취소와 정리 작업을 역순으로 한 번만 실행합니다.
    /// </summary>
    public sealed class ApplicationLifetimeCoordinator : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationToken _token;
        private readonly Stack<Action> _cleanupActions = new();
        private readonly object _sync = new();
        private bool _disposed;

        public ApplicationLifetimeCoordinator()
        {
            _token = _cancellation.Token;
        }

        public CancellationToken Token => _token;
        public bool IsCancellationRequested => _token.IsCancellationRequested;

        public void Register(Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _cleanupActions.Push(cleanup);
            }
        }

        public void Register(IDisposable disposable)
        {
            ArgumentNullException.ThrowIfNull(disposable);
            Register(disposable.Dispose);
        }

        public void Dispose()
        {
            Stack<Action> actions;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                actions = new Stack<Action>(_cleanupActions.Reverse());
                _cleanupActions.Clear();
            }

            _cancellation.Cancel();
            while (actions.Count > 0)
            {
                try
                {
                    actions.Pop()();
                }
                catch (Exception ex)
                {
                    AppLog.Warning("종료 정리 작업 실패", ex);
                }
            }

            _cancellation.Dispose();
        }
    }
}
