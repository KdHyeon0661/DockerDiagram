using System;
using System.Globalization;
using System.Windows.Data;

namespace DockerDiagram.Helpers
{
    public class HalfValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 입력값이 double이면 2로 나누고, 아니면 0을 반환
            return value is double d ? d / 2 : 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}