using System.Windows.Threading;
using LocalDm.App.ViewModels;
using LocalDm.Core.Tablet;

namespace LocalDm.App;

/// <summary>
/// 平板发言与桌面主持界面的桥接类。
/// 
/// 外部平板请求运行在 Kestrel 线程池上，而 <see cref="HostViewModel"/> 只允许在 WPF UI 线程被修改，
/// 必须经由 <see cref="Dispatcher"/> 进行串行化调度，防止多线程写破坏界面状态。
/// </summary>
public sealed class TabletSpeechBridge(HostViewModel host, Dispatcher dispatcher) : ITabletSpeech
{
    public bool IsBusy => host.IsBusy;

    public bool IsSpeaking => host.IsSpeaking;

    public Task<TabletSpeechAck> SubmitAsync(string roleName, string text)
    {
        return dispatcher.InvokeAsync(() => host.SubmitTabletSpeech(roleName, text)).Task;
    }

    public bool StopSpeaking()
    {
        return dispatcher.Invoke(host.StopSpeakingForTablet);
    }
}
