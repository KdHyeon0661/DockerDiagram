using System;
using System.Windows.Input;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// MVVM 패턴에서 UI의 동작(버튼 클릭 등)을 뷰모델(ViewModel)의 메서드와 연결해 주는 범용 커맨드 클래스입니다.
    /// 뷰(View)의 코드 비하인드(이벤트 핸들러)를 사용하지 않고도 명령의 실행 및 버튼의 활성/비활성 상태를 제어할 수 있습니다.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute; // 버튼을 눌렀을 때 실제로 실행될 로직
        private readonly Predicate<object?>? _canExecute; // 현재 버튼을 누를 수 있는 상태인지 검사하는 로직

        public event EventHandler? CanExecuteChanged; // 명령의 실행 가능 상태가 변경되었음을 UI(버튼 등)에 알리는 이벤트

        /// <summary>
        /// 실행할 로직(Action)과 실행 가능 여부를 검사하는 로직(Predicate)을 받아 커맨드를 초기화합니다.
        /// </summary>
        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <summary>
        /// UI가 이 명령(버튼)을 활성화할지 비활성화할지 물어볼 때 호출됩니다.
        /// </summary>
        public bool CanExecute(object? parameter)
            => _canExecute?.Invoke(parameter) ?? true;

        /// <summary>
        /// UI에서 이 명령(버튼)이 실행되었을 때 실제로 동작을 수행하는 메서드입니다.
        /// </summary>
        public void Execute(object? parameter)
            => _execute(parameter);

        /// <summary>
        /// 뷰모델에서 특정 데이터가 바뀌어 버튼의 활성/비활성 상태를 새로고침해야 할 때 수동으로 호출합니다.
        /// </summary>
        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}