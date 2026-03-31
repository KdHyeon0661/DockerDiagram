using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// MVVM 패턴의 핵심인 데이터 바인딩을 지원하는 뷰모델의 최상위 부모 클래스입니다.
    /// 데이터(속성)가 변경될 때 WPF UI(화면)가 이를 감지하고 자동으로 갱신될 수 있도록 해줍니다.
    /// </summary>
    public class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged; // 속성값이 변경되었음을 UI(화면)에 알리는 이벤트

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) // 속성 변경 이벤트를 발생시키는 메서드 (속성명 자동 추론)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // 값을 변경하고 알림까지 한 번에 처리하는 헬퍼 메서드입니다.
        // 사용법: set => SetProperty(ref _field, value);
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) // 값이 다를 때만 변경을 적용하고 알림을 보내는 최적화 메서드
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}