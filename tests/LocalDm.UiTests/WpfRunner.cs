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
