using System.Threading;
using System.Windows;

namespace LocalDm.UiTests;

/// <summary>
/// WPF 控件必须在 STA 线程创建，且一个 AppDomain 只能有一个 Application。
/// 所有界面测试共享同一条 STA 线程与同一个 Application，串行执行。
/// </summary>
internal static class WpfRunner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Application? _app;
    private static System.Windows.Threading.Dispatcher? _dispatcher;

    public static async Task RunAsync(Func<Task> body)
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            EnsureApp();

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = _dispatcher!.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            await tcs.Task.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 把窗口显示到所有屏幕之外，然后调用 Show()。
    ///
    /// 测试需要真实窗口（布局、模板、可视树都要真的生成），但人正在用电脑时
    /// 不该被打断：这里把窗口挪到虚拟桌面左上角再往外退一个窗口宽高加 100，
    /// 接了几台显示器都落在可见范围之外。截图走 RenderTargetBitmap，
    /// 只要求窗口已经渲染，不要求它在屏幕上可见。
    ///
    /// 窗口的 Width / Height 由调用方决定，这里不碰。
    /// 以后新增的界面测试一律通过这个方法显示窗口，不要直接调用 window.Show()。
    /// </summary>
    public static void ShowOffscreen(Window window)
    {
        // 尺寸可能还没成型（没设 Width 时取 XAML 或默认值），两个都兜住。
        var width = window.Width is double w && !double.IsNaN(w) && w > 0 ? w : 800;
        var height = window.Height is double h && !double.IsNaN(h) && h > 0 ? h : 600;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.VirtualScreenLeft - width - 100;
        window.Top = SystemParameters.VirtualScreenTop - height - 100;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
    }

    private static void EnsureApp()
    {
        if (_dispatcher is not null)
        {
            return;
        }

        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(_dispatcher));
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
    }
}
