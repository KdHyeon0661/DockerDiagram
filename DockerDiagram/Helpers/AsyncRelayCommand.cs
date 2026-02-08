using System;
using System.ComponentModel;
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
        private int _isExecuting; // 0: Idle, 1: Executing (Interlocked 사용)

        public event EventHandler? CanExecuteChanged;

        // === 기존 생성자 ===
        public AsyncRelayCommand(
            Func<object?, Task> execute,
            Predicate<object?>? canExecute = null,
            Action<Exception>? onException = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onException = onException;
        }

        public AsyncRelayCommand(
            Func<Task> execute,
            Func<bool>? canExecute = null,
            Action<Exception>? onException = null)
            : this(_ => execute(), canExecute != null ? (_ => canExecute()) : null, onException)
        {
        }

        public AsyncRelayCommand(
            Func<object?, Task> execute,
            Predicate<object?>? canExecute,
            INotifyPropertyChanged notifier,
            params string[] watchedProperties)
            : this(execute, canExecute)
        {
            if (notifier == null) return;

            notifier.PropertyChanged += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.PropertyName) ||
                    watchedProperties.Length == 0 ||
                    Array.IndexOf(watchedProperties, e.PropertyName) >= 0)
                {
                    RaiseCanExecuteChanged();
                }
            };
        }

        public bool CanExecute(object? parameter)
        {
            // 실행 중일 때는 버튼을 비활성화하여 중복 클릭 방지
            if (Volatile.Read(ref _isExecuting) == 1) return false;
            return _canExecute?.Invoke(parameter) ?? true;
        }

        public async void Execute(object? parameter)
        {
            await ExecuteAsync(parameter);
        }

        public async Task ExecuteAsync(object? parameter)
        {
            if (!CanExecute(parameter)) return;

            // 실행 상태로 전환 (Atomic operation)
            if (Interlocked.Exchange(ref _isExecuting, 1) == 1) return;

            RaiseCanExecuteChanged(); // UI 비활성화 갱신

            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                if (_onException != null) _onException(ex);
                else System.Diagnostics.Debug.WriteLine($"[AsyncRelayCommand] Error: {ex}");
            }
            finally
            {
                // 실행 상태 해제 및 UI 활성화 갱신
                Interlocked.Exchange(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            // UI 스레드에서 이벤트 발생 보장
            if (Application.Current?.Dispatcher?.CheckAccess() == true)
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Application.Current?.Dispatcher?.BeginInvoke(
                    new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
            }
        }
    }
}