using System.Globalization;
using System.Windows.Data; // IValueConverter 인터페이스를 사용하기 위한 네임스페이스

namespace DockerDiagram.Helpers
{
    // 입력받은 숫자(double) 값을 2로 나누어 반환하는 변환기입니다. 예: 너비가 100이면 50을 반환하여 요소를 중앙에 배치할 때 등에 사용합니다.
    public class HalfValueConverter : IValueConverter
    {
        // 소스(데이터)에서 타겟(UI)으로 값을 보낼 때 호출됩니다.
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 입력받은 값(value)이 double 타입인지 확인합니다.
            if (value is double d)
            {
                return d / 2; // 숫자를 2로 나누어 결과값을 보냅니다.
            }

            // double 타입이 아니라면 기본값 0을 반환합니다.
            return 0;
        }

        // 타겟(UI)의 값을 소스(데이터)로 돌려보낼 때 호출됩니다. 이 컨버터는 화면 표시용이므로 거꾸로 계산하는 기능은 구현하지 않았습니다.
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 양방향 바인딩이 필요 없는 경우 보통 예외를 던지도록 둡니다.
            throw new NotImplementedException();
        }
    }
}