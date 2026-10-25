using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using LocalDm.App;
using LocalDm.Infrastructure.Diagnostics;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.App;

public partial class App : Application
{
    /// <summary>诊断日志写入锁：多处异常可能同时落盘。</summary>
    private static readonly object CrashLogLock = new();

    public App()
    {
        // 配色必须在 App.xaml 和第一个窗口加载 Shared.xaml 之前定下来，所以在构造函数里读设置。
        try
        {
            var root = SessionContext.ResolveDataRoot();
            Themes.ThemePalette.UseLight =
                new SettingsStore(Path.Combine(root, "settings.json"), Path.Combine(root, "secrets")).Load().LightTheme;
        }
        catch (Exception)
        {
            // 读不到设置就用默认深色：这时崩溃处理还没挂上，配色偏好不能挡住启动。
        }
    }

    private AppComposition? _composition;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject?.ToString() ?? "未知异常");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            LogCrash("TaskScheduler", args.Exception?.ToString() ?? "未知异常");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        LocalDm.Core.Diagnostics.RunLog.Folder = Path.Combine(SessionContext.ResolveDataRoot(), "logs");
        LocalDm.Core.Diagnostics.RunLog.Write("App", "软件启动");

        try
        {
            _composition = await AppComposition.CreateAsync(CancellationToken.None);
            var shell = await _composition.CreateShellAsync(CancellationToken.None);
            MainWindow window = new() { DataContext = shell };

            // 缩放与对话宽度由窗口落实：启动时读一次，设置页保存后再同步。
            var settings = _composition.Settings.Load();
            window.ApplyUiSettings(_composition.Settings, settings);
            UiWidth.ApplyChatWidth(shell, settings.ChatWidthMode);
            _composition.ApplyUiSettings = saved =>
            {
                window.ApplyUiSettings(_composition.Settings, saved);
                UiWidth.ApplyChatWidth(shell, saved.ChatWidthMode);
            };

            MainWindow = window;
            var closing = false;
            var stopping = false;
            window.Closing += async (_, args) =>
            {
                if (closing) { return; }
                args.Cancel = true;
                if (stopping) { return; }
                stopping = true;
                window.IsEnabled = false;
                try { await _composition.StopTabletAsync(); }
                catch (Exception ex) { LogCrash("TabletShutdown", ex.GetType().Name); }
                finally { closing = true; window.Close(); }
            };
            window.Closed += (_, _) =>
            {
                // 正常关闭不是崩溃，不写 crash.log：那份日志只放异常，
                // 混进日常噪声会淹没真正要查的线索。
                Shutdown();
            };
            window.Show();
            await _composition.StartTabletAsync();
        }
        catch (Exception ex)
        {
            LogCrash("StartupCatch", ex.ToString());
            // 启动故障只显示脱敏摘要与错误码，不把原始异常全文绑定到界面
            MessageBox.Show(
                "启动失败：" + RedactedLog.Redact(ex.GetType().Name) + "\n" + RedactedLog.Redact(ex.Message),
                "Dungeon Master",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 只有非正常退出才算异常：正常关窗不写 crash.log。
        if (e.ApplicationExitCode != 0)
        {
            LogCrash("OnExit", $"ExitCode={e.ApplicationExitCode}");
        }

        _composition?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("DispatcherUnhandledException", e.Exception.ToString());
        e.Handled = true;
        MessageBox.Show(
            "操作失败：" + RedactedLog.Redact(e.Exception.Message),
            "Dungeon Master",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// 写诊断日志。
    ///
    /// 落在运行数据根目录的 logs/ 下（%LOCALAPPDATA%\LocalDm\logs\crash.log），
    /// 不写死任何开发机路径 —— 打包版与便携版在别的机器上也必须留得下痕迹。
    /// 内容一律经 <see cref="RedactedLog"/> 脱敏：异常里可能带 caller key、
    /// 本机路径与剧本原文，日志同样受「不得包含未公开内容」的约束。
    /// </summary>
    private static void LogCrash(string tag, string detail)
    {
        try
        {
            lock (CrashLogLock)
            {
                var directory = Path.Combine(SessionContext.ResolveDataRoot(), "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}: {RedactedLog.Redact(detail)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 诊断日志写不进去也不能影响应用运行。
        }
    }
}
