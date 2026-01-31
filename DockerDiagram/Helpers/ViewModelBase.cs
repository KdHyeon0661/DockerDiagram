using System.ComponentModel; // INotifyPropertyChanged 인터페이스가 들어있는 네임스페이스
using System.Runtime.CompilerServices; // [CallerMemberName] 특성을 쓰기 위한 네임스페이스

namespace DockerDiagram.Helpers
{
    // 모든 ViewModel의 부모가 되는 베이스 클래스입니다. 이 클래스를 상속받으면 UI 자동 업데이트 기능을 가질 수 있습니다.
    public class ViewModelBase : INotifyPropertyChanged
    {
        // UI가 데이터 변경을 감지하기 위해 구독(Listen)하는 이벤트입니다. 값이 바뀌면 이 이벤트가 "데이터 바뀌었어!"라고 신호를 보냅니다.
        public event PropertyChangedEventHandler? PropertyChanged;

        // 속성 값이 바뀌었을 때 UI에 알림을 보내는 메서드입니다.
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            // PropertyChanged 이벤트에 등록된 리스너(UI 요소들)가 있다면 호출합니다.
            // name에는 "UserName" 같은 속성 이름이 전달됩니다.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}