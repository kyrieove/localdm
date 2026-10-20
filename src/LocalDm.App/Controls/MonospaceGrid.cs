using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LocalDm.App.ViewModels;

namespace LocalDm.App.Controls;

/// <summary>
/// 等宽块渲染器：把空格对齐的原文按字符栅格画出来，并给每一列画一条参考线。
///
/// 复盘时间线是「一行一个人」的宽表，桌边 1.5 米外看时，
/// 没有竖线就只能靠目测推断谁在哪一行 —— 列位参考线就是给眼睛用的栅格。
/// 列位置由内容本身推导（见 <c>TruthDocument.DetectColumns</c>），
/// 因此不依赖原文写得多整齐。
///
/// 注意：列位置按<b>视觉宽度</b>算，不按字符序号算 ——
/// 汉字是全角、西文是半角，字符序号推不出屏幕上的列。
/// </summary>
public sealed class MonospaceGrid : FrameworkElement
{
    private readonly Typeface _typeface;
    private double? _charWidth;
    private List<double>? _guidePositions;

    public MonospaceGrid()
    {
        _typeface = new Typeface(
            new FontFamily("NSimSun, SimSun, monospace"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        ClipToBounds = true;
    }

    /// <summary>要画的行。</summary>
    public IReadOnlyList<string> Lines
    {
        get => (IReadOnlyList<string>)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines),
        typeof(IReadOnlyList<string>),
        typeof(MonospaceGrid),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnContentChanged));

    /// <summary>列的起始字符下标（保留给外部按需使用；参考线由内容自行推导）。</summary>
    public IReadOnlyList<int> Columns
    {
        get => (IReadOnlyList<int>)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(IReadOnlyList<int>),
        typeof(MonospaceGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>字号。</summary>
    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize),
        typeof(double),
        typeof(MonospaceGrid),
        new FrameworkPropertyMetadata(
            15.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnCellSizeChanged));

    /// <summary>字号变了，之前量出来的字宽与参考线都要作废。</summary>
    private static void OnCellSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (MonospaceGrid)d;
        grid._charWidth = null;
        grid._guidePositions = null;
    }

    /// <summary>文字颜色。</summary>
    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush),
        typeof(Brush),
        typeof(MonospaceGrid),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>列参考线颜色。</summary>
    public Brush GuideBrush
    {
        get => (Brush)GetValue(GuideBrushProperty);
        set => SetValue(GuideBrushProperty, value);
    }

    public static readonly DependencyProperty GuideBrushProperty = DependencyProperty.Register(
        nameof(GuideBrush),
        typeof(Brush),
        typeof(MonospaceGrid),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>一个半角字符的宽度（像素）。</summary>
    private double CharWidth => _charWidth ??= MeasureHalfWidth();

    private double MeasureHalfWidth()
    {
        try
        {
            var sample = new FormattedText(
                "MMMM",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                CellSize,
                Brushes.Black,
                1.0);

            return sample.Width / 4.0;
        }
        catch
        {
            return CellSize * 0.5;
        }
    }

    private double MeasurePrefix(string line, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        var count = Math.Min(length, line.Length);
        try
        {
            var sample = new FormattedText(
                line[..count],
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                CellSize,
                Brushes.Black,
                1.0);

            return sample.Width;
        }
        catch
        {
            return count * CharWidth;
        }
    }

    /// <summary>内容或字号变了：之前算出来的参考线要作废。</summary>
    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MonospaceGrid)d)._guidePositions = null;

    /// <summary>
    /// 列的视觉位置。
    ///
    /// 列位必须在<b>视觉空间</b>里算：汉字是全角、西文是半角，
    /// 同一「列」在不同行里的字符下标并不相同（原文正是按视觉对齐写的）。
    /// 做法：量出每个词的起始像素位置，把挨得近的并成一列，
    /// 取每组的中位数作为参考线的位置。
    /// </summary>
    private List<double> ComputeGuidePositions()
    {
        var lines = Lines ?? [];
        var tolerance = CharWidth * 0.75;

        var positions = new List<double>();
        foreach (var line in lines)
        {
            foreach (var start in TruthDocument.TokenStarts(line))
            {
                positions.Add(MeasurePrefix(line, start));
            }
        }

        if (positions.Count == 0)
        {
            return [];
        }

        positions.Sort();
        var groups = new List<List<double>>();
        foreach (var x in positions)
        {
            if (groups.Count == 0 || x - groups[^1][^1] > tolerance)
            {
                groups.Add([x]);
            }
            else
            {
                groups[^1].Add(x);
            }
        }

        return groups
            .Select(g =>
            {
                g.Sort();
                return g[g.Count / 2];
            })
            .ToList();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lines = Lines ?? [];
        var columns = Columns ?? [];
        var lineHeight = CellSize * 1.55;

        var maxWidth = 0.0;
        foreach (var line in lines)
        {
            maxWidth = Math.Max(maxWidth, MeasurePrefix(line, line.Length));
        }

        // maxWidth 已经是像素，不能再乘一次字宽；
        // 只有「按字符数列推出来的宽度」才需要换算成像素。
        var columnWidth = columns.Count > 0 ? (columns[^1] + 1) * CharWidth : 0.0;
        var width = Math.Max(maxWidth, columnWidth);
        var height = Math.Max(lines.Count * lineHeight, 1);
        return new Size(width + 8, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var lines = Lines ?? [];
        var lineHeight = CellSize * 1.55;

        // 列参考线：让「谁在哪一行」不再靠目测
        var guidePositions = _guidePositions ??= ComputeGuidePositions();
        if (guidePositions.Count > 0)
        {
            var pen = new Pen(GuideBrush, 1);
            pen.Freeze();
            foreach (var x in guidePositions)
            {
                var px = Math.Round(x) + 0.5;
                dc.DrawLine(pen, new Point(px, 0), new Point(px, lines.Count * lineHeight));
            }
        }

        var textBrush = TextBrush ?? Brushes.Black;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var formatted = new FormattedText(
                line,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                CellSize,
                textBrush,
                1.0);

            dc.DrawText(formatted, new Point(0, i * lineHeight));
        }
    }
}
