using System.Collections.ObjectModel;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 一局主持：展示无剧透阶段条件、大家准备勾选、材料提示、纸笔汇总与揭秘确认。
/// 只拿公共投影；命令一律经 CommandDispatcher。
/// </summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly CommandDispatcher _dispatcher;
    private ScriptPackage? _package;
    private PublicSessionProjection? _projection;
    private bool _groupReady;
    private bool _voteConfirmed;
    private bool _revealConfirmed;
    private string _status = string.Empty;
    private string _voteInput = string.Empty;

    public SessionViewModel(CommandDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        StartCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Start));
        AdvanceCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Advance));
        RevealCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Reveal));
        PauseCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Pause));
        ResumeCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Resume));
        CompleteCommand = new RelayCommand(() => ExecuteAsync(GameCommandType.Complete));
        SubmitVoteCommand = new RelayCommand(SubmitVoteAsync);
        ReleaseMaterialsCommand = new RelayCommand(ReleaseMaterialsAsync);
    }

    public ObservableCollection<string> PhaseConditions { get; } = [];

    public ObservableCollection<string> PendingMaterials { get; } = [];

    public ObservableCollection<string> ReleasedMaterials { get; } = [];

    public ObservableCollection<string> AllowedActionLabels { get; } = [];

    /// <summary>阶段章标：整局的阶段顺序，标出已过与当前，供主持人一眼看清进度。</summary>
    public ObservableCollection<PhaseRailItem> PhaseRail { get; } = [];

    public string PhaseTitle => _projection?.CurrentPhase.Title ?? "（未开始）";

    /// <summary>契约状态原文（Prepared/Playing/...），供测试与存档对齐。</summary>
    public string SessionStateText => _projection?.State ?? "Prepared";

    /// <summary>界面上显示的中文状态；与契约值分开，避免显示层反向影响状态语义。</summary>
    public string SessionStateLabel => _projection?.State switch
    {
        nameof(SessionState.Prepared) => "待开局",
        nameof(SessionState.Playing) => "进行中",
        nameof(SessionState.Paused) => "已暂停",
        nameof(SessionState.Revealed) => "已揭秘",
        nameof(SessionState.Completed) => "已复盘",
        _ => "　",
    };

    public int Revision => _projection?.Revision ?? 0;

    public string PublicTitle => _projection?.PublicTitle ?? string.Empty;

    public bool GroupReady
    {
        get => _groupReady;
        set => SetProperty(ref _groupReady, value);
    }

    public bool VoteConfirmed
    {
        get => _voteConfirmed;
        set => SetProperty(ref _voteConfirmed, value);
    }

    /// <summary>揭秘的独立二次确认：不能复用票数汇总的确认，两者是不同的操作。</summary>
    public bool RevealConfirmed
    {
        get => _revealConfirmed;
        set => SetProperty(ref _revealConfirmed, value);
    }

    public string VoteInput
    {
        get => _voteInput;
        set => SetProperty(ref _voteInput, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public PublicSessionProjection? Projection => _projection;

    /// <summary>一次状态提交成功后被触发；参数是命令类型。供主持台接上模型播报。</summary>
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
        };

    /// <summary>只有 Prepared 才能开局；已开始的局再点开始没有意义。</summary>
    public bool CanStart => _projection?.State == nameof(SessionState.Prepared);

    /// <summary>按钮可用性：让不可用的操作直接灰掉，而不是点了没反应。</summary>
    public bool CanAdvance => _projection?.State == nameof(SessionState.Playing);

    public bool CanSubmitVote => _projection?.State == nameof(SessionState.Playing) && IsCurrentPhaseVote;

    public bool CanReveal => _projection?.State == nameof(SessionState.Playing) && IsCurrentPhaseReveal;

    public bool CanReleaseMaterials => _projection?.State == nameof(SessionState.Playing) && PendingMaterials.Count > 0;

    public bool CanPause => _projection?.State is nameof(SessionState.Playing) or nameof(SessionState.Revealed);

    public bool CanResume => _projection?.State == nameof(SessionState.Paused);

    public bool CanComplete => _projection?.State is nameof(SessionState.Revealed) or nameof(SessionState.Paused)
                               && _projection.RevealedTexts is not null;

    private bool IsCurrentPhaseVote =>
        _package?.Phases.FirstOrDefault(p => p.PhaseId == _projection?.CurrentPhase.PhaseId)?.IsVotePhase == true;

    private bool IsCurrentPhaseReveal =>
        _package?.Phases.FirstOrDefault(p => p.PhaseId == _projection?.CurrentPhase.PhaseId)?.IsRevealPhase == true;

    public bool HasRevealedText => _projection?.RevealedTexts is { Count: > 0 };

    /// <summary>只有解锁后才存在的揭秘文字；未解锁时为 null，不是隐藏字符串。</summary>
    public string? RevealedText => _projection?.RevealedTexts is { Count: > 0 } texts
        ? string.Join("\n", texts)
        : null;

    public string ActionAvailability =>
        _projection is null
            ? string.Empty
            : "可用动作：" + string.Join("、", AllowedActionLabels);

    private bool CurrentPhaseIsVote =>
        _package?.Phases.FirstOrDefault(p => p.PhaseId == _projection?.CurrentPhase.PhaseId)?.IsVotePhase == true;

    private bool CurrentPhaseIsReveal =>
        _package?.Phases.FirstOrDefault(p => p.PhaseId == _projection?.CurrentPhase.PhaseId)?.IsRevealPhase == true;

    public RelayCommand StartCommand { get; }

    public RelayCommand AdvanceCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand CompleteCommand { get; }

    public RelayCommand SubmitVoteCommand { get; }

    public RelayCommand ReleaseMaterialsCommand { get; }

    public void Attach(ScriptPackage package, Session session)
    {
        _package = package;
        _projection = PublicSessionProjector.Project(package, session);
        RefreshView(package, _projection);
    }

    /// <summary>
    /// 执行一次命令。模型与自然语言都不能走到这里；只有明确的界面动作会调用。
    /// </summary>
    public async Task ExecuteAsync(
        GameCommandType type,
        IReadOnlyList<string>? materials = null,
        VoteSummary? votes = null)
    {
        if (_package is null || _projection is null)
        {
            Status = "当前没有可操作的一局。";
            return;
        }

        // 需要二次确认的操作，先给出明确提示，避免用户以为按钮坏了
        if (type == GameCommandType.Reveal && !RevealConfirmed)
        {
            Status = "揭秘需要先勾选「确认这是公开揭秘，且原本条件已满足」。";
            return;
        }

        if (type == GameCommandType.SubmitVoteSummary && !VoteConfirmed)
        {
            Status = "请先勾选「确认这是大家核对过的汇总」。";
            return;
        }

        if (type is GameCommandType.Start or GameCommandType.Advance && !GroupReady)
        {
            Status = "请先勾选「大家已口头准备好」。";
            return;
        }

        var command = new GameCommand
        {
            SessionId = _projection.SessionId,
            OperationId = Guid.NewGuid().ToString("N"),
            ExpectedRevision = _projection.Revision,
            Type = type,
            GroupReady = GroupReady,
            // 两类操作各有独立确认：票数汇总用汇总勾选，揭秘用揭秘勾选
            Confirmed = type == GameCommandType.Reveal ? RevealConfirmed : VoteConfirmed,
            MaterialIds = materials ?? [],
            Votes = votes,
        };

        var result = await _dispatcher.DispatchAsync(command, _package, CancellationToken.None);
        switch (result.Outcome)
        {
            case DispatchOutcome.Committed when result.Projection is not null:
                _projection = result.Projection;
                RefreshView(_package, _projection);

                // 只清除「这次用掉了」的勾选，不能连带清掉主持人已经勾好的另一个确认
                if (type is GameCommandType.Start or GameCommandType.Advance)
                {
                    GroupReady = false;
                }

                if (type == GameCommandType.SubmitVoteSummary)
                {
                    VoteConfirmed = false;
                }

                if (type == GameCommandType.Reveal)
                {
                    RevealConfirmed = false;
                }

                Status = $"已提交：{_projection.CurrentPhase.Title}，revision={_projection.Revision}。";

                // 状态已由程序裁决；主持台据此请求模型生成播报措辞
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

    private async Task SubmitVoteAsync()
    {
        if (!VoteConfirmed)
        {
            Status = "请先勾选「确认这是大家核对过的汇总」。";
            return;
        }

        if (!TryParseVotes(VoteInput, out var votes, out var error))
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

        // 阶段章标：按剧本阶段顺序，标出当前与已过
        var currentIndex = package.Phases
            .Select((p, i) => (p.PhaseId, Index: i))
            .FirstOrDefault(t => t.PhaseId == projection.CurrentPhase.PhaseId).Index;
        PhaseRail.Clear();
        for (var i = 0; i < package.Phases.Count; i++)
        {
            PhaseRail.Add(new PhaseRailItem(
                package.Phases[i].Title,
                IsCurrent: i == currentIndex,
                IsPast: i < currentIndex));
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

        AllowedActionLabels.Clear();
        foreach (var action in projection.CurrentPhase.AllowedActions)
        {
            AllowedActionLabels.Add(ActionLabel(action));
        }

        OnPropertyChanged(nameof(PhaseTitle));
        OnPropertyChanged(nameof(SessionStateText));
        OnPropertyChanged(nameof(SessionStateLabel));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(PublicTitle));
        OnPropertyChanged(nameof(HasRevealedText));
        OnPropertyChanged(nameof(RevealedText));
        OnPropertyChanged(nameof(ActionAvailability));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(CanSubmitVote));
        OnPropertyChanged(nameof(CanReveal));
        OnPropertyChanged(nameof(CanReleaseMaterials));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanComplete));
    }

    private static string ActionLabel(PublicAction action) => action switch
    {
        PublicAction.Start => "开始这一局",
        PublicAction.Advance => "下一环节",
        PublicAction.ReleaseMaterials => "发放材料",
        PublicAction.SubmitVoteSummary => "录入选票汇总",
        PublicAction.Reveal => "揭秘",
        PublicAction.Pause => "暂停并保存",
        PublicAction.Resume => "继续",
        PublicAction.Complete => "进入复盘",
        _ => action.ToString(),
    };
}

/// <summary>阶段章标的一项：标题与它在整局中的位置。</summary>
public sealed record PhaseRailItem(string Title, bool IsCurrent, bool IsPast);
