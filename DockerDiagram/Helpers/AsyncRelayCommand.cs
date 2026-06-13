using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 비동기 작업(Task)을 안전하게 실행하기 위한 커맨드 클래스입니다.
    /// 작업 실행 중에는 명령을 비활성화하여 중복 실행을 방지합니다.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute; // 비동기로 실행할 실제 로직 (Task 반환)
        private readonly Predicate<object?>? _canExecute; // 실행 가능 여부를 검사하는 로직
        private readonly Action<Exception>? _onException; // 작업 중 에러가 발생했을 때 처리할 로직
        private int _isExecuting; // 현재 작업이 실행 중인지 여부를 저장 (0: 대기 중, 1: 실행 중 / 스레드 안전성 보장용)

        public event EventHandler? CanExecuteChanged; // 버튼 활성/비활성 상태 변경을 UI에 알리는 이벤트

        /// <summary>
        /// 파라미터를 받아 비동기 작업을 수행하는 커맨드를 초기화합니다.
        /// </summary>
        public AsyncRelayCommand(
            Func<object?, Task> execute,
            Predicate<object?>? canExecute = null,
            Action<Exception>? onException = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onException = onException;
        }

        /// <summary>
        /// 파라미터를 사용하지 않는 비동기 작업을 위한 오버로딩 생성자입니다.
        /// </summary>
        public AsyncRelayCommand(
            Func<Task> execute,
            Func<bool>? canExecute = null,
            Action<Exception>? onException = null)
            : this(_ => execute(), canExecute != null ? (_ => canExecute()) : null, onException)
        {
        }

        /// <summary>
        /// 뷰모델의 특정 속성(Property)이 변할 때마다 자동으로 CanExecute를 다시 검사하도록 연결하는 생성자입니다.
        /// </summary>
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

        /// <summary>
        /// 현재 이 커맨드(버튼)를 실행할 수 있는 상태인지 검사합니다.
        /// 작업이 이미 실행 중(Executing)일 경우 무조건 false를 반환하여 중복 실행을 막습니다.
        /// </summary>
        public bool CanExecute(object? parameter)
        {
            if (Volatile.Read(ref _isExecuting) == 1) return false;
            return _canExecute?.Invoke(parameter) ?? true;
        }

        /// <summary>
        /// ICommand 인터페이스 규격을 맞추기 위한 동기 진입점입니다. 내부적으로 ExecuteAsync를 호출합니다.
        /// </summary>
        public async void Execute(object? parameter)
        {
            await ExecuteAsync(parameter);
        }

        /// <summary>
        /// 실제 비동기 작업을 수행하는 메서드입니다.
        /// Interlocked를 사용하여 멀티스레드 환경에서도 안전하게 실행 상태를 관리합니다.
        /// </summary>
        public async Task ExecuteAsync(object? parameter)
        {
            if (!CanExecute(parameter)) return;

            // 원자적(Atomic) 연산으로 상태를 1(실행 중)로 변경. 만약 이미 1이었다면 중복 실행이므로 즉시 취소
            if (Interlocked.Exchange(ref _isExecuting, 1) == 1) return;

            RaiseCanExecuteChanged(); // 버튼 비활성화 UI 업데이트

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
                // 작업이 끝나면(성공이든 에러든) 상태를 0(대기 중)으로 되돌리고 버튼 활성화 UI 업데이트
                Interlocked.Exchange(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// 커맨드의 실행 가능 여부(CanExecute)가 변경되었음을 UI에 강제로 알립니다.
        /// UI 스레드 접근 오류를 방지하기 위해 Dispatcher를 사용합니다.
        /// </summary>
        public void RaiseCanExecuteChanged()
        {
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
