using System.Collections.ObjectModel;
using System.Windows.Threading;
using LocalDm.App.Demo;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 主持台：一条连续对话 + 一个底边固定输入区。
///
/// 玩家输入、主持回复与阶段事件按时间顺序排在同一个 <see cref="Messages"/> 里；
/// 生成、朗读、停止、失败都在对应消息上就地显示，不再另设常驻状态栏。
/// 只绑定公开投影与公开文字，不持有完整剧本或真相对象。
/// </summary>
public sealed class HostViewModel : ObservableObject, IDisposable
{
    private readonly HostDirector _director;
    private readonly Func<ToolSettings> _settingsProvider;
    private readonly HostIntentRouter _router;

    /// <summary>
    /// 本局的 DM 对话记忆：发给模型的历史、备忘与真实 token 用量。
    /// 屏幕上的聊天记录不受它影响：这里只决定发给模型什么。
    /// </summary>
    private DmConversation _conversation = new();

    private string? _conversationSessionId;
    private readonly CommandDispatcher? _dispatcher;
    private readonly ISessionStore? _store;
    private readonly SessionViewModel _session;
    private readonly ITtsService? _tts;
    private readonly SemaphoreSlim _conversationSaveGate = new(1, 1);

    /// <summary>
    /// 音色变更回调：把选择落盘。返回 null 表示成功，返回文案表示失败原因。
    /// 落盘失败必须由调用方展示给主持人，不能静默吞掉。
    /// </summary>
    private readonly Func<string, string?>? _onVoiceChanged;
    private readonly IDictationService? _dictation;
    private bool _dictationBusy;
    private DispatcherTimer? _dictationTimer;
    private bool _isListening;
    private string _dictationHint = "";

    private string? _selectedVoiceId;

    /// <summary>初始化或外部同步时抑制「已切换音色」提示与落盘回调。</summary>
    private bool _suppressVoiceFeedback;
    private string _inputText = string.Empty;
    private string _status = "等待开始。界面不会主动请求模型。";
    private SpeechState _speechState = SpeechState.Idle;
    private bool _isGenerating;
    private bool _isSpeaking;
    private bool _showMaterials;
    private CorrectionKind? _pendingCorrection;
    private int _pendingRevision;

    public bool HasPendingCorrection => _pendingCorrection is not null;
    public string PendingCorrectionText => _pendingCorrection switch
    {
        CorrectionKind.RevertPhase => $"DM 提议：回到上一环节「{_session.PreviousPhaseTitle}」。",
        CorrectionKind.CorrectVote => "DM 提议：重新录入这一轮票数。",
        _ => string.Empty,
    };
    public RelayCommand ConfirmCorrectionCommand { get; }
    public RelayCommand DismissCorrectionCommand { get; }

    /// <summary>
    /// 当前这一轮请求的序号。停止时自增，让已经在飞的请求认输出局：
    /// 晚到的文字既不上屏，也不朗读 —— 否则「停止」会变成「暂停一下然后接着说」。
    /// </summary>
    private int _generation;

    /// <summary>正在朗读的那条消息；停止时要把它的就地状态改成「已停止」。</summary>
    private ChatMessage? _speakingMessage;

    public HostViewModel(
        HostDirector director,
        Func<ToolSettings> settingsProvider,
        HostIntentRouter router,
        CommandDispatcher? dispatcher = null,
        ISessionStore? store = null,
        ITtsService? tts = null,
        Func<string, string?>? onVoiceChanged = null,
        IDictationService? dictation = null)
    {
        _director = director;
        _settingsProvider = settingsProvider;
        _router = router;
        _dispatcher = dispatcher;
        _store = store;
        _tts = tts;
        _onVoiceChanged = onVoiceChanged;
        _dictation = dictation;

        if (_dictation is not null)
        {
            _dictation.Progress += text => DictationHint = text;
        }

        // 枚举音色可能因为语音组件异常而失败：不能让整个主持台构造不出来。
        try
        {
            foreach (var v in tts?.GetChineseVoices() ?? [])
            {
                AvailableVoices.Add(new VoiceOption(v));
            }
        }
        catch (Exception ex)
        {
            LocalDm.Core.Diagnostics.RunLog.Write("Host", "枚举音色失败：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(Convert.ToString(ex.Message)));
        }

        var savedVoice = settingsProvider()?.VoiceId;
        _suppressVoiceFeedback = true;
        try
        {
            _selectedVoiceId = !string.IsNullOrEmpty(savedVoice) && AvailableVoices.Any(v => v.Name == savedVoice)
                ? savedVoice
                : AvailableVoices.FirstOrDefault()?.Name;
        }
        finally
        {
            _suppressVoiceFeedback = false;
        }

        _session = dispatcher is null ? new SessionViewModel(new CommandDispatcher(new UnwiredSessionStore())) : new SessionViewModel(dispatcher);
        _session.PropertyChanged += OnSessionPropertyChanged;
        SyncIdleActivity();

        // 每次状态提交成功后，按对应意图请模型生成播报措辞。
        // 状态已由程序裁决，这里只是「把刚发生的事说出来」。
        _session.Committed += async (_, type) =>
        {
            // 阶段事件先落进对话：即使随后模型失败，玩家也看得见流程确实走了一步。
            AppendEvent(_session.Status);

            // 播报期间也占住忙碌闸：否则主持人可以在上一段还没念完时再点一次提交，
            // 于是模型请求与播音各跑一路，状态与日志互相覆盖。
            _session.SetNarrationBusy(true);
            try
            {
                await NarrateAfterCommandAsync(type);
            }
            catch (Exception ex)
            {
                // 公共屏只显示固定文案：异常里可能带本机路径与模型细节。
                LocalDm.Core.Diagnostics.RunLog.Write("Host", "播报异常：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(Convert.ToString(ex)));
                Status = "播报未完成，文字已保留。";
            }
            finally
            {
                _session.SetNarrationBusy(false);
            }
        };

        // Stop clears IsBusy before a gateway that ignores cancellation returns; generation checks protect the newer turn.
        SendCommand = new RelayCommand(SendAsync, () => !IsBusy, allowReentry: true);
        StopCommand = new RelayCommand(StopAsync, () => IsBusy, allowReentry: true);
        OpeningSpeechCommand = new RelayCommand(
            () => NarrateIntentAsync(HostNarrationIntent.OpenSession),
            () => !IsBusy && ShowOpeningSpeech,
            allowReentry: true);
        AskStatusCommand = new RelayCommand(
            () => AskAsync("现在这个环节需要做什么？"),
            () => !IsBusy && ShowSuggestions,
            allowReentry: true);
        AskRulesCommand = new RelayCommand(
            () => AskAsync("请再说明一次当前环节的规则。"),
            () => !IsBusy && ShowSuggestions,
            allowReentry: true);
        ShowMaterialsCommand = new RelayCommand(() => { ShowMaterials = true; return Task.CompletedTask; });
        ShowConversationCommand = new RelayCommand(() => { ShowMaterials = false; return Task.CompletedTask; });
        CompactCommand = new RelayCommand(() => ManualCompactAsync(), () => CanCompact, allowReentry: true);
        ConfirmCorrectionCommand = new RelayCommand(ConfirmCorrectionAsync, () => HasPendingCorrection && !_session.Busy);
        DismissCorrectionCommand = new RelayCommand(() => { SetPendingCorrection(null); return Task.CompletedTask; });

        // 公开消息：桌上每落一条消息就往存档写一条，平板按序号取。
        // 存储不支持公开消息时（比如未接线）就完全不接这条线，不影响主持。
        if (store is IPublicMessageFeed feed)
        {
            _publicFeed = new PublicMessagePublisher(feed);
            _publicFeed.Failed += OnPublicFeedFailed;
            _publicFeed.Recovered += OnPublicFeedRecovered;
            _publicFeed.Committed += OnPublicFeedCommitted;
        }

        RetryPublicSyncCommand = new RelayCommand(
            () =>
            {
                _publicFeed?.Retry();
                return Task.CompletedTask;
            },
            () => HasPublicSyncError);

        ToggleDictationCommand = new RelayCommand(
            ToggleDictationAsync,
            () => _dictation is not null,
            allowReentry: true);
    }

    public ObservableCollection<VoiceOption> AvailableVoices { get; } = [];

    /// <summary>本局对话：玩家、主持与阶段事件按时间顺序在同一条线上。</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    /// <summary>
    /// 正在进行的这件事：现在在做什么、走到第几步、已经等了多久。
    /// 只在「正在做」和「失败」时显示，空闲不占屏。
    /// </summary>
    public HostActivity Activity { get; } = new();

    public SessionViewModel Session => _session;

    /// <summary>
    /// 本局的 DM 对话记忆：备忘、发给模型的历史与最近一次真实 token 用量。
    /// 只读：屏幕上的聊天记录不受它影响，这里只决定发给模型什么。
    /// </summary>
    public DmConversation Conversation => _conversation;

    public string? SelectedVoiceId
    {
        get => _selectedVoiceId;
        set
        {
            if (SetProperty(ref _selectedVoiceId, value))
            {
                OnPropertyChanged(nameof(VoiceLabel));
                OnVoiceChanged(value);
            }
        }
    }

    /// <summary>左侧页脚那一行：这位声音会不会自动读。</summary>
    public string VoiceLabel =>
        (string.IsNullOrEmpty(SelectedVoiceId) ? "跟随系统声音" : TtsVoiceDisplay.For(SelectedVoiceId))
        + (AutoRead ? " · 自动朗读" : " · 仅文字");

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public SpeechState SpeechState
    {
        get => _speechState;
        private set => SetProperty(ref _speechState, value);
    }

    /// <summary>正在等模型返回文字。</summary>
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                RaiseBusyChanged();
            }
        }
    }

    /// <summary>正在朗读。生成与朗读分开报，就地反馈才说得准。</summary>
    public bool IsSpeaking
    {
        get => _isSpeaking;
        private set
        {
            if (SetProperty(ref _isSpeaking, value))
            {
                RaiseBusyChanged();
            }
        }
    }

    public bool IsBusy => IsGenerating || IsSpeaking;

    /// <summary>停止只在忙碌时出现：没在生成也没在念，就没有可停的东西。</summary>
    public bool ShowStop => IsBusy;

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    public bool DictationAvailable => _dictation is not null;

    public bool IsListening
    {
        get => _isListening;
        private set => SetProperty(ref _isListening, value);
    }

    public string DictationHint
    {
        get => _dictationHint;
        private set
        {
            if (SetProperty(ref _dictationHint, value))
            {
                OnPropertyChanged(nameof(HasDictationHint));
            }
        }
    }

    public bool HasDictationHint => DictationHint.Length > 0;

    public TimeSpan DictationLimit { get; set; } = TimeSpan.FromSeconds(60);

    public RelayCommand ToggleDictationCommand { get; }

    /// <summary>回复生成后是否自动朗读；读的是设置页那一份，改了立刻生效。</summary>
    public bool AutoRead => _settingsProvider()?.AutoRead ?? true;

    /// <summary>输入区上方那行提示：现在会不会自动朗读、能不能点停止。</summary>
    public string ComposerHint => IsBusy
        ? "正在回应，点右侧方形按钮可停止。"
        : AutoRead
            ? "回复后自动朗读。"
            : "仅显示文字，可在消息旁逐条重播。";

    /// <summary>
    /// 本环节是否提供「请主持人开场」。
    /// 由剧本包的流程声明决定，不按「是不是第 1 个环节」推断 ——
    /// 序章本身可能就是第一步，固定序号会让这个按钮在不该出现的地方消失。
    /// </summary>
    public bool ShowOpeningSpeech =>
        _session.SessionStateText == nameof(SessionState.Playing)
        && _session.Projection?.CurrentPhase.CanOpenSession == true;

    /// <summary>快捷意图只在对话里用得上：待开局和忙碌时不摆。</summary>
    public bool ShowSuggestions =>
        !IsBusy
        && _session.Projection is not null
        && _session.SessionStateText != nameof(SessionState.Prepared);

    /// <summary>主区当前显示哪一屏：对话或公开材料/复盘。</summary>
    public bool ShowMaterials
    {
        get => _showMaterials;
        set
        {
            if (SetProperty(ref _showMaterials, value))
            {
                OnPropertyChanged(nameof(ShowConversation));
                OnShellTitleChanged();
            }
        }
    }

    public bool ShowConversation => !ShowMaterials;

    public string ShellTitle => ShowMaterials ? "公开材料与复盘" : "本局对话";

    public string MaterialsTitle => _session.HasRevealedText ? "公开材料与复盘" : "已公开材料";

    /// <summary>顶栏的材料入口只在真有东西可看时才亮。</summary>
    public bool HasMaterialsTab => _session.ReleasedMaterials.Count > 0 || _session.HasRevealedText;

    /// <summary>对话为空时给一句开场提示，而不是一片空白。</summary>
    public bool HasConversation => Messages.Count > 0;

    public RelayCommand SendCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand OpeningSpeechCommand { get; }

    public RelayCommand AskStatusCommand { get; }

    public RelayCommand AskRulesCommand { get; }

    public RelayCommand ShowMaterialsCommand { get; }

    public RelayCommand ShowConversationCommand { get; }

    /// <summary>手动整理上下文：把较早的对话整理成备忘，保留最近 2 轮原话。</summary>
    public RelayCommand CompactCommand { get; }

    /// <summary>
    /// 能不能整理上下文：忙着的时候不行；保留最近 2 轮之后前面还剩至少一条消息才行。
    /// 否则没有可整理的内容，模型只会凭空生成一份备忘。
    /// </summary>
    public bool CanCompact => !IsBusy && ContextBudget.CanCompact(_conversation);

    /// <summary>
    /// 本局备忘的正文。只给测试与诊断用，不绑定到任何界面：
    /// 备忘里可能夹带还没发出去的卡面细节，上公共屏等于提前给玩家看卡。
    /// </summary>
    public string? Memo => _conversation.Memo;

    /// <summary>整理过没有：没整理过就不显示次数。</summary>
    public bool HasMemo => !string.IsNullOrWhiteSpace(_conversation.Memo);

    /// <summary>已整理几次，例如「已整理 2 次」。</summary>
    public string CompactionCountText => $"已整理 {_conversation.CompactionCount} 次";

    /// <summary>
    /// 对话列表与底部输入区的最大宽度（DIP）。满宽档是无穷大，即只留两端边距。
    /// 由窗口在读到设置时写入，界面直接绑定，换档不必重建页面。
    /// </summary>
    public double ChatMaxWidth
    {
        get => _chatMaxWidth;
        set => SetProperty(ref _chatMaxWidth, value);
    }

    private double _chatMaxWidth = 980;

    /// <summary>玩家消息气泡的最大宽度：跟着对话宽度同比放宽。</summary>
    public double BubbleMaxWidth
    {
        get => _bubbleMaxWidth;
        set => SetProperty(ref _bubbleMaxWidth, value);
    }

    private double _bubbleMaxWidth = 620;

    /// <summary>
    /// 上下文占用：百分比与估算的 token 数，例如「上下文 42% · 约 27k / 65k」。
    /// 到 80% 会变提醒色：再聊下去就要自动整理了。
    /// </summary>
    public string ContextUsageText
    {
        get
        {
            var (tokens, window) = CurrentContextUsage();
            var percent = (int)Math.Round(tokens / (double)Math.Max(1, window) * 100);
            return $"上下文 {percent}% · 约 {tokens / 1000}k / {window / 1000}k";
        }
    }

    /// <summary>占用已经到自动整理的门槛：文字要变提醒色。</summary>
    public bool ContextUsageHigh
    {
        get
        {
            var (tokens, window) = CurrentContextUsage();
            return tokens >= (double)Math.Max(1, window) * ContextBudget.AutoCompactRatio;
        }
    }

    private void RaiseBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ComposerHint));
        OnPropertyChanged(nameof(ShowSuggestions));
        SendCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        OpeningSpeechCommand.RaiseCanExecuteChanged();
        AskStatusCommand.RaiseCanExecuteChanged();
        AskRulesCommand.RaiseCanExecuteChanged();
        CompactCommand.RaiseCanExecuteChanged();
        foreach (var message in Messages)
        {
            message.ReplayCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 当前的上下文占用：这一局已有的内容（提示词 + 备忘 + 历史）加上下一轮要发的消息。
    /// 第二个数是扣掉回复预留之后真正能放东西的窗口。
    /// </summary>
    private (int Tokens, int Window) CurrentContextUsage()
    {
        var window = _settingsProvider()?.ContextWindowTokens ?? ToolSettings.DefaultContextWindowTokens;
        var usable = ContextBudget.UsableTokens(window);
        var package = _session.Package;
        var session = _session.Session;
        if (package is null || session is null)
        {
            return (0, usable);
        }

        var system = HostNarration.BuildSystemPrompt(package);
        var next = HostNarration.BuildAskUserMessage(package, session, string.Empty, _conversation.Memo);
        return (ContextBudget.EstimateNextPromptTokens(_conversation, system, next), usable);
    }

    /// <summary>占用变了就刷新界面上的那行字。</summary>
    private void RefreshContextUsage()
    {
        OnPropertyChanged(nameof(ContextUsageText));
        OnPropertyChanged(nameof(ContextUsageHigh));
    }

    /// <summary>音色变更。落盘失败时给主持人一句能看懂的提示，不把异常抛到绑定层。</summary>
    private void OnVoiceChanged(string? voiceId)
    {
        if (voiceId is null)
        {
            return;
        }

        if (_suppressVoiceFeedback)
        {
            return;
        }

        var failure = _onVoiceChanged?.Invoke(voiceId);
        var voice = AvailableVoices.FirstOrDefault(v => v.Name == voiceId);
        Status = failure is null
            ? $"已切换主持音色为「{voice?.DisplayName ?? voiceId}」。"
            : $"音色已切换为「{voice?.DisplayName ?? voiceId}」，但没有保存成功：{failure}";
    }

    /// <summary>
    /// 朗读用的音色。设置页与主持台写的是同一份设置（<see cref="ApplySettingsVoice"/> 保持同步），
    /// 所以下拉框优先；本机一个音色都没有时才退回设置里的值。
    /// </summary>
    private string? EffectiveVoice(ToolSettings settings) => SelectedVoiceId ?? settings.VoiceId;

    /// <summary>
    /// 重新枚举音色。Edge 依赖装好后不必重启应用，回到设置页再刷一次即可。
    /// </summary>
    public void RefreshVoices()
    {
        if (_tts is null)
        {
            return;
        }

        try
        {
            var voices = _tts.GetChineseVoices();
            var names = voices.Select(v => v.Name).ToArray();
            if (names.SequenceEqual(AvailableVoices.Select(v => v.Name)))
            {
                return;
            }

            var selected = SelectedVoiceId;
            AvailableVoices.Clear();
            foreach (var v in voices)
            {
                AvailableVoices.Add(new VoiceOption(v));
            }

            if (selected is not null && AvailableVoices.All(v => v.Name != selected))
            {
                // 原来选的音色没了：换一个，并把它落盘，
                // 否则下拉框显示的和实际朗读的不是同一个音色。
                var fallback = AvailableVoices.FirstOrDefault()?.Name;
                _suppressVoiceFeedback = true;
                try
                {
                    SelectedVoiceId = fallback;
                }
                finally
                {
                    _suppressVoiceFeedback = false;
                }

                if (fallback is not null)
                {
                    _onVoiceChanged?.Invoke(fallback);
                    Status = $"原来的音色不可用，已切换为「{TtsVoiceDisplay.For(fallback)}」。";
                }
            }
        }
        catch (Exception ex)
        {
            LocalDm.Core.Diagnostics.RunLog.Write("Host", "刷新音色列表失败：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(Convert.ToString(ex.Message)));
        }
    }

    /// <summary>
    /// 设置页保存后同步主持台的音色与朗读选项。
    /// 否则主持台还在用启动时的旧音色朗读，而设置页已经换了。
    /// </summary>
    public void ApplySettingsVoice(string? voiceId)
    {
        if (!string.IsNullOrEmpty(voiceId)
            && voiceId != _selectedVoiceId
            && AvailableVoices.Any(v => v.Name == voiceId))
        {
            _suppressVoiceFeedback = true;
            try
            {
                SelectedVoiceId = voiceId;
            }
            finally
            {
                _suppressVoiceFeedback = false;
            }
        }

        // 自动朗读是设置项，改完要立刻反映到输入区提示上。
        OnPropertyChanged(nameof(AutoRead));
        OnPropertyChanged(nameof(ComposerHint));
        OnPropertyChanged(nameof(VoiceLabel));
    }

    /// <summary>默认不启动任何模型请求。</summary>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        AppendHost("欢迎回来。正在从剧本库读入剧本，读好后这一页就是你们的一局。", fromModel: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 装载一个已冻结的剧本包（来自剧本库）。不自动开始、不自动播音。
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

        var sessionId = SessionIds.For(package);
        try
        {
            var session = await _store.LoadSessionAsync(sessionId, CancellationToken.None);
            var isNew = session is null;
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
            if (isNew)
            {
                await _store.SaveSessionAsync(session, CancellationToken.None);
            }
            if (_store is SessionStore concrete)
            {
                await concrete.RegisterPackageAsync(package, CancellationToken.None);
            }

            _activePendingPackage = package;
            _session.Attach(package, session);
            OnPropertyChanged(nameof(SessionSummary));

            AnnounceLoadedSession(
                package,
                session,
                package.Title,
                $"已装载「{package.Title}」：{package.PlayerCount} 人、{package.Spans.Count} 页来源。",
                $"已装载「{package.Title}」。全员就位后，点左侧「开始这一局」。");

            await LoadConversationAsync(session.SessionId);
        }
        catch (DomainRuleException ex)
        {
            Status = ex.PublicReason;
        }
    }

    /// <summary>
    /// 重新开始：删掉这一局的存档、事件、对话与公开消息，再按新局装载（回到「开始这一局」之前）。
    /// 只在主持人确认后调用；平板复位由调用方包在外面。
    /// </summary>
    public async Task RestartPackageAsync(ScriptPackage package)
    {
        SetPendingCorrection(null);
        if (_store is not SessionStore store)
        {
            Status = "当前没有可用存档。";
            return;
        }

        if (IsBusy)
        {
            StopCommand.Execute(null);
        }

        _director.ResetToIdle();
        SpeechState = SpeechState.Idle;
        // 删之前先归档：同一本剧本只有一份存档，不归档上一局就没了。归档失败抛出，调用方提示且不删。
        var archived = await SessionExport.ArchiveAsync(store, SessionIds.For(package), package.Title, CancellationToken.None);
        if (archived is not null)
        {
            LocalDm.Core.Diagnostics.RunLog.Write("Host", $"重新开始《{package.Title}》，上一局已归档：{System.IO.Path.GetFileName(archived)}");
        }

        await store.DeleteSessionAsync(SessionIds.For(package), CancellationToken.None);
        _conversationSessionId = null; // 同一个存档 id：不清掉的话屏幕上还留着上一局的对话
        await LoadPackageAsync(package);
    }

    /// <summary>当前装载的真实剧本包；用于「继续一局」时校验指纹。</summary>
    private ScriptPackage? _activePendingPackage;

    /// <summary>
    /// 装载完成后的统一收尾：新局与恢复局只差文案，
    /// 阶段标题兜底与大屏视图的复位必须两处一致，不能各写一份。
    /// </summary>
    private void AnnounceLoadedSession(
        ScriptPackage package,
        Session session,
        string displayName,
        string loadedStatus,
        string loadedPublic)
    {
        SetPendingCorrection(null);
        BindConversationSession(session.SessionId);

        if (session.State == SessionState.Prepared)
        {
            Status = loadedStatus;
            AppendHost(loadedPublic, fromModel: false);
        }
        else
        {
            var currentPhase = package.Phases.FirstOrDefault(p => p.PhaseId == session.CurrentPhaseId);
            var phaseTitle = currentPhase?.Title ?? "当前环节";
            Status = $"已恢复「{displayName}」进度至「{phaseTitle}」。";
            AppendEvent($"已恢复「{displayName}」到「{phaseTitle}」。");
            AppendHost($"上一局停在「{phaseTitle}」，进度已经接上。安静开始，不会自动朗读。", fromModel: false);
        }

        // 真相与复盘只在该显示时显示：装载新剧本时要复位。
        // 用 TruthAccess 的同一套判定（含「已揭秘后暂停」），否则这里会把大屏关掉。
        ShowMaterials = TruthAccess.IsUnlocked(session);
        _hadRevealedText = _session.HasRevealedText;
        OnPropertyChanged(nameof(MaterialsTitle));
        OnPropertyChanged(nameof(HasMaterialsTab));
        OnPropertyChanged(nameof(ShowOpeningSpeech));
        OnPropertyChanged(nameof(ShowSuggestions));
        OnPropertyChanged(nameof(HasConversation));
    }

    /// <summary>
    /// 把对话记忆绑定到某一局。
    ///
    /// 记忆必须跟着「这一局」走：换局就把上一局的记忆和屏幕记录清掉，
    /// 否则会被写进别的存档，恢复时也会串局。
    /// 没有绑定任何局时（还没装载剧本、还没恢复任何一局），
    /// 存档那一步会直接跳过 —— 对话不会被丢进无名的存档里。
    /// </summary>
    private void BindConversationSession(string sessionId)
    {
        if (_conversationSessionId == sessionId)
        {
            return;
        }

        if (IsBusy)
        {
            StopCommand.Execute(null);
        }

        Messages.Clear();
        _conversation = new DmConversation();
        _conversationSessionId = sessionId;
    }

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
            var isNew = session is null;
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
            if (isNew)
            {
                await _store.SaveSessionAsync(session, CancellationToken.None);
            }
            if (_store is SessionStore concrete)
            {
                await concrete.RegisterPackageAsync(package, CancellationToken.None);
            }

            _activePendingPackage = package;
            _session.Attach(package, session);
            OnPropertyChanged(nameof(SessionSummary));

            AnnounceLoadedSession(
                package,
                session,
                "演示剧本",
                "演示剧本已装载。这是合成数据，用来熟悉操作。",
                "演示剧本已就绪。全员就位后，点左侧「开始这一局」。");

            await LoadConversationAsync(session.SessionId);
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
        var sessionId = SessionIds.For(package);
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
        // 恢复的这一局就是「这一局」：对话记忆必须绑上去，否则后面聊的天不会存档。
        BindConversationSession(result.Session.SessionId);
        Status = "已恢复上一次的进度。不会自动重播。";
        AppendEvent($"已恢复至「{result.Session.CurrentPhaseId}」所在环节。");
        await LoadConversationAsync(result.Session.SessionId);
    }

    // ===== 一次提问：玩家说 → 主持回 =====

    public async Task SendAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var text = InputText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 整句输入「压缩上下文」等同点那个按钮：这句话不进对话记忆，也不发给模型。
        if (_router.Route(text).Kind == HostIntentKind.Compact)
        {
            InputText = string.Empty;
            await ManualCompactAsync();
            return;
        }

        InputText = string.Empty;
        await AskCoreAsync(text);
    }

    private async Task AskCoreAsync(string text)
    {
        AppendPlayer(text);

        var settings = _settingsProvider();
        if (string.IsNullOrWhiteSpace(settings.ModelId))
        {
            AppendHost("还没有选择主持模型。请先到设置页读取模型列表。", fromModel: false);
            Status = "未选择 model ID。";
            return;
        }

        var package = _session.Package;
        var session = _session.Session;
        if (package is null || session is null)
        {
            AppendHost("还没有装载剧本。请先从剧本库选一本。", fromModel: false);
            Status = "还没有装载剧本。";
            return;
        }

        // 当前问题已上屏，但请求里只加入一次；更早的真实对话由本局记忆提供。
        var token = ++_generation;
        var messages = await PrepareTurnAsync(package, session, text, null, token);
        if (token != _generation || messages is null)
        {
            return;
        }

        await RunTurnAsync(messages, "正在请本机模型回答…", AskPipeline, text, token);
    }

    /// <summary>
    /// 平板发言：同步接单，不等整轮结束。必须在 UI 线程调用。
    /// </summary>
    public TabletSpeechAck SubmitTabletSpeech(string roleName, string text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return TabletSpeechAck.Rejected(TabletSpeechOutcome.Empty);
        }

        if (IsBusy)
        {
            return TabletSpeechAck.Rejected(TabletSpeechOutcome.Busy);
        }

        var settings = _settingsProvider();
        if (string.IsNullOrWhiteSpace(settings.ModelId))
        {
            return TabletSpeechAck.Rejected(TabletSpeechOutcome.NoModel);
        }

        if (_session.Package is null || _session.Session is null)
        {
            return TabletSpeechAck.Rejected(TabletSpeechOutcome.NoSession);
        }

        var signed = $"【{roleName}】{trimmed}";
        var route = _router.Route(trimmed);
        if (route.Kind == HostIntentKind.ProposeCommand)
        {
            AppendPlayer(signed);
            var hint = route.ProposedAction switch
            {
                PublicAction.Advance => $"{roleName} 提议：进入下一环节。请在电脑上确认后再推进。",
                PublicAction.Reveal => $"{roleName} 提议：揭秘。请在电脑上确认后再操作。",
                _ => $"{roleName} 提议：操作。请在电脑上确认后再推进。",
            };
            AppendLocalHint(hint);
            return new TabletSpeechAck(TabletSpeechOutcome.Proposed, Task.CompletedTask);
        }

        return new TabletSpeechAck(TabletSpeechOutcome.Accepted, AskCoreAsync(signed));
    }

    /// <summary>
    /// 平板打断朗读：正在朗读时打断并返回 true；生成阶段不打断并返回 false。
    /// </summary>
    public bool StopSpeakingForTablet()
    {
        if (IsSpeaking)
        {
            StopCommand.Execute(null);
            return true;
        }

        return false;
    }

    /// <summary>快捷意图：替玩家把这句话打进输入框再发出去，走的还是同一条链路。</summary>
    private Task AskAsync(string text)
    {
        InputText = text;
        return SendAsync();
    }

    /// <summary>公共开场：把“开场”这个意图交给模型，由模型组织措辞。</summary>
    public Task SpeakAsync() => NarrateIntentAsync(HostNarrationIntent.OpenSession);

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

        if (type == GameCommandType.SubmitVoteSummary && _session.LastVoteWasCorrection) { return; }

        var intent = type switch
        {
            GameCommandType.Start => HostNarrationIntent.AnnouncePhase,
            GameCommandType.Advance => HostNarrationIntent.AnnouncePhase,
            GameCommandType.RevertPhase => HostNarrationIntent.AnnouncePhase,
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
            await NarrateIntentAsync(intent);
        }
        catch (DomainRuleException ex)
        {
            // 播报失败不改变已提交的进度
            Status = "播报未完成：" + ex.PublicReason;
        }
    }

    /// <summary>
    /// 按主持意图请求模型措辞，经公开政策核对后朗读。
    /// 按钮给的是「现在要做什么」，措辞由模型生成，程序只核对能否回指公开原文。
    /// </summary>
    public async Task NarrateAsync(HostNarrationIntent intent) => await NarrateIntentAsync(intent);

    private async Task NarrateIntentAsync(HostNarrationIntent intent)
    {
        if (IsBusy)
        {
            // 不静默吞掉：主持人得知道这一段没有播，而且进度与文字都还在。
            Status = "上一段播报还没结束，这一段没有播。";
            AppendLocalHint("上一段播报还没结束，这一段的播报跳过了；文字与进度都已保留。");
            return;
        }

        var settings = _settingsProvider();
        if (string.IsNullOrWhiteSpace(settings.ModelId))
        {
            AppendHost("还没有选择主持模型；流程操作仍然可用。", fromModel: false);
            Status = "未选择 model ID。";
            return;
        }

        var package = _session.Package;
        var session = _session.Session;
        if (package is null || session is null)
        {
            AppendHost("还没有装载剧本。请先从剧本库选一本。", fromModel: false);
            Status = "还没有装载剧本。";
            return;
        }

        var token = ++_generation;
        var messages = await PrepareTurnAsync(package, session, null, intent, token);
        if (token != _generation || messages is null)
        {
            return;
        }

        var phase = package.Phases.FirstOrDefault(p => p.PhaseId == session.CurrentPhaseId);
        await RunTurnAsync(
            messages,
            "正在请本机模型写主持词…",
            SpeakPipeline,
            $"【主持台操作】{HostNarration.IntentLine(intent, phase?.Title ?? "开场")}",
            token);
    }

    /// <summary>提问这条流水线的两步：先问模型，再朗读。</summary>
    private static readonly string[] AskPipeline = ["请模型回答", "朗读"];

    /// <summary>口播这条流水线的两步：先请模型写词，再朗读。</summary>
    private static readonly string[] SpeakPipeline = ["请模型写词", "朗读"];

    /// <summary>
    /// 发这一轮之前先按窗口计量上下文：快满了就把较早的对话整理成备忘，再发这一轮。
    /// 返回 null 表示这一轮被取消（操作者按了停止）。
    /// </summary>
    private async Task<IReadOnlyList<MagpieMessage>?> PrepareTurnAsync(
        ScriptPackage package,
        Session session,
        string? userText,
        HostNarrationIntent? intent,
        int token)
    {
        var settings = _settingsProvider();
        var window = settings.ContextWindowTokens;
        var system = HostNarration.BuildSystemPrompt(package);

        string NextUserMessage() => intent is { } asked
            ? HostNarration.BuildIntentUserMessage(package, session, asked, _conversation.Memo, settings.ModelId)
            : HostNarration.BuildAskUserMessage(package, session, userText!, _conversation.Memo, settings.ModelId);

        if (ContextBudget.UsageRatio(_conversation, system, NextUserMessage(), window) >= ContextBudget.AutoCompactRatio)
        {
            if (!await CompactAsync(package, system, NextUserMessage(), window, manual: false, token: token))
            {
                return null;
            }
        }

        return intent is { } asked
            ? HostNarration.BuildMessages(package, session, asked, _conversation, settings.ModelId)
            : HostNarration.BuildAskMessages(package, session, userText!, _conversation, settings.ModelId);
    }

    /// <summary>
    /// 手动整理：保留最近 2 轮原话，其余全部整理成备忘。
    /// </summary>
    private async Task ManualCompactAsync()
    {
        var package = _session.Package;
        var session = _session.Session;
        if (package is null || session is null)
        {
            Status = "还没有装载剧本。";
            return;
        }

        if (IsBusy)
        {
            Status = "现在正忙着，整理上下文要等这一轮结束。";
            return;
        }

        var settings = _settingsProvider();
        var system = HostNarration.BuildSystemPrompt(package);
        var next = HostNarration.BuildAskUserMessage(package, session, string.Empty, _conversation.Memo);
        var token = ++_generation;
        await CompactAsync(package, system, next, settings.ContextWindowTokens, manual: true, token: token);
    }

    /// <summary>
    /// 把较早的对话整理成一份本局备忘。返回 false 表示这一轮被取消。
    ///
    /// 整理失败时不动记忆：这一轮照常发。真的发不出去（占用超过 100%）时，
    /// 才退而求其次丢掉最早的轮次，并把丢了几轮说给主持人听。
    /// </summary>
    private async Task<bool> CompactAsync(
        ScriptPackage package,
        string system,
        string nextUserMessage,
        int contextWindowTokens,
        bool manual,
        int token)
    {
        var settings = _settingsProvider();
        var keepFromIndex = manual
            ? ContextBudget.ManualKeepFromIndex(_conversation)
            : ContextBudget.KeepFromIndex(_conversation, contextWindowTokens);

        if (keepFromIndex == 0)
        {
            // 前面没有可整理的内容：不发请求，免得模型凭空生成一份备忘。
            // 从没整理过的局是「对话还太短」；整理过的局才是「再之前都整理过了」。
            if (manual)
            {
                Status = _conversation.CompactionCount == 0
                    ? "对话还太短，暂时没有需要整理的内容。"
                    : "最近 2 轮之前的对话都整理过了，这次没有需要整理的内容。";
                return true;
            }

            // 整段对话都在预算内，却已经占满窗口：压缩帮不上忙，只能提示换更大的窗口。
            if (ContextBudget.UsageRatio(_conversation, system, nextUserMessage, contextWindowTokens) >= 1.0)
            {
                AppendLocalHint("主持资料和最近的对话已经占满上下文窗口，压缩帮不上忙。请在设置里调大模型上下文长度，或换一个上下文更长的模型。");
            }

            // 这一轮照常发送，不丢任何轮次。
            return true;
        }

        IsGenerating = true;
        Activity.Begin(ActivityKind.Thinking, "正在整理较早的对话…", ["整理记忆"], PhaseDetail());
        var continueTurn = false;
        try
        {
            var (memo, reason, late) = await _director.GenerateOnlyAsync(
                settings.ModelId!,
                HostNarration.BuildCompactionMessages(package, _conversation, keepFromIndex),
                CancellationToken.None);

            if (token != _generation || late)
            {
                // 操作者按了停止：记忆保持原样。
                return false;
            }

            if (string.IsNullOrWhiteSpace(memo))
            {
                var dropped = DropOldestUntilSendable(system, nextUserMessage, contextWindowTokens);
                if (dropped > 0)
                {
                    AppendLocalHint($"整理失败，已丢弃最早的 {dropped} 轮对话以继续主持。");
                }
                else
                {
                    AppendLocalHint("整理较早的对话失败，这一轮照常发送。");
                }

                continueTurn = !manual;
                return true;
            }

            // 人工验收发现模型会把还没读到的卡面细节写进备忘：备忘里只要有一句照着卡面原文写了
            // 桌上没人念过的一段，就当作这次整理没成功，整份不采用，这一轮照常发送。
            var unsupported = DmMemoCheck.UnsupportedTerms(memo, _conversation, package);
            if (unsupported.Count > 0)
            {
                AppendLocalHint($"整理较早的对话失败：整理结果里有 {unsupported.Count} 处对话中没出现过的内容，这份备忘没有采用，这一轮照常发送。");
                continueTurn = !manual;
                return true;
            }

            if (token != _generation)
            {
                return false;
            }

            _conversation.ApplyCompaction(memo, keepFromIndex);
            var kept = (_conversation.Turns.Count + 1) / 2;
            AppendLocalHint($"已整理较早的对话（第 {_conversation.CompactionCount} 次），保留了最近 {kept} 轮原话。");
            OnPropertyChanged(nameof(Memo));
            OnPropertyChanged(nameof(HasMemo));
            OnPropertyChanged(nameof(CompactionCountText));
            RefreshContextUsage();
            await SaveConversationAsync(token);
            continueTurn = !manual && token == _generation;
            return token == _generation;
        }
        finally
        {
            if (token == _generation && (manual || !continueTurn))
            {
                IsGenerating = false;
            }
        }
    }

    /// <summary>
    /// 占用已经超过窗口时，丢弃最早的轮次直到能发出去。返回丢了几轮。
    ///
    /// 这是唯一会丢内容的路径，所以只在真的发不出去（占用超过 100%）时才走：
    /// 整理失败但还发得出去时，这一轮照常发，不丢任何内容。
    /// </summary>
    private int DropOldestUntilSendable(string system, string nextUserMessage, int contextWindowTokens)
    {
        double Ratio() => ContextBudget.UsageRatio(_conversation, system, nextUserMessage, contextWindowTokens);
        if (Ratio() < 1.0)
        {
            return 0;
        }

        var dropped = 0;
        while (_conversation.Turns.Count > 0 && Ratio() >= ContextBudget.DropTargetRatio)
        {
            dropped += _conversation.DropOldestRounds(1);
        }

        return dropped;
    }

    /// <summary>
    /// 一轮完整的「生成 → 朗读」。生成与朗读各自就地报状态；
    /// 任一步被停止或失败，都只在这条消息上留痕，不影响已经提交的游戏状态。
    /// </summary>
    private async Task RunTurnAsync(
        IReadOnlyList<MagpieMessage> messages,
        string thinkingTitle,
        string[] pipeline,
        string memoryUserText,
        int token)
    {
        var settings = _settingsProvider();

        IsGenerating = true;
        Activity.Begin(ActivityKind.Thinking, thinkingTitle, pipeline, PhaseDetail());
        _session.SetNarrationBusy(true);
        try
        {
            var (reply, reason, late) = await _director.GenerateOnlyAsync(
                settings.ModelId!, messages, CancellationToken.None);

            // 已经被停止：晚到的回复不上屏，也不朗读。
            if (token != _generation || late)
            {
                return;
            }

            if (reply is null)
            {
                LocalDm.Core.Diagnostics.RunLog.Write("Host", "模型没有给出可用内容：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(reason ?? "（没有原因）"));
                SpeechState = SpeechState.Error;
                AppendHost(reason ?? "模型这次没有给出可用内容，请检查本机模型服务。", fromModel: false);
                Activity.Fail("模型没有给出可用内容", reason ?? "请检查本机模型服务。");
                return;
            }

            var (shown, correction) = CorrectionProposal.Extract(reply);
            reply = string.IsNullOrWhiteSpace(shown) ? "好的，请在电脑上确认。" : shown;
            OfferCorrection(correction);

            // 模型拥有完整主持资料，自主组织对外回复；不把内部提示词直接显示或朗读。
            var message = AppendHost(reply);

            // 请求成功了才写进本局记忆：失败或被停止时这句话不进记忆，玩家可以重发。
            _conversation.AppendUser(memoryUserText);
            _conversation.AppendAssistant(reply, _director.LastUsage);
            RefreshContextUsage();
            await SaveConversationAsync(token);

            if (token != _generation)
            {
                return;
            }

            if (!AutoRead)
            {
                message.SetSpeech(ChatSpeechState.Ready, "自动朗读已关闭，可点重播。");
                SpeechState = SpeechState.Idle;
                Activity.Complete("文字已生成");
                return;
            }

            IsGenerating = false;
            IsSpeaking = true;
            _speakingMessage = message;
            message.SetSpeech(ChatSpeechState.Speaking, "正在朗读…");
            SpeechState = SpeechState.Speaking;
            Activity.SetStep(1, "正在朗读");
            Activity.SetDetail(PhaseDetail());

            var result = await _director.SpeakApprovedAsync(reply, EffectiveVoice(settings), CancellationToken.None);

            if (token != _generation)
            {
                // 停止已经改写过这条消息的状态，这里不要覆盖它。
                return;
            }

            if (result.Item is not null)
            {
                message.SetSpeech(ChatSpeechState.Spoken, "已朗读");
                SpeechState = SpeechState.Idle;
                Activity.Complete("已朗读");
            }
            else
            {
                message.SetSpeech(ChatSpeechState.Failed, result.PublicReason ?? "朗读失败，文字已保留。");
                SpeechState = SpeechState.Error;
                Activity.Fail("朗读没有完成", result.PublicReason ?? "文字已保留，可稍后重播。");
            }
        }
        catch (DomainRuleException ex)
        {
            if (token != _generation)
            {
                return;
            }

            AppendHost(ex.PublicReason, fromModel: false);
            Activity.Fail("这一步没有完成", ex.PublicReason);
        }
        finally
        {
            if (token == _generation)
            {
                IsGenerating = false;
                IsSpeaking = false;
                _speakingMessage = null;
                _session.SetNarrationBusy(false);
                if (Activity.IsActive)
                {
                    Activity.Reset("就绪", PhaseDetail());
                }
            }
        }
    }

    private void OfferCorrection(CorrectionKind? kind)
    {
        if (kind is null) return;
        var canApply = kind switch
        {
            CorrectionKind.RevertPhase => _session.SessionStateText == "Playing" && _session.PreviousPhaseTitle is not null,
            CorrectionKind.CorrectVote => _session.IsVotePhase && _session.HasVoteSummary && _session.SessionStateText == "Playing",
            _ => false,
        };
        if (canApply)
        {
            _pendingRevision = _session.Revision;
            SetPendingCorrection(kind);
        }
        else
        {
            AppendLocalHint("DM 提议的改正现在不能做，请直接在左侧操作。");
            SetPendingCorrection(null); // 新提议做不了时，旧提议也撤掉，免得「确认」执行的是上一条
        }
    }

    private async Task ConfirmCorrectionAsync()
    {
        if (_session.Busy || _pendingCorrection is null) return;
        if (_session.Revision != _pendingRevision)
        {
            SetPendingCorrection(null);
            AppendLocalHint("DM 提议的改正现在不能做，请直接在左侧操作。");
            return;
        }

        var kind = _pendingCorrection;
        SetPendingCorrection(null);
        if (kind == CorrectionKind.RevertPhase)
        {
            await _session.RevertPhaseAsync();
        }
        else if (kind == CorrectionKind.CorrectVote)
        {
            _session.BeginVoteCorrection();
        }
    }

    private void SetPendingCorrection(CorrectionKind? kind)
    {
        _pendingCorrection = kind;
        OnPropertyChanged(nameof(HasPendingCorrection));
        OnPropertyChanged(nameof(PendingCorrectionText));
        ConfirmCorrectionCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 停止：取消模型请求与播音，晚到的结果一律丢弃。
    /// 不回滚已经提交的游戏状态，也不删已经生成好的文字。
    /// </summary>
    private Task StopAsync()
    {
        _generation++;
        _director.Stop();

        _speakingMessage?.SetSpeech(ChatSpeechState.Stopped, "已停止朗读");
        _speakingMessage = null;


        IsGenerating = false;
        IsSpeaking = false;
        _session.SetNarrationBusy(false);
        SpeechState = SpeechState.Cancelled;
        Activity.Reset("就绪", PhaseDetail());
        Status = "已停止本次回应。本局进度没有变化。";
        AppendLocalHint("已停止本次回应。本局进度没有变化。");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 重播某一条主持回复：只用它已经存下来的文字，不再请求模型，也不重复提交状态。
    ///
    /// 重播期间同样占住会话的播报忙碌闸：否则旧音频还在播放时点「进入下一环节」，
    /// 阶段会真的推进，而紧接着那段播报因为宿主忙碌被直接丢弃 —— 主持人听不到新阶段。
    /// </summary>
    private async Task ReplayMessageAsync(ChatMessage message)
    {
        if (IsBusy || !message.CanReplay)
        {
            return;
        }

        var settings = _settingsProvider();
        var item = new SpeechItem
        {
            SpeechId = Guid.NewGuid().ToString("N"),
            RequestId = "replay-" + message.Id,
            GenerationId = "replay-" + message.Id,
            SafeText = message.Text,
        };

        var token = ++_generation;
        _speakingMessage = message;
        IsSpeaking = true;
        _session.SetNarrationBusy(true);
        message.SetSpeech(ChatSpeechState.Speaking, "正在重播…");
        Activity.Begin(ActivityKind.Speaking, "正在重播…", ["朗读"], PhaseDetail());
        try
        {
            var result = await _director.ReplayItemAsync(item, EffectiveVoice(settings), CancellationToken.None);
            if (token != _generation)
            {
                return;
            }

            // 只有真的念出来才算成功：失败时 director 不给 Item。
            if (result.Item is not null && result.State == SpeechState.Idle)
            {
                message.SetSpeech(ChatSpeechState.Spoken, "已重播（未请求模型）");
                Activity.Complete("已重播");
            }
            else if (result.State == SpeechState.Cancelled)
            {
                message.SetSpeech(ChatSpeechState.Stopped, "已停止朗读");
                Activity.Reset("就绪", PhaseDetail());
            }
            else
            {
                message.SetSpeech(
                    ChatSpeechState.Failed,
                    "重播失败：" + (result.PublicReason ?? "文字已保留，可稍后再试。"));
                Activity.Fail("重播没有完成", result.PublicReason ?? "文字已保留，可稍后再试。");
            }
        }
        catch (DomainRuleException ex)
        {
            if (token != _generation)
            {
                return;
            }

            message.SetSpeech(ChatSpeechState.Failed, ex.PublicReason);
            Activity.Fail("重播没有完成", ex.PublicReason);
        }
        finally
        {
            if (token == _generation)
            {
                IsSpeaking = false;
                _speakingMessage = null;
                _session.SetNarrationBusy(false);
                if (Activity.IsActive)
                {
                    Activity.Reset("就绪", PhaseDetail());
                }
            }
        }
    }

    /// <summary>
    /// 装载一局时把这一局的对话记忆读回来。
    ///
    /// 读到的记忆只影响发给模型的内容：不回放到屏幕上，也不朗读（恢复不自动重播）。
    /// </summary>
    private async Task LoadConversationAsync(string sessionId)
    {
        _conversation = new DmConversation();
        if (_store is not null)
        {
            try
            {
                var snapshot = await _store.LoadDmConversationAsync(sessionId, CancellationToken.None);
                _conversation = DmConversation.FromSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                // 记忆读不出来不该让这一局开不了：这一局从空记忆继续。
                LocalDm.Core.Diagnostics.RunLog.Write("Host", "读取对话记忆失败：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(Convert.ToString(ex.Message)));
            }
        }

        OnPropertyChanged(nameof(Memo));
        OnPropertyChanged(nameof(HasMemo));
        OnPropertyChanged(nameof(CompactionCountText));
        RefreshContextUsage();
        CompactCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 把本局对话记忆存档。存失败只在状态栏提示，不影响主持。
    /// </summary>
    private async Task SaveConversationAsync(int generation)
    {
        var store = _store;
        var sessionId = _conversationSessionId;
        var conversation = _conversation;
        if (store is null || sessionId is null)
        {
            return;
        }

        await _conversationSaveGate.WaitAsync();
        try
        {
            if (sessionId == _conversationSessionId && !ReferenceEquals(conversation, _conversation))
            {
                return;
            }

            await store.SaveDmConversationAsync(
                sessionId,
                conversation.ToSnapshot(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalDm.Core.Diagnostics.RunLog.Write("Host", "保存对话记忆失败：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(Convert.ToString(ex.Message)));
            if (generation == _generation
                && sessionId == _conversationSessionId
                && ReferenceEquals(conversation, _conversation))
            {
                Status = "对话记忆没有存下来；这一局照常主持。";
            }
        }
        finally
        {
            _conversationSaveGate.Release();
        }
    }

    /// <summary>当前环节的一行说明，放在状态行里帮助主持人定位。</summary>
    private string PhaseDetail()
    {
        var phase = _session.PhaseTitle;
        return string.IsNullOrEmpty(phase) || phase == "（未开始）" ? string.Empty : $"当前环节：{phase}";
    }

    /// <summary>空闲时状态行显示什么：就绪 + 当前环节。阶段变化时同步。</summary>
    private void SyncIdleActivity()
    {
        Activity.SetIdleState("就绪", PhaseDetail());
    }

    // ===== 消息维护 =====

    private ChatMessage AppendHost(string text, bool fromModel = true)
    {
        var message = new ChatMessage(ChatAuthor.Host, text) { IsModelReply = fromModel };
        if (fromModel)
        {
            message.SetSpeech(ChatSpeechState.Ready, "尚未朗读");
            message.ReplayCommand = new RelayCommand(
                () => ReplayMessageAsync(message),
                () => !IsBusy && message.CanReplay,
                allowReentry: true);
        }
        else
        {
            message.SetSpeech(ChatSpeechState.NotApplicable, "本机提示，未调用模型");
        }

        AddMessage(message);
        return message;
    }

    private void AppendPlayer(string text) => AddMessage(new ChatMessage(ChatAuthor.Player, text));

    private void AppendEvent(string text) => AddMessage(new ChatMessage(ChatAuthor.Event, text));

    /// <summary>
    /// 本机提示：出现在电脑的聊天区，但不进公开消息记录。
    /// 上下文满了、整理失败、已停止、装载失败这类话是给操作者看的，
    /// 平板那边只该看到桌上说过的话。
    /// </summary>
    private void AppendLocalHint(string text) =>
        AddMessage(new ChatMessage(ChatAuthor.Event, text) { IsLocalOnly = true });

    private void AddMessage(ChatMessage message)
    {
        Messages.Add(message);
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(ShowSuggestions));
        // 先落成公开消息，再通知外面：网页要看到的是已经存下来的那条。
        PublishPublicMessage(message);
        MessageAppended?.Invoke(this, message);
    }

    /// <summary>
    /// 把桌上的一条消息变成公开消息，排进写入队列。
    ///
    /// 只同步「桌上公开说过的话」：玩家发言、主持回复、公开流程事件。
    /// 本机提示（不调用模型的那些引导语）不进公开消息 ——
    /// 那是给操作者看的本机诊断，不是桌上说的话。
    /// </summary>
    private void PublishPublicMessage(ChatMessage message)
    {
        var publisher = _publicFeed;
        var sessionId = _conversationSessionId;
        if (publisher is null || string.IsNullOrEmpty(sessionId))
        {
            // 还没绑定到任何一局：没有「本局」可写，也就没有公开消息。
            return;
        }

        if (message.IsLocalOnly)
        {
            // 本机提示：给操作者看的，不进公开消息记录。
            return;
        }

        PublicMessageKind kind;
        PublicMessageAuthor author;
        if (message.IsPlayer)
        {
            kind = PublicMessageKind.PlayerSpeech;
            author = PublicMessageAuthor.Player;
        }
        else if (message.IsEvent)
        {
            kind = PublicMessageKind.FlowEvent;
            author = PublicMessageAuthor.System;
        }
        else if (message.IsModelReply)
        {
            kind = PublicMessageKind.HostReply;
            author = PublicMessageAuthor.Host;
        }
        else
        {
            // 本机提示（不调用模型的那些引导语）：不是桌上说的话，不同步。
            return;
        }

        publisher.Publish(new PublicMessage
        {
            SessionId = sessionId,
            MessageId = message.Id,
            Author = author,
            Text = message.Text,
            CreatedAt = DateTimeOffset.UtcNow,
            Kind = kind,
        });
    }

    /// <summary>公开消息写入队列：桌上每落一条消息就往这里排一条。</summary>
    private readonly PublicMessagePublisher? _publicFeed;

    /// <summary>同步断了没有：断了就不能对外说「已同步」。</summary>
    public bool HasPublicSyncError => _publicSyncError is not null;

    /// <summary>同步异常的原因，直接展示给主持人。</summary>
    public string PublicSyncErrorText =>
        _publicSyncError is null ? string.Empty : "同步异常：" + _publicSyncError;

    private string? _publicSyncError;

    /// <summary>重试把没写成功的公开消息写回去。</summary>
    public RelayCommand RetryPublicSyncCommand { get; }

    /// <summary>一条公开消息真的落盘之后触发：网页据此通知平板刷新。</summary>
    public event EventHandler<PublicMessage>? PublicMessageCommitted;

    private void OnPublicFeedFailed(object? sender, string reason)
    {
        // 同步异常只报在本机状态行与横幅上，不进公共聊天区：
        // 报到聊天区会再触发一次写入，形成自己喊自己。
        _publicSyncError = reason;
        OnPropertyChanged(nameof(HasPublicSyncError));
        OnPropertyChanged(nameof(PublicSyncErrorText));
        RetryPublicSyncCommand.RaiseCanExecuteChanged();
        Status = "同步异常：" + reason;
    }

    private void OnPublicFeedRecovered(object? sender, EventArgs e)
    {
        _publicSyncError = null;
        OnPropertyChanged(nameof(HasPublicSyncError));
        OnPropertyChanged(nameof(PublicSyncErrorText));
        RetryPublicSyncCommand.RaiseCanExecuteChanged();
        Status = "公开消息已经补齐，平板可以继续同步。";
    }

    private void OnPublicFeedCommitted(object? sender, PublicMessage message)
    {
        PublicMessageCommitted?.Invoke(this, message);
    }

    /// <summary>有新消息落地；视图据此把对话滚到底。</summary>
    public event EventHandler<ChatMessage>? MessageAppended;

    /// <summary>当前装载的公开文字，供视图在材料页里显示。</summary>
    public string? RevealedText => _session.RevealedText;

    /// <summary>头部摘要只包含阶段与状态，不含任何私有内容，也不报内部 revision。</summary>
    public string SessionSummary =>
        _session.Projection is null
            ? "当前没有装载一局。"
            : $"{_session.PhaseTitle} · {_session.SessionStateLabel}";

    /// <summary>会话状态变化时把权威状态同步到主持台头部，避免显示装载时的旧值。</summary>
    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.SessionStateText)
            or nameof(SessionViewModel.Revision)
            or nameof(SessionViewModel.PhaseTitle))
        {
            OnPropertyChanged(nameof(SessionSummary));
            OnPropertyChanged(nameof(ShowOpeningSpeech));
            OnPropertyChanged(nameof(ShowSuggestions));
            OpeningSpeechCommand.RaiseCanExecuteChanged();
            AskStatusCommand.RaiseCanExecuteChanged();
            AskRulesCommand.RaiseCanExecuteChanged();
            SyncIdleActivity();
        }

        // 只在「还没有揭秘内容 → 有揭秘内容」的那一刻自动切到材料/复盘页。
        // 注意只能在 HasRevealedText 事件里更新 _hadRevealedText：
        // RefreshView 会先发 PhaseTitle 等事件，那时投影已经是新值，
        // 若在每个事件里都同步，跃迁会被提前吃掉，自动切换就再也不会发生。
        if (e.PropertyName == nameof(SessionViewModel.HasRevealedText))
        {
            var hasRevealed = _session.HasRevealedText;
            if (hasRevealed && !_hadRevealedText)
            {
                ShowMaterials = true;
            }

            _hadRevealedText = hasRevealed;
            OnPropertyChanged(nameof(MaterialsTitle));
            OnPropertyChanged(nameof(HasMaterialsTab));
            OnPropertyChanged(nameof(RevealedText));
        }
        if (e.PropertyName == nameof(SessionViewModel.Busy)) { ConfirmCorrectionCommand.RaiseCanExecuteChanged(); }
        if (e.PropertyName == nameof(SessionViewModel.Revision) && _pendingCorrection is not null && _session.Revision != _pendingRevision)
        {
            SetPendingCorrection(null); // 手动推进、改票等让状态变了：旧提议不能再作用到新环节
        }
    }

    /// <summary>上一次刷新时是否已有揭秘内容；用来识别 false → true 的跃迁。</summary>
    private bool _hadRevealedText;

    /// <summary>主区标题变化（对话 / 公开材料）。</summary>
    public event EventHandler? TitleChanged;

    private void OnShellTitleChanged() => TitleChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 报告一个主持人需要知道的问题（装载失败、继续失败等）。
    /// 对外只给固定文案，细节写诊断日志。
    /// </summary>
    public void ReportProblem(string message)
    {
        Status = message;
        AppendLocalHint(message);
    }

    private async Task ToggleDictationAsync()
    {
        if (_dictation is null || _dictationBusy)
        {
            return;
        }

        if (!IsListening)
        {
            _tts?.Stop();
            DictationHint = "";
            _dictationBusy = true;
            var problem = await _dictation.StartAsync(CancellationToken.None);
            _dictationBusy = false;
            if (problem is not null)
            {
                DictationHint = problem;
                return;
            }

            IsListening = true;
            DictationHint = "正在听…";
            _dictationTimer = new DispatcherTimer { Interval = DictationLimit };
            _dictationTimer.Tick += (_, _) => _ = StopDictationAsync();
            _dictationTimer.Start();
        }
        else
        {
            await StopDictationAsync();
        }
    }

    private async Task StopDictationAsync()
    {
        if (!IsListening)
        {
            return;
        }

        _dictationTimer?.Stop();
        _dictationTimer = null;
        IsListening = false;
        _dictationBusy = true;
        DictationHint = "正在识别…";
        var result = await _dictation!.StopAsync(CancellationToken.None);
        _dictationBusy = false;
        if (!string.IsNullOrEmpty(result.Text))
        {
            InputText = string.IsNullOrWhiteSpace(InputText)
                ? result.Text
                : InputText.TrimEnd() + " " + result.Text;
            DictationHint = "";
        }
        else
        {
            DictationHint = result.Problem ?? "";
        }
    }

    /// <summary>
    /// 释放状态条的计时器。
    /// </summary>
    public void Dispose()
    {
        _dictationTimer?.Stop();
        _dictationTimer = null;
        Activity.Dispose();
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

        public Task<IReadOnlyList<StoredSessionRef>> ListSessionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredSessionRef>>([]);

        public Task SaveSessionAsync(Session session, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("未接线的会话存储不能保存。");

        public Task<DmConversationSnapshot?> LoadDmConversationAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<DmConversationSnapshot?>(null);

        public Task SaveDmConversationAsync(string sessionId, DmConversationSnapshot snapshot, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("未接线的会话存储不能保存对话记忆。");
    }
}

