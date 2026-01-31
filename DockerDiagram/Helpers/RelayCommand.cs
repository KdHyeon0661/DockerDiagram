using System.Windows.Input; // ICommand 인터페이스가 들어있는 곳

namespace DockerDiagram.Helpers
{
    // 버튼 클릭 등의 동작을 ViewModel의 메서드와 연결해주는 전달자(Relay) 클래스입니다.
    public class RelayCommand : ICommand
    {
        // 실행할 로직을 담는 변수 (예: 저장하기 메서드)
        private readonly Action<object?> _execute;
        // 실행 가능한지 여부를 확인하는 로직 (예: 입력값이 비어있으면 버튼 비활성화)
        private readonly Predicate<object?>? _canExecute;

        // 생성자: 실행할 내용(execute)과 실행 가능 조건(canExecute)을 받습니다.
        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        // 버튼의 활성화/비활성화 상태가 바뀌어야 할 때 WPF에 알려주는 이벤트입니다.
        // CommandManager.RequerySuggested를 사용하면 UI의 상태가 바뀔 때마다 버튼 상태를 자동으로 체크합니다.
        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        // 버튼이 눌릴 수 있는 상태인지 확인합니다 (true면 버튼 활성화, false면 회색으로 비활성화).
        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        // 버튼을 눌렀을 때 실제로 실행되는 로직입니다.
        public void Execute(object? parameter) => _execute(parameter);
    }
}