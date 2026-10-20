using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LocalDm.App.Controls;

/// <summary>
/// 「进行中」的呼吸点：三个点依次明灭。
///
/// 纯代码构建（不引 XAML 文件）：一个小控件不值得为它单开一个资源字典，
/// 而且这样能精确控制动画的时序与配色。
/// </summary>
public sealed class ActivityDots : Decorator
{
    private const int DotCount = 3;

    private static readonly Duration DotPeriod = new(TimeSpan.FromMilliseconds(900));

    private readonly Ellipse[] _dots = new Ellipse[DotCount];
    private Storyboard? _storyboard;

    public ActivityDots()
    {
        Focusable = false;
        IsHitTestVisible = false;
        Width = 34;
        Height = 12;
        HorizontalAlignment = HorizontalAlignment.Left;
        BuildDots();

        // 控件离开视觉树时必须停掉动画：Forever 的 Storyboard 没人管会一直跑。
        Unloaded += (_, _) => StopAnimation();

        // 回到视觉树时，若仍处于「进行中」，动画要重新开始。
        Loaded += (_, _) =>
        {
            if (IsAnimating)
            {
                StartAnimation();
            }
        };
    }

    /// <summary>点的颜色。默认用印泥红，与状态条的强调色一致。</summary>
    public Brush DotBrush
    {
        get => (Brush)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush),
        typeof(Brush),
        typeof(ActivityDots),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender, OnDotBrushChanged));

    /// <summary>颜色变了要同步到每个点上：否则 Style 里的 Setter 只改了属性，点本身还是旧色。</summary>
    private static void OnDotBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dots = (ActivityDots)d;
        foreach (var dot in dots._dots)
        {
            dot.Fill = (Brush)e.NewValue;
        }
    }

    /// <summary>是否播放动效。空闲时停止并复位为静止状态。</summary>
    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    public static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.Register(
        nameof(IsAnimating),
        typeof(bool),
        typeof(ActivityDots),
        new FrameworkPropertyMetadata(false, OnIsAnimatingChanged));

    private static void OnIsAnimatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dots = (ActivityDots)d;
        if ((bool)e.NewValue)
        {
            dots.StartAnimation();
        }
        else
        {
            dots.StopAnimation();
        }
    }

    private void BuildDots()
    {
        var panel = new Canvas();
        for (var i = 0; i < DotCount; i++)
        {
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = DotBrush,
                Opacity = 0.6,
            };
            Canvas.SetLeft(dot, i * 11);
            Canvas.SetTop(dot, 2.5);
            _dots[i] = dot;
            panel.Children.Add(dot);
        }

        Child = panel;
    }
    private void StartAnimation()
    {
        StopAnimation();

        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        for (var i = 0; i < DotCount; i++)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = DotPeriod,
                BeginTime = TimeSpan.FromMilliseconds(i * (DotPeriod.TimeSpan.TotalMilliseconds / DotCount)),
            };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, KeyTime.FromPercent(0.0)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0.35)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, KeyTime.FromPercent(1.0)));

            Storyboard.SetTarget(animation, _dots[i]);
            Storyboard.SetTargetProperty(animation, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(animation);
        }

        _storyboard = storyboard;
        storyboard.Begin(this, isControllable: true);
    }

    private void StopAnimation()
    {
        if (_storyboard is null)
        {
            return;
        }

        _storyboard.Stop(this);
        _storyboard.Remove(this);
        _storyboard = null;

        foreach (var dot in _dots)
        {
            dot.Opacity = 0.6;
        }
    }

    /// <summary>控件离开视觉树后，动画必须停下来。</summary>
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualParent is null)
        {
            StopAnimation();
        }
    }
}
