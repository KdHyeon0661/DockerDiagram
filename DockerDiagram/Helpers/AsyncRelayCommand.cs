using System.Windows;
using System.Windows.Input;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 비동기 작업(Task)을 지원하고 중복 실행을 방지하는 커맨드 클래스입니다.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;       // 실행할 비동기 로직
        private readonly Predicate<object?>? _canExecute;    // 실행 가능 조건
        private readonly Action<Exception>? _onException;    // 예외 발생 시 처리 로직
        private int _isExecuting;                            // 실행 중 상태 (0: 대기, 1: 실행중)

        public event EventHandler? CanExecuteChanged;

        // 생성자 1: 매개변수가 있는 버전
        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null, Action<Exception>? onException = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onException = onException;
        }

        // 생성자 2: 매개변수가 없는 버전 (this를 사용하여 생성자 1을 호출함)
        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onException = null)
            : this(_ => execute(), canExecute != null ? (_ => canExecute()) : null, onException) { }

        // 현재 명령이 실행 가능한 상태인지 확인합니다. 실행 중일 때는 무조건 false를 반환하여 중복 클릭을 방지합니다.
        public bool CanExecute(object? parameter)
        {
            if (Volatile.Read(ref _isExecuting) == 1) return false;
            return _canExecute == null || _canExecute(parameter);
        }

        // ICommand 인터페이스의 명세에 따라 비동기 실행을 시작합니다.
        public async void Execute(object? parameter)
        {
            await ExecuteAsync(parameter);
        }

        // 실제 비동기 로직을 수행하며 중복 방지 및 예외 처리를 담당합니다.
        public async Task ExecuteAsync(object? parameter)
        {
            // 1. 실행 가능 여부 및 중복 실행 체크
            if (!CanExecute(parameter)) return;
            if (Interlocked.Exchange(ref _isExecuting, 1) == 1) return;

            // 2. 버튼 상태 갱신 알림 (비활성화 상태로 만듦)
            RaiseCanExecuteChanged();

            try
            {
                // 3. 실제 전달받은 비동기 작업 수행
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                // 4. 예외 처리
                if (_onException != null) _onException(ex);
                else System.Diagnostics.Debug.WriteLine($"Async Command Error: {ex}");
            }
            finally
            {
                // 5. 상태 복구 및 버튼 상태 다시 알림
                Interlocked.Exchange(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        // UI 스레드에서 안전하게 CanExecuteChanged 이벤트를 발생시킵니다.
        public void RaiseCanExecuteChanged()
        {
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