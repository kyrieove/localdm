using System.Windows;
using System.Windows.Threading;
using LocalDm.App;
using LocalDm.Infrastructure.Diagnostics;

namespace LocalDm.App;

public partial class App : Application
{
    private AppComposition? _composition;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            _composition = await AppComposition.CreateAsync(CancellationToken.None);
            var shell = await _composition.CreateShellAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = shell };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // 启动故障只显示脱敏摘要与错误码，不把原始异常全文绑定到界面
            MessageBox.Show(
                "启动失败：" + RedactedLog.Redact(ex.GetType().Name) + "\n" + RedactedLog.Redact(ex.Message),
                "本局主持",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _composition?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(
            "操作失败：" + RedactedLog.Redact(e.Exception.Message),
            "本局主持",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
