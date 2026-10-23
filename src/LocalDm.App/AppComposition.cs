using System.IO;
using System.Windows;
using LocalDm.App.Demo;
using LocalDm.App.Input;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Import;
using LocalDm.Infrastructure.Magpie;
using LocalDm.Infrastructure.Storage;
using LocalDm.Infrastructure.Voice;
using LocalDm.Infrastructure.Voice.Dictation;

namespace LocalDm.App;

/// <summary>
/// 组合根：唯一装配外部适配器与视图模型的地方。
/// 不把原包、工具 stderr 或私有剧本对象绑定到界面；界面只拿到脱敏状态与公开投影。
/// </summary>
public sealed class AppComposition : IDisposable
{
    private readonly SessionContext _context;
    private readonly Task _ttsWarmup;
    private IDictationService? _dictation;
    private HostViewModel? _host;
    private TabletJoinViewModel? _tablet;
    private LibraryViewModel? _library;

    private AppComposition(
        SessionContext context,
        MagpieGateway magpie,
        TtsService tts,
        HostDirector director,
        HostInputController input,
        Task ttsWarmup)
    {
        _context = context;
        _ttsWarmup = ttsWarmup;
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

    /// <summary>
    /// 界面缩放与对话宽度变了：窗口据此改 LayoutTransform 与宽度绑定。
    /// 组合根不认识窗口，由宿主（App）挂上来。
    /// </summary>
    public Action<ToolSettings>? ApplyUiSettings { get; set; }

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

        // 探测 Edge-TTS 依赖要起 python 进程，最多几秒。放到后台先跑，
        // 之后界面线程问「有没有神经音色」就是瞬时的，不会在启动时卡住。
        var warmup = EdgeTtsEngine.WarmUpAsync();

        return new AppComposition(context, magpie, tts, director, input, warmup);
    }

    public async Task<ShellViewModel> CreateShellAsync(CancellationToken cancellationToken)
    {
        var settings = await Settings.LoadAsync(cancellationToken);

        // 探测最长 5 秒：有界等待，超时先放行。
        // 别把主窗口的出现推迟到探测结束 —— 那正是我们要解决的「点了没反应」。
        await Task.WhenAny(_ttsWarmup, Task.Delay(TimeSpan.FromSeconds(1)));

        var setup = new SetupViewModel(Settings, Magpie, Tts, Director);
        var dispatcher = new LocalDm.Core.Game.CommandDispatcher(_context.Store);
        _dictation = new DictationService(new DictationModelStore(_context.DataRoot));
        var host = new HostViewModel(
            Director,
            // 每次都从磁盘读当前设置：设置页刚保存的 model ID 立刻生效
            () => Settings.Load(),
            Input.Router,
            dispatcher,
            _context.Store,
            Tts,
            // 主持台切换音色：读改写当前设置，不覆盖其它字段。
            // 写失败要把原因带回界面，不能静默丢掉这次选择。
            voiceId =>
            {
                try
                {
                    var current = Settings.Load();
                    Settings.Save(current with { VoiceId = voiceId });
                    return null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] 保存音色失败：" + ex.Message);
                    return "设置文件写入失败";
                }
            },
            _dictation);
        var library = new LibraryViewModel(Path.Combine(_context.DataRoot, "packages"));
        _host = host;
        var feedSource = new CurrentSessionPublicFeedSource((LocalDm.Core.Ports.IPublicMessageFeed)_context.Store, () => host.Session);
        var tablet = new TabletJoinViewModel(
            feedSource,
            speech: new TabletSpeechBridge(host, System.Windows.Application.Current.Dispatcher),
            ui: new CurrentSessionTabletUiSource(feedSource, () => host.Session, System.Windows.Application.Current.Dispatcher));
        setup.Tablet = tablet;
        _tablet = tablet;
        library.Tablet = tablet;
        _library = library;

        async Task LoadCurrentPackageAsync(ScriptPackage package)
        {
            // 先关旧局的网页，再换桌面会话，避免旧卡目录读到新局的消息。
            var row = library.AllPackages.FirstOrDefault(p => p.PackageId == package.PackageId);
            await tablet.SetPackageAsync(package, row?.FilePath, async () =>
            {
                await host.LoadPackageAsync(package);
                if (!ReferenceEquals(host.Session.Package, package))
                {
                    throw new InvalidOperationException("桌面剧本未装载完成，平板服务保持关闭。");
                }
            });
        }

        // 导入向导：识别与建包走同一份服务，起草流程用同一个模型。
        // 服务每次现造：设置页刚改的 MinerU / OCR 路径立刻生效。
        var wizard = new ImportWizardViewModel(
            _context.DataRoot,
            Path.Combine(_context.DataRoot, "packages"),
            () =>
            {
                var current = Settings.Load();
                return new ScriptImportService(new ScriptImportOptions
                {
                    DataRoot = _context.DataRoot,
                    MineruExecutable = current.MineruExecutable,
                    OcrPythonPath = current.OcrPythonPath,
                });
            },
            new FlowDrafter(Magpie),
            () => Settings.Load().ModelId);

        // 保存设置后把新地址与 caller key 同步到网关，不必重启；
        // 音色也要同步到主持台，否则主持台还在用启动时的旧音色朗读。
        setup.SettingsSaved += (_, saved) =>
        {
            Magpie.BaseUri = saved.MagpieBaseUri;
            Magpie.CallerKey = Settings.LoadCallerKey(saved.CallerKeyRef);
            host.ApplySettingsVoice(saved.VoiceId);
            ApplyUiSettings?.Invoke(saved);
        };

        // 启动时先把上次的缩放与对话宽度交给窗口。
        ApplyUiSettings?.Invoke(settings);

        // 先建立壳，再让首页的开局动作装入剧本（切页由 ShellViewModel 负责）
        var shell = new ShellViewModel(library, setup, host, wizard);

        // 导入建好一本：装进主持台，并把剧本库刷新一遍（新剧本要出现在列表里）。
        shell.ImportCompleted += async (_, package) =>
        {
            try
            {
                await library.LoadAsync(CancellationToken.None);
                await LoadCurrentPackageAsync(package);
                shell.ShowHostCommand.Execute(null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] 导入后装载剧本失败：" + ex);
                host.ReportProblem("导入完成，但装载失败，请从剧本库重新打开。");
            }
        };
        library.PackageOpened += async (_, package) =>
        {
            try
            {
                await LoadCurrentPackageAsync(package);
            }
            catch (Exception ex)
            {
                // 事件处理器是 async void：异常必须在这里吃掉，
                // 否则会变成未处理异常把整个应用带走。
                System.Diagnostics.Debug.WriteLine("[App] 装载剧本失败：" + ex);
                host.ReportProblem("装载剧本失败，请重试或换一个剧本。");
            }
        };

        _currentSettings = settings;

        await shell.InitializeAsync(cancellationToken);

        // 找出一局没走完的存档并给出「继续这一局」入口：
        // 重启后主持人不该自己重新翻包找进度。
        var resumable = await FindResumableAsync(library, cancellationToken);
        if (resumable is not null)
        {
            library.SetResumable(resumable.Value.Package, resumable.Value.Session);
        }

        // 存档还在、绑定的包不在了：必须明说不能恢复。
        // 否则重新准备时改了流程，旧存档会静静消失（或者更糟：被新包顶上去跳过新环节）。
        library.SetOrphanNotice(await DescribeOrphanedSessionsAsync(library, cancellationToken));

        library.ContinueRequested += async (_, package) =>
        {
            try
            {
                await LoadCurrentPackageAsync(package);
                shell.ShowHostCommand.Execute(null);
                // 这一局已经接上了，首页那张卡片不再有代表性。
                library.ClearResumable();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] 继续上一局失败：" + ex);
                host.ReportProblem("继续这一局失败，请重试。");
            }
        };

        // 每次回到首页都重算一次：那局可能已经玩完了。
        shell.LibraryShown += async (_, _) =>
        {
            var latest = await FindResumableAsync(library, CancellationToken.None);
            if (latest is null)
            {
                library.ClearResumable();
            }
            else
            {
                library.SetResumable(latest.Value.Package, latest.Value.Session);
            }
        };

        // 第一次用的人没有剧本包：首页直接给一条能走的路。
        library.DemoRequested += async (_, _) =>
        {
            try
            {
                await tablet.SetPackageAsync(null, null, () => host.LoadDemoAsync());
                shell.ShowHostCommand.Execute(null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] 装载演示剧本失败：" + ex);
                host.ReportProblem("装载演示剧本失败，请重试。");
            }
        };

        return shell;
    }

    /// <summary>
    /// 找出「存档还在、绑定的冻结包不在了」的局面并给出一句说明。
    /// 只读绑定信息，不碰剧情内容。
    /// </summary>
    private async Task<string?> DescribeOrphanedSessionsAsync(
        LibraryViewModel library,
        CancellationToken cancellationToken)
    {
        var knownPackageIds = library.AllPackages
            .Select(r => r.PackageId)
            .Append(DemoPackage.PackageId)
            .ToHashSet(StringComparer.Ordinal);

        var stored = await _context.Store.ListSessionsAsync(cancellationToken);
        var orphaned = stored
            .Where(s => s.State != SessionState.Completed
                        && s.Revision > 0
                        && !knownPackageIds.Contains(s.PackageId))
            .ToArray();

        return orphaned.Length == 0
            ? null
            : $"有 {orphaned.Length} 局存档绑定的剧本包已经不在了（通常是重新准备时流程被改写）。"
              + "为了不跳过环节，不会用新包继续这一局；请找回原来的冻结包，或按新流程重开一局。";
    }

    /// <summary>找出进度最靠前的一局未完成存档。</summary>
    private async Task<(ScriptPackage Package, Session Session)?> FindResumableAsync(
        LibraryViewModel library,
        CancellationToken cancellationToken)
    {
        ScriptPackage? bestPackage = null;
        Session? bestSession = null;

        // 包目录里的剧本 + 内置演示剧本（演示不在目录里，但它的存档也可能没走完）
        var candidates = library.AllPackages
            .Select(r => (Package: r.Package, SessionId: SessionIds.For(r.Package)))
            .ToList();
        candidates.Add((DemoPackage.Create(), DemoPackage.SessionId));

        foreach (var (package, sessionId) in candidates)
        {
            var session = await _context.Store.LoadSessionAsync(sessionId, cancellationToken);
            if (session is null || session.State == SessionState.Completed || session.Revision == 0)
            {
                continue;
            }

            if (bestSession is null || session.Revision > bestSession.Revision)
            {
                bestPackage = package;
                bestSession = session;
            }
        }

        return bestPackage is null || bestSession is null
            ? null
            : (bestPackage, bestSession);
    }

    /// <summary>最近一次读到的设置；主持页每次发送前重新读取，避免使用过期 model ID。</summary>
    public ToolSettings CurrentToolSettings => _currentSettings ?? ToolSettings.Default;

    private ToolSettings? _currentSettings;

    /// <summary>软件启动后调一次：起平板服务，并在后台给已有剧本包补平板材料（读旁路文件，没有才准备）。</summary>
    public async Task StartTabletAsync()
    {
        if (_tablet is null) { return; }
        await _tablet.StartAsync();
        var rows = _library?.AllPackages.ToArray() ?? [];
        _ = Task.Run(() =>
        {
            foreach (var row in rows.Where(r => r.Package.Tablet is null && !string.IsNullOrEmpty(r.FilePath)))
            {
                try { LocalDm.Infrastructure.Import.TabletMaterials.Ensure(row.Package, row.FilePath); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Tablet] 后台补材料失败：" + ex.GetType().Name); }
            }
        });
    }

    public Task StopTabletAsync() => _tablet?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    public void Dispose()
    {
        if (_tablet is not null)
        {
            Task.Run(async () => await _tablet.DisposeAsync()).GetAwaiter().GetResult();
        }
        _host?.Dispose();
        _dictation?.Dispose();
        Tts.Dispose();
        Magpie.Dispose();
        _context.Dispose();
    }}
