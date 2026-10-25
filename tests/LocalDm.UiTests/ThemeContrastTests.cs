using System.Windows;
using System.Windows.Media;
using Xunit;

namespace LocalDm.UiTests;

/// <summary>
/// 「灯下」配色的每一对文字/底色都要 ≥4.5:1（WCAG AA 正文）。
/// 读的是软件实际加载的 Palette 字典，不在这里另抄一份色值。
/// </summary>
public sealed class ThemeContrastTests
{
    // 界面里真实出现的文字/底色配对：(字, 底, 出现在哪)
    private static readonly (string Text, string Back, string Where)[] Pairs =
    [
        ("Ink", "Paper", "正文"),
        ("Ink", "PaperDeep", "导航栏与环节面板"),
        ("Ink", "PaperDim", "卡片、输入框、默认按钮"),
        ("Ink", "Bubble", "气泡、悬停中的按钮"),
        ("Ink", "AvatarWash", "提示框、按下的按钮"),
        ("InkSoft", "Paper", "次要字"),
        ("InkSoft", "PaperDim", "卡片里的次要字"),
        ("InkSoft", "PaperDeep", "导航栏未选中项"),
        ("Muted", "Paper", "时间、说明"),
        ("Muted", "PaperDeep", "面板里的说明、禁用按钮字"),
        ("Muted", "PaperDim", "卡片里的说明、禁用按钮字"),
        ("Muted", "Bubble", "气泡里的时间"),
        ("Seal", "Paper", "强调字"),
        ("Seal", "PaperDeep", "当前环节"),
        ("Seal", "PaperDim", "卡片里的强调字"),
        ("OnSeal", "Seal", "主按钮字"),
        ("OnSeal", "SealHover", "悬停中的主按钮字"),
        ("OnSealWash", "SealWash", "暂停横幅、选中的导航项"),
        ("Ink", "SealWash", "暂停横幅正文"),
        ("SealDeep", "Paper", "警告与拒绝原因"),
        ("SealDeep", "PaperDeep", "面板里的警告"),
        ("SealDeep", "ErrorWash", "同步异常横幅"),
        ("Ink", "ErrorWash", "同步异常横幅正文"),
    ];

    [Theory]
    [InlineData("Palette.Dark.xaml")]
    [InlineData("Palette.Light.xaml")]
    public async Task Every_text_pair_meets_AA(string palette)
    {
        await WpfRunner.RunAsync(() =>
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/LocalDm;component/Themes/{palette}"),
            };
            var failures = new List<string>();
            foreach (var (text, back, where) in Pairs)
            {
                var ratio = Contrast(Color(dict, text), Color(dict, back));
                if (ratio < 4.5)
                {
                    failures.Add($"{where}：{text}/{back} = {ratio:0.00}");
                }
            }

            Assert.True(failures.Count == 0, $"{palette} 对比度不足：\n" + string.Join("\n", failures));
            return Task.CompletedTask;
        });
    }

    private static Color Color(ResourceDictionary dict, string key) =>
        Assert.IsType<SolidColorBrush>(dict[key]).Color;

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
