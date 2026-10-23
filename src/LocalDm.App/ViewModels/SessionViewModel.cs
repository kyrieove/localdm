using System.Collections.ObjectModel;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 一局的公共视图：左侧进度轨道、当前流程提示、可用动作与材料编号。
///
/// 两条硬规则：
/// 1) 阶段数量、顺序、标题、提示、动作名与材料全部来自剧本包，不按固定序号或中文标题判断；
/// 2) 点击即确认 —— 界面上没有「大家准备好了」「已核对」这类勾选框，
///    真正拦人的只有机器能验证的条件（阶段、票数、材料、版本），由规则核心裁决。
/// </summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly CommandDispatcher _dispatcher;
    private ScriptPackage? _package;
    private PublicSessionProjection? _projection;
    private string _status = string.Empty;
    private string _voteInput = string.Empty;
    private bool _busy;
    private bool _narrationBusy;

    public SessionViewModel(CommandDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        StartCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Start));
        AdvanceCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Advance));
        RevealCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Reveal));
        PauseResumeCommand = new RelayCommand(() => ExecuteAsync(IsPaused ? GameCommandType.Resume : GameCommandType.Pause));
        CompleteCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Complete));
        SubmitVoteCommand = new RelayCommand(SubmitVoteAsync);
        ReleaseMaterialsCommand = new RelayCommand(ReleaseMaterialsAsync);
        RestartCommand = new RelayCommand(() => { SetRestartConfirming(true); return Task.CompletedTask; });
        CancelRestartCommand = new RelayCommand(() => { SetRestartConfirming(false); return Task.CompletedTask; });
        ConfirmRestartCommand = new RelayCommand(() =>
        {
            SetRestartConfirming(false);
            if (_package is not null) { RestartRequested?.Invoke(this, _package); }
            return Task.CompletedTask;
        });
    }

    // ===== 重新开始：丢掉这一局的进度与对话，回到「开始这一局」之前 =====

    /// <summary>确认后触发；参数是当前包。清存档、复位平板由外层做。</summary>
    public event EventHandler<ScriptPackage>? RestartRequested;

    public RelayCommand RestartCommand { get; }

    public RelayCommand ConfirmRestartCommand { get; }

    public RelayCommand CancelRestartCommand { get; }

    /// <summary>开过局就能重来（含玩完的局）；还没开局时没有可丢的进度。</summary>
    public bool IsRestartVisible => _projection is not null && !IsPrepared && !IsRestartConfirming;

    public bool CanRestart => !Busy;

    /// <summary>第一次点只展开确认行：这一步会清掉进度和对话，不可撤销。</summary>
    public bool IsRestartConfirming { get; private set; }

    public string RestartWarning => "会清掉这一局的进度、对话和平板上的角色认领，回到开局前。不能撤销。";

    private void SetRestartConfirming(bool value)
    {
        IsRestartConfirming = value;
        OnPropertyChanged(nameof(IsRestartConfirming));
        OnPropertyChanged(nameof(IsRestartVisible));
    }

    public ObservableCollection<string> PhaseConditions { get; } = [];

    public ObservableCollection<string> PendingMaterials { get; } = [];

    public ObservableCollection<string> ReleasedMaterials { get; } = [];

    /// <summary>阶段章标：整局的阶段顺序，标出已过与当前，供主持人一眼看清进度。</summary>
    public ObservableCollection<PhaseRailItem> PhaseRail { get; } = [];

    public string PhaseTitle => _projection?.CurrentPhase.Title ?? "（未开始）";

    /// <summary>进度读数，例如「2 / 7」。阶段总数来自剧本包，不是常量。</summary>
    public string PhaseProgress => _projection is null
        ? string.Empty
        : $"{_projection.CurrentPhase.Index} / {_projection.CurrentPhase.Count}";

    /// <summary>当前流程提示：剧本给的那一句，没给就退回按原本条件拼接。</summary>
    public string Hint
    {
        get
        {
            var phase = _projection?.CurrentPhase;
            if (phase is null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(phase.Hint))
            {
                return phase.Hint;
            }

            return string.Join("；", PhaseConditions);
        }
    }

    /// <summary>契约状态原文（Prepared/Playing/...），供测试与存档对齐。</summary>
    public string SessionStateText => _projection?.State ?? "Prepared";

    /// <summary>界面上显示的中文状态；与契约值分开，避免显示层反向影响状态语义。</summary>
    public string SessionStateLabel => _projection?.State switch
    {
        nameof(SessionState.Prepared) => "待开局",
        nameof(SessionState.Playing) => "进行中",
        nameof(SessionState.Paused) => "已暂停",
        nameof(SessionState.Revealed) => "已揭秘",
        nameof(SessionState.Completed) => "已结束",
        _ => "　",
    };

    public int Revision => _projection?.Revision ?? 0;

    public string PublicTitle => _projection?.PublicTitle ?? string.Empty;

    public int PlayerCount => _projection?.PlayerCount ?? 0;

    public string VoteInput
    {
        get => _voteInput;
        set
        {
            if (SetProperty(ref _voteInput, value))
            {
                OnPropertyChanged(nameof(VoteHint));
                OnPropertyChanged(nameof(CanSubmitVote));
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public PublicSessionProjection? Projection => _projection;

    /// <summary>一次状态提交成功后被触发；参数是命令类型。供主持台接上对话与播报。</summary>
    public event EventHandler<GameCommandType>? Committed;

    /// <summary>当前装载的剧本包；未装载时为 null。仅供主持台构造提示词与公开依据。</summary>
    public ScriptPackage? Package => _package;

    /// <summary>
    /// 由公共投影还原出的会话视图，只用于构造提示词与核对依据。
    /// 不含真相与私有段，确保主持台拿不到未公开内容。
    /// </summary>
    public Session? Session => _package is null || _projection is null
        ? null
        : new Session
        {
            SessionId = _projection.SessionId,
            PackageId = _package.PackageId,
            PackageVersion = _package.Version,
            ManifestHash = _package.ManifestHash,
            State = Enum.TryParse<SessionState>(_projection.State, out var state) ? state : SessionState.Prepared,
            CurrentPhaseId = _projection.CurrentPhase.PhaseId,
            Revision = _projection.Revision,
            ReleasedMaterialIds = _projection.ReleasedMaterialIds,
            // 投影里没有 ResumeState，但「投影里有真相」就等价于「已解锁」。
            // 暂停态下必须把这件事带回去：否则真相会从提示词里消失，
            // 而屏幕上还明明白白显示着真相 —— 那是模型与界面各说各话。
            ResumeState = state == SessionState.Paused && _projection.RevealedTexts is { Count: > 0 }
                ? SessionState.Revealed
                : null,
        };

    /// <summary>
    /// 忙碌闸：命令派发中或播报中都不接受新的阶段命令。
    /// 两者分开记，是因为命令派发结束得比播报早 —— 用同一个标志会被派发结束提前清掉。
    /// </summary>
    public bool Busy => _busy || _narrationBusy;

    private bool IsPrepared => _projection?.State == nameof(SessionState.Prepared);

    private bool IsPlaying => _projection?.State == nameof(SessionState.Playing);

    private bool IsPaused => _projection?.State == nameof(SessionState.Paused);

    private bool IsRevealed =>
        _projection?.State is nameof(SessionState.Revealed);

    private bool IsCompleted => _projection?.State is nameof(SessionState.Completed);

    private bool Allows(PublicAction action) =>
        _projection?.CurrentPhase.AllowedActions.Contains(action) == true;

    /// <summary>标记一次状态提交后的播报正在进行。</summary>
    public void SetNarrationBusy(bool value)
    {
        if (_narrationBusy == value)
        {
            return;
        }

        _narrationBusy = value;
        RaiseBusyChanged();
    }

    private void SetCommandBusy(bool value)
    {
        if (_busy == value)
        {
            return;
        }

        _busy = value;
        RaiseBusyChanged();
    }

    private void RaiseBusyChanged()
    {
        OnPropertyChanged(nameof(Busy));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(IsBlockedByMaterials));
        OnPropertyChanged(nameof(CanSubmitVote));
        OnPropertyChanged(nameof(CanReveal));
        OnPropertyChanged(nameof(CanReleaseMaterials));
        OnPropertyChanged(nameof(CanPauseResume));
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CanRestart));
    }

    // ===== 开局 =====

    /// <summary>只有 Prepared 才能开局；已开始的局再点开始没有意义。</summary>
    public bool IsStartVisible => IsPrepared;

    public bool CanStart => !Busy && IsPrepared;

    public string StartLabel => "开始这一局";

    // ===== 推进：动作名来自剧本包 =====

    /// <summary>
    /// 推进按钮只在「点了真的能走」时出现：
    /// 投票未汇总、真相未公开、已到最后一环节，都不给一个点了会报错的按钮。
    /// </summary>
    public bool IsAdvanceVisible =>
        IsPlaying
        && Allows(PublicAction.Advance)
        && !(IsCurrentPhaseVote && !HasVoteSummary)
        && !IsCurrentPhaseReveal;

    public bool CanAdvance => !Busy && IsAdvanceVisible && !IsBlockedByMaterials;

    /// <summary>
    /// 剧本声明「本轮材料必须发完才能推进」时，按钮先灰掉：
    /// 让人一眼看出还差材料，而不是点了才被规则核心拒绝。
    /// </summary>
    public bool IsBlockedByMaterials =>
        _projection?.CurrentPhase.RequiresAllMaterials == true && PendingMaterials.Count > 0;

    /// <summary>例如「进入第二轮搜证」；剧本没给下一环节时退回固定文案。</summary>
    public string AdvanceLabel =>
        string.IsNullOrWhiteSpace(_projection?.CurrentPhase.AdvanceLabel)
            ? "结束本局"
            : _projection!.CurrentPhase.AdvanceLabel!;

    // ===== 材料 =====

    public bool IsReleaseVisible => IsPlaying && Allows(PublicAction.ReleaseMaterials) && PendingMaterials.Count > 0;

    public bool CanReleaseMaterials => !Busy && IsReleaseVisible;

    /// <summary>逐个发放：按钮上直接写清这一次要发哪个编号。</summary>
    public string ReleaseLabel => PendingMaterials.Count > 0
        ? $"发放材料 {PendingMaterials[0]}"
        : "发放材料";

    /// <summary>本环节有没有材料编号可发。没有就不占位。</summary>
    public bool HasMaterialSection => PendingMaterials.Count > 0 || ReleasedMaterials.Count > 0;

    /// <summary>还有几个编号没发。主持人一眼知道这一轮要发几堆牌。</summary>
    public string PendingMaterialSummary =>
        PendingMaterials.Count == 0
            ? string.Empty
            : $"还有 {PendingMaterials.Count} 个编号未发放";

    // ===== 投票 =====

    /// <summary>只有投票环节、且还没汇总时才出现录入入口。</summary>
    public bool IsVoteVisible => IsPlaying && IsCurrentPhaseVote && !HasVoteSummary;

    public bool CanSubmitVote => !Busy && IsVoteVisible && TryParseVoteInput(out _, out _);

    /// <summary>录入过程中的即时反馈：已录入多少票、还差几人。</summary>
    public string VoteHint
    {
        get
        {
            if (!TryParseVoteInput(out var summary, out var error))
            {
                return error;
            }

            var total = summary.Total;
            var players = PlayerCount;
            return total == players
                ? $"已录入 {total} / {players} 票，点击下方即确认提交。"
                : $"已录入 {total} / {players} 票" + (total > players ? "，票数超过人数。" : "。");
        }
    }

    /// <summary>票数汇总按钮：点击即确认，没有额外的「已核对」勾选。</summary>
    public string SubmitVoteLabel => "确认提交汇总";

    /// <summary>投票成功后公开生效的票数汇总展示；未录入前为 null。</summary>
    public string? VoteSummaryDisplay => _projection?.VoteSummary is { Counts.Count: > 0 } vs
        ? "已生效票数：" + string.Join("，", vs.Counts.Select(c => $"{c.Option}={c.Count}")) + $"（共 {vs.TotalVotes} 票）"
        : null;

    public bool HasVoteSummary => !string.IsNullOrEmpty(VoteSummaryDisplay);

    // ===== 揭秘与复盘 =====

    /// <summary>揭秘只在真相环节、且尚未公开时出现。</summary>
    public bool IsRevealVisible => IsPlaying && IsCurrentPhaseReveal;

    public bool CanReveal => !Busy && IsRevealVisible;

    /// <summary>揭秘动作名，例如「公开结局」。剧本没写就用「公开真相」。</summary>
    public string RevealLabel =>
        string.IsNullOrWhiteSpace(_projection?.CurrentPhase.RevealLabel)
            ? "公开真相"
            : _projection!.CurrentPhase.RevealLabel!;

    /// <summary>按钮旁必须说清后果：点一下就对所有玩家公开结局。</summary>
    public string RevealWarning => $"点击「{RevealLabel}」会向所有玩家公开结局，无需再次确认。";

    public bool IsCompleteVisible => IsRevealed || (IsPaused && HasRevealedText);

    public bool CanComplete => !Busy && IsCompleteVisible;

    public string CompleteLabel => "进入复盘";

    // ===== 暂停 / 继续 =====

    public bool IsPauseResumeVisible =>
        IsPlaying || IsPaused || IsRevealed;

    public bool CanPauseResume => !Busy && ((IsPlaying || IsRevealed) || IsPaused);

    public string PauseResumeLabel => IsPaused ? "继续本局" : "暂停本局";

    public bool IsPausedNow => IsPaused;

    /// <summary>
    /// 当前是不是投票环节。决定左侧是否给出投票录入入口。
    /// </summary>
    public bool IsVotePhase => IsCurrentPhaseVote;

    /// <summary>当前是不是真相环节（或已经揭秘）。</summary>
    public bool IsRevealPhase => IsCurrentPhaseReveal || IsRevealed || IsCompleted;

    /// <summary>动作区下方那行说明：机器人条件与「点击即确认」的区别写在这里。</summary>
    public string ActionHint
    {
        get
        {
            if (_projection is null)
            {
                return string.Empty;
            }

            if (IsPaused)
            {
                return "进度已保存。继续后仍停在本步骤。";
            }

            if (IsCompleted)
            {
                return "本局已结束，对话与公开内容仍可查看。";
            }

            if (IsRevealed)
            {
                return "公开内容已在大屏展开，点「进入复盘」一起回顾。";
            }

            if (IsVoteVisible)
            {
                return $"汇总总票数应等于参与人数 {PlayerCount}。点击提交即确认。";
            }

            if (IsRevealVisible)
            {
                return "点击即向所有玩家公开结局，不再重复确认。";
            }

            if (PendingMaterials.Count > 0)
            {
                return $"还有 {PendingMaterials.Count} 份本环节材料待发放。";
            }

            return "完成当前步骤后，直接点击动作继续，不需要额外勾选。";
        }
    }

    public bool HasRevealedText => _projection?.RevealedTexts is { Count: > 0 };

    /// <summary>只有解锁后才存在的揭秘文字；未解锁时为 null，不是隐藏字符串。</summary>
    public string? RevealedText => _projection?.RevealedTexts is { Count: > 0 } texts
        ? string.Join("\n", texts)
        : null;

    /// <summary>
    /// 去掉 Markdown 语法与扫描页码后的揭秘文字。
    /// 原样上屏会把 <c>##</c>、<c>```</c> 这些语法直接糊到桌面上。
    /// </summary>
    public string RevealedTextClean => TruthDocument.Clean(RevealedText);

    /// <summary>
    /// 揭秘原文拆成的块：标题 / 等宽块 / 管道表格 / 段落。
    /// 复盘大屏按块渲染，等宽块才会带上列参考线。
    /// </summary>
    public IReadOnlyList<TruthBlock> TruthBlocks => _truthBlocks;

    private IReadOnlyList<TruthBlock> _truthBlocks = [];

    /// <summary>已解析的原文指纹，避免每次刷新都重新拆一遍。</summary>
    private string? _parsedTruthSource;

    public RelayCommand StartCommand { get; }

    public RelayCommand AdvanceCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand PauseResumeCommand { get; }

    public RelayCommand CompleteCommand { get; }

    public RelayCommand SubmitVoteCommand { get; }

    public RelayCommand ReleaseMaterialsCommand { get; }

    private bool IsCurrentPhaseVote => _projection?.CurrentPhase.PhaseId is { } id
                                       && _package?.Phases.FirstOrDefault(p => p.PhaseId == id)?.IsVotePhase == true;

    private bool IsCurrentPhaseReveal => _projection?.CurrentPhase.PhaseId is { } id
                                         && _package?.Phases.FirstOrDefault(p => p.PhaseId == id)?.IsRevealPhase == true;

    public void Attach(ScriptPackage package, Session session)
    {
        _package = package;
        _projection = PublicSessionProjector.Project(package, session);
        RefreshView(package, _projection);
    }

    /// <summary>
    /// 执行一次命令。模型与自然语言都不能走到这里；只有明确的界面动作会调用。
    ///
    /// 一次点击只提交一次：入口由 <see cref="Busy"/> 挡住重入，命令自带唯一 operationId，
    /// 并带上点击时的 ExpectedRevision —— 连点或重试只会得到 STALE_REVISION，不会重复生效。
    /// </summary>
    public async Task ExecuteAsync(
        GameCommandType type,
        IReadOnlyList<string>? materials = null,
        VoteSummary? votes = null)
    {
        if (Busy)
        {
            // 正在派发或播报：给一句提示，别让主持人以为按钮坏了。
            Status = "上一条操作还在进行中，请稍候。";
            return;
        }

        if (_package is null || _projection is null)
        {
            Status = "当前没有可操作的一局。";
            return;
        }

        var command = new GameCommand
        {
            SessionId = _projection.SessionId,
            OperationId = Guid.NewGuid().ToString("N"),
            ExpectedRevision = _projection.Revision,
            Type = type,
            MaterialIds = materials ?? [],
            Votes = votes,
        };

        SetCommandBusy(true);
        try
        {
            var result = await _dispatcher.DispatchAsync(command, _package, CancellationToken.None);
            switch (result.Outcome)
            {
                case DispatchOutcome.Committed when result.Projection is not null:
                    _projection = result.Projection;
                    RefreshView(_package, _projection);
                    Status = CommandStatus(type, _projection);

                    // 状态已由程序裁决；主持台据此写入阶段事件并请求播报措辞
                    Committed?.Invoke(this, type);
                    break;
                case DispatchOutcome.Duplicate:
                    Status = "这条操作之前已经提交过，没有重复执行。";
                    break;
                default:
                    Status = result.Rejection?.PublicReason ?? "操作被拒绝。";
                    break;
            }
        }
        finally
        {
            SetCommandBusy(false);
        }
    }

    /// <summary>
    /// 提交后的公开结果文案。只写玩家需要知道的事，不出现 revision、回执或工具细节。
    /// </summary>
    private string CommandStatus(GameCommandType type, PublicSessionProjection projection)
    {
        var phase = projection.CurrentPhase.Title;
        return type switch
        {
            GameCommandType.Start => $"本局已开始。现在是「{phase}」。",
            GameCommandType.Advance => $"已进入「{phase}」。",
            GameCommandType.ReleaseMaterials => "材料编号已公开。",
            GameCommandType.SubmitVoteSummary => "票数汇总已生效。",
            GameCommandType.Reveal => "结局已公开。",
            GameCommandType.Pause => "本局已暂停，进度已保存。",
            GameCommandType.Resume => $"已继续，仍停在「{phase}」。",
            GameCommandType.Complete => "已进入复盘。",
            _ => "已提交。",
        };
    }

    private async Task SubmitVoteAsync()
    {
        if (!TryParseVoteInput(out var votes, out var error))
        {
            Status = error;
            return;
        }

        await ExecuteAsync(GameCommandType.SubmitVoteSummary, votes: votes);
    }

    private async Task ReleaseMaterialsAsync()
    {
        var material = PendingMaterials.FirstOrDefault();
        if (material is null)
        {
            Status = "当前阶段没有待发放的材料编号。";
            return;
        }

        await ExecuteAsync(GameCommandType.ReleaseMaterials, materials: [material]);
    }

    private bool TryParseVoteInput(out VoteSummary summary, out string error) =>
        TryParseVotes(VoteInput, out summary, out error);

    /// <summary>解析「A=3, B=2」形式的纸笔汇总；负数、非整数、空选项都拒绝。</summary>
    public static bool TryParseVotes(string text, out VoteSummary summary, out string error)
    {
        summary = new VoteSummary { Counts = [] };
        error = string.Empty;
        var parts = text.Split(',', '，', ';', '；')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length == 0)
        {
            error = "请按「选项=票数」录入，例如 A=3, B=2。";
            return false;
        }

        var counts = new List<VoteCount>();
        foreach (var part in parts)
        {
            var kv = part.Split('=', ':', '：');
            if (kv.Length != 2)
            {
                error = "票数格式不正确，请用 选项=票数。";
                return false;
            }

            var option = kv[0].Trim();
            if (option.Length == 0 || !int.TryParse(kv[1].Trim(), out var count) || count < 0)
            {
                error = "票数必须是非负整数，选项不能为空。";
                return false;
            }

            counts.Add(new VoteCount { Option = option, Count = count });
        }

        summary = new VoteSummary { Counts = counts };
        return true;
    }

    private void RefreshView(ScriptPackage package, PublicSessionProjection projection)
    {
        var phase = package.Phases.FirstOrDefault(p => p.PhaseId == projection.CurrentPhase.PhaseId);

        // 阶段章标：按剧本包给的阶段顺序，标出当前与已过
        var track = projection.PhaseTrack;
        var currentIndex = track
            .Select((p, i) => (p.PhaseId, Index: i))
            .FirstOrDefault(t => t.PhaseId == projection.CurrentPhase.PhaseId).Index;

        PhaseRail.Clear();
        for (var i = 0; i < track.Count; i++)
        {
            var isCurrent = i == currentIndex;
            PhaseRail.Add(new PhaseRailItem(
                track[i].Title,
                IsCurrent: isCurrent,
                IsPast: i < currentIndex,
                Status: isCurrent ? SessionStateLabel : string.Empty));
        }

        PhaseConditions.Clear();
        foreach (var condition in phase?.Conditions ?? [])
        {
            PhaseConditions.Add(condition);
        }

        PendingMaterials.Clear();
        foreach (var material in phase?.MaterialIds ?? [])
        {
            if (!projection.ReleasedMaterialIds.Contains(material, StringComparer.Ordinal))
            {
                PendingMaterials.Add(material);
            }
        }

        ReleasedMaterials.Clear();
        foreach (var material in projection.ReleasedMaterialIds)
        {
            ReleasedMaterials.Add(material);
        }

        OnPropertyChanged(nameof(PhaseTitle));
        OnPropertyChanged(nameof(PhaseProgress));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(SessionStateText));
        OnPropertyChanged(nameof(SessionStateLabel));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(PublicTitle));
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(HasRevealedText));
        OnPropertyChanged(nameof(RevealedText));
        OnPropertyChanged(nameof(RevealedTextClean));
        RefreshTruthBlocks();
        OnPropertyChanged(nameof(VoteSummaryDisplay));
        OnPropertyChanged(nameof(HasVoteSummary));
        OnPropertyChanged(nameof(VoteHint));
        OnPropertyChanged(nameof(ActionHint));
        OnPropertyChanged(nameof(IsStartVisible));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsAdvanceVisible));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(IsBlockedByMaterials));
        OnPropertyChanged(nameof(AdvanceLabel));
        OnPropertyChanged(nameof(IsReleaseVisible));
        OnPropertyChanged(nameof(CanReleaseMaterials));
        OnPropertyChanged(nameof(ReleaseLabel));
        OnPropertyChanged(nameof(IsVoteVisible));
        OnPropertyChanged(nameof(CanSubmitVote));
        OnPropertyChanged(nameof(IsRevealVisible));
        OnPropertyChanged(nameof(CanReveal));
        OnPropertyChanged(nameof(RevealLabel));
        OnPropertyChanged(nameof(RevealWarning));
        OnPropertyChanged(nameof(IsCompleteVisible));
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(IsPauseResumeVisible));
        OnPropertyChanged(nameof(CanPauseResume));
        OnPropertyChanged(nameof(PauseResumeLabel));
        OnPropertyChanged(nameof(IsPausedNow));
        OnPropertyChanged(nameof(IsRestartVisible));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(IsVotePhase));
        OnPropertyChanged(nameof(IsRevealPhase));
        OnPropertyChanged(nameof(HasMaterialSection));
        OnPropertyChanged(nameof(PendingMaterialSummary));
        OnPropertyChanged(nameof(Busy));
    }

    /// <summary>重新解析揭秘原文（只在原文变化时做，桌边刷新不用反复拆）。</summary>
    private void RefreshTruthBlocks()
    {
        var source = RevealedText;
        if (string.Equals(source, _parsedTruthSource, StringComparison.Ordinal))
        {
            return;
        }

        _parsedTruthSource = source;
        _truthBlocks = TruthDocument.Parse(source);
        OnPropertyChanged(nameof(TruthBlocks));
    }
}

/// <summary>阶段章标的一项：标题与它在整局中的位置。</summary>
public sealed record PhaseRailItem(string Title, bool IsCurrent, bool IsPast, string Status = "");
