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

    /// <summary>
    /// 把窗口放到屏幕外。界面测试必须真的 Show() 才能量布局、渲染快照，
    /// 但跑测试的人不该被一路闪过的窗口打断；屏幕外位置让这两件事同时成立。
    /// 只改位置，不动尺寸、可见性与渲染，所以快照与断言结果都不受影响。
    /// </summary>
    public static void MoveOffScreen(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
    }

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
