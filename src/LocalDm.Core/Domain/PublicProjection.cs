using System.Collections.ObjectModel;

namespace LocalDm.Core.Domain;

/// <summary>
/// 公开投影：公共屏幕、TTS、日志与普通问答只能取得这里的信息。
/// 应用数据模型 C05：不含 truth / privateStory / 未释放线索 / 未解锁真相。
/// 未解锁时不存在的字段是「没有这个属性」，而不是「值为隐藏字符串」。
/// </summary>
public sealed record PublicPhaseView
{
    public required string PhaseId { get; init; }

    public required string Title { get; init; }

    /// <summary>只包含当前阶段允许公开的段落 ID；已经过程序筛选。</summary>
    public required IReadOnlyList<string> PublicSegmentIds { get; init; }

    /// <summary>当前阶段允许主持执行的动作。</summary>
    public required IReadOnlyList<PublicAction> AllowedActions { get; init; }

    /// <summary>当前流程提示（一句话）。界面「当前流程提示」直接用这一句。</summary>
    public string? Hint { get; init; }

    /// <summary>推进动作名，例如「进入第二轮搜证」。为空表示没有下一环节。</summary>
    public string? AdvanceLabel { get; init; }

    /// <summary>揭秘动作名，例如「公开结局」。</summary>
    public string? RevealLabel { get; init; }

    /// <summary>本环节材料是否必须发完才能推进。</summary>
    public bool RequiresAllMaterials { get; init; }

    /// <summary>本环节是否提供「请主持人开场」动作（由流程声明给出）。</summary>
    public bool CanOpenSession { get; init; }

    /// <summary>本环节在整局里的序号（从 1 开始）。</summary>
    public required int Index { get; init; }

    /// <summary>整局的环节总数。</summary>
    public required int Count { get; init; }
}

/// <summary>进度轨道的一项：只含公开的阶段标题与位置，供左侧「本局进度」渲染。</summary>
public sealed record PublicPhaseTrackItem
{
    public required string PhaseId { get; init; }

    public required string Title { get; init; }
}

public enum PublicAction
{
    Start = 0,
    Advance = 1,
    ReleaseMaterials = 2,
    SubmitVoteSummary = 3,
    Reveal = 4,
    Pause = 5,
    Resume = 6,
    Complete = 7,
}

/// <summary>
/// 公共投影中的票数汇总：只有参与人数与各选项票数，不含玩家身份。
/// </summary>
public sealed record PublicVoteSummaryView
{
    public required IReadOnlyList<PublicVoteCount> Counts { get; init; }

    public required int TotalVotes { get; init; }
}

public sealed record PublicVoteCount
{
    public required string Option { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// 主持窗口唯一的数据源。准备工具、私有包加载器不能直接作为 UI data context。
/// </summary>
public sealed record PublicSessionProjection
{
    public required string SessionId { get; init; }

    public required string PublicTitle { get; init; }

    public required int PlayerCount { get; init; }

    public required string State { get; init; }

    public required int Revision { get; init; }

    public required PublicPhaseView CurrentPhase { get; init; }

    /// <summary>整局的环节轨道（只有标题与顺序），用于左侧进度列表。</summary>
    public required IReadOnlyList<PublicPhaseTrackItem> PhaseTrack { get; init; }

    /// <summary>已释放的材料编号，只有编号不含内容。</summary>
    public required IReadOnlyList<string> ReleasedMaterialIds { get; init; }

    public required PublicVoteSummaryView? VoteSummary { get; init; }

    /// <summary>
    /// 揭秘内容：仅在 Revealed / Completed 状态且原本条件满足后由投影填充，否则为 null。
    /// </summary>
    public required IReadOnlyList<string>? RevealedTexts { get; init; }

    private static readonly IReadOnlyList<string> Empty = new ReadOnlyCollection<string>([]);

    /// <summary>
    /// 防御性校验：投影里不能出现私有/真相段落 ID，避免上游拼接错误把秘密带进公共界面。
    /// </summary>
    public PublicSessionProjection EnsureSecretFree(IReadOnlyCollection<string> secretSegmentIds)
    {
        foreach (var id in CurrentPhase.PublicSegmentIds)
        {
            if (secretSegmentIds.Contains(id))
            {
                throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "公共投影包含了非公开段落。");
            }
        }

        if (VoteSummary is not null && VoteSummary.TotalVotes != VoteSummary.Counts.Sum(c => c.Count))
        {
            throw new DomainRuleException(DomainErrorCode.VOTE_INVALID, "票数汇总总数不一致。");
        }

        return this;
    }

    public static IReadOnlyList<string> NoIds => Empty;
}
