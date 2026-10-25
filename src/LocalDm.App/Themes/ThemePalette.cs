using System.Windows;

namespace LocalDm.App.Themes;

/// <summary>
/// 深浅配色的开关：Shared.xaml 合并的是它，它按 <see cref="UseLight"/> 去读 Palette.Dark / Palette.Light。
/// 每个 View 都自己合并 Shared.xaml（界面测试不走 App.xaml），所以开关只能放在这一层；
/// 启动时读一次设置，改了要重启才生效（StaticResource 在加载时就取定了颜色）。
/// </summary>
public sealed class ThemePalette : ResourceDictionary
{
    public static bool UseLight { get; set; }

    public ThemePalette() =>
        Source = new Uri(
            UseLight ? "/LocalDm;component/Themes/Palette.Light.xaml" : "/LocalDm;component/Themes/Palette.Dark.xaml",
            UriKind.Relative);
}
