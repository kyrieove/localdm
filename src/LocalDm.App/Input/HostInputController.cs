using System.Windows.Input;
using LocalDm.Core.Hosting;

namespace LocalDm.App.Input;

/// <summary>
/// 主持台输入分流：保留原生 WPF 组合输入语义，Enter 发送、Shift+Enter 换行，
/// 状态意图只产生待确认提案。不使用输入停顿计时器，不模拟 OpenLess 快捷键。
/// </summary>
public sealed class HostInputController
{
    private readonly HostIntentRouter _router;

    public HostInputController(HostIntentRouter router)
    {
        _router = router;
    }

    /// <summary>
    /// 判断一次按键是否应当发送。IME 正在组合或候选确认时一律不发送。
    ///
    /// 判据是 <c>e.Key == Key.ImeProcessed</c>：按 WPF 的约定，IME 处于活动状态时
    /// KeyDown/KeyUp 的 <c>Key</c> 取值为 <c>Key.ImeProcessed</c>，而
    /// <c>ImeProcessedKey</c> 给的是<b>原始按键</b>（例如 Key.Enter），不是 ImeProcessed 本身。
    /// 因此拿 ImeProcessedKey 去比 ImeProcessed 永远不成立，是条死判据。
    /// </summary>
    public static bool ShouldSendOnEnter(KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            return false;
        }

        return ShouldSendOnEnterArgs(e.Key, composing: false, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    /// <summary>
    /// 与窗口无关的判定核心：便于在不启动真实窗口时验证输入行为。
    /// </summary>
    public static bool ShouldSendOnEnterArgs(Key key, bool composing, bool shift) =>
        (key == Key.Enter || key == Key.Return) && !composing && !shift;

    public HostIntentRouter Router => _router;

    /// <summary>
    /// 把一次已完成的文本提交分流：普通公共请求，或只产生提案的状态意图。
    /// </summary>
    public HostIntent ResolveIntent(string text)
    {
        return _router.Route(text);
    }
}
