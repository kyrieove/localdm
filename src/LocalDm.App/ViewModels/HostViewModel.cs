using System.Collections.ObjectModel;
using LocalDm.App.Demo;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 主持页：合成口播、发送、停止和重播，加上确定性完整局的阶段操作。
/// 只绑定公开投影与公开文字，不持有完整剧本或真相对象。
/// </summary>
public sealed class HostViewModel : ObservableObject
{
    private readonly HostDirector _director;
    private readonly Func<ToolSettings> _settingsProvider;
    private readonly HostIntentRouter _router;
    private readonly CommandDispatcher? _dispatcher;
    private readonly ISessionStore? _store;
    private readonly SessionViewModel _session;
    private string _inputText = string.Empty;
    private string _lastSpokenText = string.Empty;
    private string _status = "等待开始。界面不会主动请求模型。";
    private SpeechState _speechState = SpeechState.Idle;
    private bool _busy;
    private string _proposal = string.Empty;

    public HostViewModel(
        HostDirector director,
        Func<ToolSettings> settingsProvider,
        HostIntentRouter router,
        CommandDispatcher? dispatcher = null,
        ISessionStore? store = null)
    {
        _director = director;
        _settingsProvider = settingsProvider;
        _router = router;
        _dispatcher = dispatcher;
        _store = store;
        _session = dispatcher is null ? new SessionViewModel(new CommandDispatcher(new UnwiredSessionStore())) : new SessionViewModel(dispatcher);
        _session.PropertyChanged += OnSessionPropertyChanged;

        // 每次状态提交成功后，按对应意图请模型生成播报措辞。
        // 状态已由程序裁决，这里只是「把刚发生的事说出来」。
        _session.Committed += async (_, type) => await NarrateAfterCommandAsync(type);

        SpeakCommand = new RelayCommand(SpeakAsync, () => !Busy);
        SendCommand = new RelayCommand(SendAsync, () => !Busy);
        StopCommand = new RelayCommand(() => { _director.Stop(); Status = "已停止播音。"; return Task.CompletedTask; });
        ReplayCommand = new RelayCommand(ReplayAsync, () => !Busy && !string.IsNullOrEmpty(LastSpokenText));
        LoadDemoCommand = new RelayCommand(LoadDemoAsync, () => !Busy);
        ResumeCommand = new RelayCommand(ResumeAsync, () => !Busy && _dispatcher is not null);
    }

    public ObservableCollection<HostLogEntry> Log { get; } = [];

    public SessionViewModel Session => _session;

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    public string LastSpokenText
    {
        get => _lastSpokenText;
        private set => SetProperty(ref _lastSpokenText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Proposal
    {
        get => _proposal;
        private set => SetProperty(ref _proposal, value);
    }

    public SpeechState SpeechState
    {
        get => _speechState;
        private set => SetProperty(ref _speechState, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                SpeakCommand.RaiseCanExecuteChanged();
                SendCommand.RaiseCanExecuteChanged();
                ReplayCommand.RaiseCanExecuteChanged();
                LoadDemoCommand.RaiseCanExecuteChanged();
                ResumeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand SpeakCommand { get; }

    public RelayCommand SendCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand ReplayCommand { get; }

    public RelayCommand LoadDemoCommand { get; }

    public RelayCommand ResumeCommand { get; }

    /// <summary>默认不启动任何模型请求。</summary>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        AnswerWithPublic("欢迎回来。点击「装载演示剧本」可以走完一局；点击「公共开场」会请求本机模型。");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 装载一个已冻结的剧本包（来自首页选择）。不自动开始、不自动播音。
    /// 同一包若已有存档则接着用，不覆盖进度。
    /// </summary>
    public async Task LoadPackageAsync(ScriptPackage package)
    {
        if (_dispatcher is null || _store is null)
        {
            Status = "当前没有可用存档。";
            return;
        }

        if (package.Validation != PackageValidationState.Ready)
        {
            Status = "这个剧本包还不是 Ready，不能开局。";
            return;
        }

        var sessionId = "session-" + package.PackageId;
        try
        {
            var session = await _store.LoadSessionAsync(sessionId, CancellationToken.None);
            session ??= new Session
            {
                SessionId = sessionId,
                PackageId = package.PackageId,
                PackageVersion = package.Version,
                ManifestHash = package.ManifestHash,
                State = SessionState.Prepared,
                CurrentPhaseId = package.Phases[0].PhaseId,
                Revision = 0,
                ReleasedMaterialIds = [],
            };
            await _store.SaveSessionAsync(session, CancellationToken.None);
            if (_store is SessionStore concrete)
            {
                await concrete.RegisterPackageAsync(package, CancellationToken.None);
            }

            _activePendingPackage = package;
            _session.Attach(package, session);
            OnPropertyChanged(nameof(SessionSummary));
            Status = $"已装载「{package.Title}」：{package.PlayerCount} 人、{package.Spans.Count} 页来源。请勾选准备后开始。";
            AnswerWithPublic($"已装载「{package.Title}」。请先确认全员就位，再点开始。");
        }
        catch (DomainRuleException ex)
        {
            Status = ex.PublicReason;
        }
    }

    /// <summary>当前装载的真实剧本包；用于「继续一局」时校验指纹。</summary>
    private ScriptPackage? _activePendingPackage;

    /// <summary>装载演示剧本并建立/恢复一局；不自动开始、不自动播音。</summary>
    public async Task LoadDemoAsync()
    {
        if (_dispatcher is null || _store is null)
        {
            Status = "当前没有可用存档。";
            return;
        }

        try
        {
            var package = DemoPackage.Create();
            var session = await _store.LoadSessionAsync(DemoPackage.SessionId, CancellationToken.None);
            session ??= new Session
            {
                SessionId = DemoPackage.SessionId,
                PackageId = package.PackageId,
                PackageVersion = package.Version,
                ManifestHash = package.ManifestHash,
                State = SessionState.Prepared,
                CurrentPhaseId = package.Phases[0].PhaseId,
                Revision = 0,
                ReleasedMaterialIds = [],
            };
            await _store.SaveSessionAsync(session, CancellationToken.None);
            if (_store is SessionStore concrete)
            {
                await concrete.RegisterPackageAsync(package, CancellationToken.None);
            }

            _activePendingPackage = package;
            _session.Attach(package, session);
            OnPropertyChanged(nameof(SessionSummary));
            Status = "演示剧本已装载：请勾选「大家已口头准备好」后再开始。";
            AnswerWithPublic("演示剧本已就绪。请先勾选「大家已口头准备好」，再点开始。");
        }
        catch (DomainRuleException ex)
        {
            Status = ex.PublicReason;
        }
    }

    /// <summary>继续一局：检查包指纹并恢复；恢复后不自动播音。</summary>
    public async Task ResumeAsync()
    {
        if (_store is null)
        {
            Status = "当前没有可用存档。";
            return;
        }

        // 优先用当前装载的真实剧本；没有装载时退回演示剧本
        var package = _activePendingPackage ?? DemoPackage.Create();
        var sessionId = _activePendingPackage is null
            ? DemoPackage.SessionId
            : "session-" + package.PackageId;
        var recovery = new SessionRecovery(_store);
        var result = await recovery.RecoverAsync(package, sessionId, CancellationToken.None);
        if (!result.Recovered || result.Session is null)
        {
            Status = result.PublicReason ?? "无法恢复这一局。";
            return;
        }

        _director.ResetToIdle();
        SpeechState = SpeechState.Idle;
        _session.Attach(package, result.Session);
        OnPropertyChanged(nameof(SessionSummary));
        Status = $"已恢复：revision={result.Session.Revision}，state={result.Session.State}。不会自动重播。";
    }

    /// <summary>公共开场：把“开场”这个意图交给模型，由模型组织措辞。</summary>
    public Task SpeakAsync() => NarrateAsync(HostNarrationIntent.OpenSession);

    /// <summary>
    /// 一次状态提交后的播报。没配置模型就静默跳过，不覆盖「已提交」的状态提示。
    /// </summary>
    private async Task NarrateAfterCommandAsync(GameCommandType type)
    {
        var settings = _settingsProvider();
        if (string.IsNullOrWhiteSpace(settings.ModelId))
        {
            return;
        }

        var intent = type switch
        {
            GameCommandType.Start => HostNarrationIntent.AnnouncePhase,
            GameCommandType.Advance => HostNarrationIntent.AnnouncePhase,
            GameCommandType.ReleaseMaterials => HostNarrationIntent.ReleaseMaterials,
            GameCommandType.SubmitVoteSummary => HostNarrationIntent.CallVote,
            GameCommandType.Reveal => HostNarrationIntent.RevealTruth,
            GameCommandType.Complete => HostNarrationIntent.EnterReview,
            GameCommandType.Pause => HostNarrationIntent.PauseSession,
            GameCommandType.Resume => HostNarrationIntent.AnnouncePhase,
            _ => HostNarrationIntent.AnnouncePhase,
        };

        try
        {
            await NarrateAsync(intent);
        }
        catch (DomainRuleException ex)
        {
            // 播报失败不改变已提交的进度
            Status = "播报未完成：" + ex.PublicReason;
        }
    }

    public async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 状态意图只出提案，绝不能凭一句话改状态
        var intent = _router.Route(text);
        if (intent.Kind == HostIntentKind.ProposeCommand)
        {
            ProposeCommand(intent.ProposedAction == PublicAction.Reveal ? "揭秘" : "下一环节");
            InputText = string.Empty;
            return;
        }

        var settings = _settingsProvider();
        if (string.IsNullOrWhiteSpace(settings.ModelId))
        {
            Status = "还没有选择 model ID。请先在设置页读取模型列表。";
            return;
        }

        var package = _session.Package;
        var session = _session.Session;
        if (package is null || session is null)
        {
            Status = "还没有装载剧本，先选一本再提问。";
            return;
        }

        Busy = true;
        SpeechState = SpeechState.GeneratingText;
        Status = "正在请求本机模型…";
        try
        {
            // 玩家/主持人的原话 + 当前公开上下文一起交给模型；回复不审查，直接念
            var messages = HostNarration.BuildAskMessages(package, session, text);
            var (reply, reason, late) = await _director.GenerateOnlyAsync(
                settings.ModelId, messages, CancellationToken.None);

            if (late)
            {
                Status = reason ?? "已取消，晚到的回复不会显示。";
                return;
            }

            if (reply is null)
            {
                SpeechState = SpeechState.Error;
                Status = reason ?? "模型这次没有给出可用内容。";
                return;
            }

            var result = await _director.SpeakApprovedAsync(reply, settings.VoiceId, CancellationToken.None);
            ApplyResult(result);
            if (result.Item is not null)
            {
                InputText = string.Empty;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task ReplayAsync()
    {
        Busy = true;
        try
        {
            var settings = _settingsProvider();
            var result = await _director.ReplayAsync(settings.VoiceId, CancellationToken.None);
            ApplyResult(result, isReplay: true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>状态意图只产生待确认提案，不调用 CommandDispatcher、不改状态。</summary>
    public void ProposeCommand(string proposal)
    {
        Proposal = proposal;
        Status = "这只是待确认提案：需要在阶段面板里手动确认后才会提交。";
    }

    /// <summary>
    /// 按主持意图请求模型措辞，经公开政策核对后朗读。
    /// 按钮给的是「现在要做什么」，措辞由模型生成，程序只核对能否回指公开原文。
    /// </summary>
    public async Task NarrateAsync(HostNarrationIntent intent)
    {
        var settings = _settingsProvider();
        if (string.IsNullOrWhiteSpace(settings.ModelId))
        {
            Status = "还没有选择 model ID，主持词暂时无法生成；流程操作仍可用。";
            return;
        }

        var package = _session.Package;
        var session = _session.Session;
        if (package is null || session is null)
        {
            Status = "还没有装载剧本，先选一本再主持。";
            return;
        }

        Busy = true;
        SpeechState = SpeechState.GeneratingText;
        Status = "正在生成主持词…";
        try
        {
            var messages = HostNarration.BuildMessages(package, session, intent);
            var (text, reason, late) = await _director.GenerateOnlyAsync(
                settings.ModelId, messages, CancellationToken.None);

            if (late)
            {
                Status = reason ?? "已取消。";
                return;
            }

            if (text is null)
            {
                SpeechState = SpeechState.Error;
                Status = reason ?? "模型这次没有给出可用内容。";
                return;
            }

            // 产品决定：不审查模型输出，回什么就念什么。
            // 秘密隔离发生在拼提示词处（HostNarration 只取 Public 段），那里才是可建立标准的地方；
            // 对自然语言做输出审查没有可靠标准，反而会毁掉主持的自然度。
            var result = await _director.SpeakApprovedAsync(text, settings.VoiceId, CancellationToken.None);
            ApplyResult(result);
            if (result.Item is not null)
            {
                Status = "已朗读。";
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private void ApplyResult(HostDirector.HostTurnResult result, bool isReplay = false)
    {
        SpeechState = result.State;
        if (result.Item is not null)
        {
            AnswerWithPublic(result.Item.SafeText);
            Status = isReplay ? "已重播已保存的口播，没有请求模型。" : "已朗读。";
        }
        else if (result.PublicReason is not null)
        {
            Status = result.PublicReason;
        }

        ReplayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>会话状态变化时把权威状态同步到主持台头部，避免显示装载时的旧值。</summary>
    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.SessionStateText) or nameof(SessionViewModel.Revision) or nameof(SessionViewModel.PhaseTitle))
        {
            OnPropertyChanged(nameof(SessionSummary));
        }
    }

    /// <summary>头部摘要只包含阶段、状态与 revision，不含任何私有内容。</summary>
    public string SessionSummary =>
        _session.Projection is null
            ? "当前没有装载一局。"
            : $"{_session.PhaseTitle} · {_session.SessionStateText} · revision {_session.Revision}";

    public void AnswerWithPublic(string text)
    {
        LastSpokenText = text;
        Log.Insert(0, new HostLogEntry(DateTime.Now.ToString("HH:mm:ss"), text));
        while (Log.Count > 50)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    /// <summary>未接线的存储：任何提交都显式失败，避免误以为已经写入。</summary>
    private sealed class UnwiredSessionStore : ISessionStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<Session?>(null);

        public Task<OperationReceipt?> FindReceiptAsync(string sessionId, string operationId, CancellationToken cancellationToken) =>
            Task.FromResult<OperationReceipt?>(null);

        public Task CommitAsync(
            Session current,
            Session next,
            GameCommand command,
            GameEvent gameEvent,
            OperationReceipt receipt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("未接线的会话存储不能提交命令。");

        public Task<IReadOnlyList<GameEvent>> ReadEventsAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameEvent>>([]);

        public Task SaveSessionAsync(Session session, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("未接线的会话存储不能保存。");
    }
}

public sealed record HostLogEntry(string Time, string Text);
