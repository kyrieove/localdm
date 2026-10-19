using System.IO;
using System.Windows;
using LocalDm.App.Input;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Magpie;
using LocalDm.Infrastructure.Storage;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.App;

/// <summary>
/// 组合根：唯一装配外部适配器与视图模型的地方。
/// 不把原包、工具 stderr 或私有剧本对象绑定到界面；界面只拿到脱敏状态与公开投影。
/// </summary>
public sealed class AppComposition : IDisposable
{
    private readonly SessionContext _context;

    private AppComposition(
        SessionContext context,
        MagpieGateway magpie,
        TtsService tts,
        HostDirector director,
        HostInputController input)
    {
        _context = context;
        Magpie = magpie;
        Tts = tts;
        Director = director;
        Input = input;
    }

    public MagpieGateway Magpie { get; }

    public TtsService Tts { get; }

    public HostDirector Director { get; }

    public HostInputController Input { get; }

    public SettingsStore Settings => _context.Settings;

    public string DataRoot => _context.DataRoot;

    public ISessionStore Store => _context.Store;

    public static async Task<AppComposition> CreateAsync(CancellationToken cancellationToken)
    {
        var context = await SessionContext.OpenAsync(dataRoot: null, cancellationToken);
        return await CreateFromContextAsync(context, cancellationToken);
    }

    /// <summary>供自动化测试注入独立的临时数据根目录。</summary>
    public static async Task<AppComposition> CreateFromContextAsync(
        SessionContext context,
        CancellationToken cancellationToken)
    {
        var settings = await context.Settings.LoadAsync(cancellationToken);

        var magpie = new MagpieGateway
        {
            BaseUri = settings.MagpieBaseUri,
            CallerKey = context.Settings.LoadCallerKey(settings.CallerKeyRef),
        };
        var tts = new TtsService();
        var director = new HostDirector(magpie, tts);
        var input = new HostInputController(new HostIntentRouter());

        return new AppComposition(context, magpie, tts, director, input);
    }

    public async Task<ShellViewModel> CreateShellAsync(CancellationToken cancellationToken)
    {
        var settings = await Settings.LoadAsync(cancellationToken);

        var setup = new SetupViewModel(Settings, Magpie, Tts, Director);
        var dispatcher = new LocalDm.Core.Game.CommandDispatcher(_context.Store);
        var host = new HostViewModel(
            Director,
            // 每次都从磁盘读当前设置：设置页刚保存的 model ID 立刻生效
            () => Settings.Load(),
            Input.Router,
            dispatcher,
            _context.Store);
        var library = new LibraryViewModel(Path.Combine(_context.DataRoot, "packages"));

        // 保存设置后把新地址与 caller key 同步到网关，不必重启
        setup.SettingsSaved += (_, saved) =>
        {
            Magpie.BaseUri = saved.MagpieBaseUri;
            Magpie.CallerKey = Settings.LoadCallerKey(saved.CallerKeyRef);
        };

        // 先建立壳，再让首页的开局动作装入剧本（切页由 ShellViewModel 负责）
        var shell = new ShellViewModel(library, setup, host);
        library.PackageOpened += async (_, package) => await host.LoadPackageAsync(package);

        _currentSettings = settings;

        await shell.InitializeAsync(cancellationToken);
        return shell;
    }

    /// <summary>最近一次读到的设置；主持页每次发送前重新读取，避免使用过期 model ID。</summary>
    public ToolSettings CurrentToolSettings => _currentSettings ?? ToolSettings.Default;

    private ToolSettings? _currentSettings;

    public void Dispose()
    {
        Tts.Dispose();
        Magpie.Dispose();
        _context.Dispose();
    }
}
