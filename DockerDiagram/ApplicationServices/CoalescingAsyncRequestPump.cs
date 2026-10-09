namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// 실행 중 들어온 요청을 버리지 않고, 현재 실행이 끝난 뒤 한 번으로 합쳐 다시 실행합니다.
    /// </summary>
    public sealed class CoalescingAsyncRequestPump : IDisposable
    {
        private readonly object _gate = new();
        private readonly Func<Task> _operation;
        private readonly List<TaskCompletionSource> _pendingRequests = new();
        private Task? _runner;
        private bool _disposed;

        public CoalescingAsyncRequestPump(Func<Task> operation)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        }

        public Task RequestAsync()
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingRequests.Add(completion);
                _runner ??= RunLoopAsync();
                return completion.Task;
            }
        }

        private async Task RunLoopAsync()
        {
            // RequestAsync가 _runner를 할당하기 전에 동기 완료되는 경합을 막습니다.
            await Task.Yield();

            while (true)
            {
                TaskCompletionSource[] batch;
                lock (_gate)
                {
                    if (_disposed || _pendingRequests.Count == 0)
                    {
                        // 요청 확인과 runner 해제를 같은 lock에서 처리해야 종료 직전 요청이 유실되지 않습니다.
                        _runner = null;
                        return;
                    }

                    batch = _pendingRequests.ToArray();
                    _pendingRequests.Clear();
                }

                try
                {
                    await _operation();
                    foreach (TaskCompletionSource completion in batch)
                        completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    foreach (TaskCompletionSource completion in batch)
                        completion.TrySetException(ex);
                }
            }
        }

        public void Dispose()
        {
            TaskCompletionSource[] abandonedRequests;
            lock (_gate)
            {
                _disposed = true;
                abandonedRequests = _pendingRequests.ToArray();
                _pendingRequests.Clear();
            }

            foreach (TaskCompletionSource completion in abandonedRequests)
                completion.TrySetResult();
        }
    }
}
