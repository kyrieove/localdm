using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LocalDm.App.Converters;

/// <summary>false → Collapsed，true → Visible；用于空状态与可选区块。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
