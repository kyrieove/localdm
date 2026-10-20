using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;

namespace LocalDm.App;

/// <summary>
/// 对话宽度：把设置里的档位换算成两条最大宽度，写进主持台的绑定。
///
/// 单独放一个类型而不是挂在窗口上：App 里有一个 <c>MainWindow</c> 属性与一个同名类型，
/// 静态方法的写法容易落到属性上；这里不依赖窗口实例，测试也能直接调。
/// </summary>
public static class UiWidth
{
    /// <summary>
    /// 对话列表与底部输入区一起改；满宽档不限宽，只留两端 34 的边距。
    /// 玩家消息气泡按同一比例放宽。
    /// </summary>
    public static void ApplyChatWidth(ShellViewModel? shell, ChatWidthMode mode)
    {
        if (shell?.Host is not { } host)
        {
            return;
        }

        host.ChatMaxWidth = ToolSettings.ChatMaxWidth(mode) ?? double.PositiveInfinity;
        host.BubbleMaxWidth = ToolSettings.ChatBubbleMaxWidth(mode) ?? double.PositiveInfinity;
    }
}
