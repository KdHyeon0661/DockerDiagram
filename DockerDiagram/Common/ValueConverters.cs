using System;
using System.Globalization;
using System.Windows.Data;

namespace DockerDiagram.Common
{
    /// <summary>
    /// WPF 데이터 바인딩 시 입력된 실수(double) 값을 정확히 절반(1/2)으로 나누어 반환하는 변환기입니다.
    /// 주로 UI 요소의 너비(Width)나 높이(Height)를 바인딩하여 중심점(Center) 좌표를 계산할 때 사용됩니다.
    /// </summary>
    public class HalfValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) // 소스(데이터)에서 UI(화면)로 값을 보낼 때 호출됨: double이면 2로 나눔
        {
            return value is double d ? d / 2 : 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) // UI에서 소스로 값을 되돌릴 때 호출됨 (이 앱에서는 단방향 계산만 하므로 미사용)
        {
            throw new NotImplementedException();
        }
    }

    public sealed class InverseScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is double scale && double.IsFinite(scale) && scale > 0
                ? 1.0 / scale
                : 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}