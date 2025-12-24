using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DockerDiagram.Helpers
{
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private readonly Action<Exception>? _onException;
        private int _isExecuting; // 0/1 (Interlocked)

        public event EventHandler? CanExecuteChanged;

        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null, Action<Exception>? onException = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onException = onException;
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onException = null)
            : this(_ => execute(), canExecute != null ? (_ => canExecute()) : null, onException) { }

        public bool CanExecute(object? parameter)
        {
            if (Volatile.Read(ref _isExecuting) == 1) return false;
            return _canExecute == null || _canExecute(parameter);
        }

        public async void Execute(object? parameter)
        {
            await ExecuteAsync(parameter);
        }

        public async Task ExecuteAsync(object? parameter)
        {
            if (!CanExecute(parameter)) return;

            if (Interlocked.Exchange(ref _isExecuting, 1) == 1) return;
            RaiseCanExecuteChanged();

            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                if (_onException != null) _onException(ex);
                else System.Diagnostics.Debug.WriteLine($"Async Command Error: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            // UI 스레드에서 안전하게 이벤트 발생
            if (Application.Current?.Dispatcher?.CheckAccess() == true)
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
            }
        }
    }
}