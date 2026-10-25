using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.App;

/// <summary>
/// 整窗缩放：Ctrl+加号 / Ctrl+减号 / Ctrl+0 / Ctrl+滚轮。
///
/// 缩放放在主窗口内容根的 LayoutTransform 上（见 MainWindow.xaml 的 UiScale），
/// 用 LayoutTransform 而不是 RenderTransform：放大的同时布局也跟着重排，
/// 否则 200% 下右上角的按钮会被推出窗口、左栏也量不到真实可用宽度。
/// 改完立即写进设置文件，下次启动保持上次的倍数。
/// </summary>
public partial class MainWindow
{
    private SettingsStore? _settingsStore;

    /// <summary>当前界面的缩放内容根：MainWindow.xaml 里带 LayoutTransform 的那个元素。</summary>
    public FrameworkElement UiRoot => UiScaleRoot;

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    /// <summary>从设置文件读缩放，并记下同一个存储，改完立即写回。</summary>
    public void ApplyUiSettings(SettingsStore settingsStore, ToolSettings settings)
    {
        _settingsStore = settingsStore;
        UiScale.ScaleX = settings.UiScale;
        UiScale.ScaleY = settings.UiScale;
    }

    /// <summary>把缩放调到指定倍数（超出范围就夹到范围里），并立即保存。</summary>
    public void SetUiScale(double scale)
    {
        var clamped = Math.Clamp(scale, ToolSettings.MinUiScale, ToolSettings.MaxUiScale);
        var rounded = Math.Round(clamped, 2);
        if (Math.Abs(UiScale.ScaleX - rounded) < 0.001)
        {
            return;
        }

        UiScale.ScaleX = rounded;
        UiScale.ScaleY = rounded;
        SaveUiSettings();
    }

    /// <summary>Ctrl+0：回到 1.0 倍。</summary>
    public void ResetUiScale() => SetUiScale(ToolSettings.DefaultUiScale);

    /// <summary>加上或减去一格。</summary>
    public void StepUiScale(int direction) =>
        SetUiScale(UiScale.ScaleX + direction * ToolSettings.UiScaleStep);

    private void SaveUiSettings()
    {
        if (_settingsStore is null)
        {
            return;
        }

        try
        {
            _settingsStore.Save(_settingsStore.Load() with { UiScale = UiScale.ScaleX });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去不能把界面卡住：这一次缩放仍然生效，只是下次启动回到旧倍数。
            LocalDm.Core.Diagnostics.RunLog.Write("Settings", "保存界面缩放失败：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(Convert.ToString(ex.Message)));
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        // OemPlus / Add 都是「加号」键：主键盘与小键盘各发一个。
        switch (e.Key)
        {
            case Key.OemPlus when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
            case Key.Add:
                StepUiScale(1);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                StepUiScale(-1);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                ResetUiScale();
                e.Handled = true;
                break;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Delta == 0)
        {
            return;
        }

        StepUiScale(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnTabletEntryClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { Library.Tablet: { } tablet })
        {
            return;
        }

        var panel = new StackPanel { Margin = new Thickness(28), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "平板扫码加入",
            FontSize = 20,
            FontFamily = (FontFamily)FindResource("DisplayFace"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var qr = new Image { Source = tablet.QrCode, Width = 280, Height = 280, Margin = new Thickness(0, 14, 0, 10) };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        panel.Children.Add(qr);
        panel.Children.Add(new TextBlock
        {
            Text = tablet.SelectedAddress ?? tablet.Status,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        // 平板关了浏览器或换 App 扫码会认不回角色：在这里释放后让它重新认领。
        foreach (var role in tablet.JoinedRoles)
        {
            var release = new Button
            {
                Content = $"释放「{role.DisplayName}」",
                Style = (Style)FindResource("QuietButton"),
                Margin = new Thickness(0, 8, 0, 0),
                ToolTip = "平板认不回这个角色时点这里，再在平板上重新选它；翻开的页要重新翻",
            };
            release.Click += (_, _) =>
            {
                role.ReleaseCommand.Execute(null);
                panel.Children.Remove(release);
            };
            panel.Children.Add(release);
        }

        new Window
        {
            Title = "平板入口",
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Paper"),
        }.ShowDialog();
    }
}
