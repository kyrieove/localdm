using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LocalDm.App.Converters;

/// <summary>
/// 环节表盘的弧：0–1 的进度 → 从 12 点顺时针的一段圆弧。
/// 圆心与半径固定为 44×44 的表盘（圆心 22,22，半径 19，配 4px 描边）。
/// </summary>
public sealed class ProgressArcConverter : IValueConverter
{
    private const double Center = 22, Radius = 19;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Clamp(d, 0, 1) : 0;
        if (fraction <= 0)
        {
            return Geometry.Empty;
        }

        if (fraction >= 1)
        {
            return new EllipseGeometry(new Point(Center, Center), Radius, Radius);
        }

        var angle = fraction * 2 * Math.PI;
        var end = new Point(Center + Radius * Math.Sin(angle), Center - Radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(Center, Center - Radius) };
        figure.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
