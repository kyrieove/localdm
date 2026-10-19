using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LocalDm.App.Converters;

/// <summary>false → Visible，true → Collapsed；用于空状态显示。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}
