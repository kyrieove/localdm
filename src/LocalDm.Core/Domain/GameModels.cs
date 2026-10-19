namespace LocalDm.Core.Domain;

/// <summary>
/// 一局的状态、命令与事件。
/// 应用数据模型 C04 / C06 / C07 / C08 / C09。
/// </summary>
public sealed record Session
{
    public required string SessionId { get; init; }

    public required string PackageId { get; init; }

    public required string PackageVersion { get; init; }

    public required string ManifestHash { get; init; }

    public required SessionState State { get; init; }

    public required string? CurrentPhaseId { get; init; }

    /// <summary>从 0 开始，每次成功提交恰加 1。</summary>
    public required int Revision { get; init; }

    public required IReadOnlyList<string> ReleasedMaterialIds { get; init; }

    public VoteSummary? VoteSummary { get; init; }

    /// <summary>Pause 时记录；Resume 恢复它，不能覆盖揭秘状态。</summary>
    public SessionState? ResumeState { get; init; }

    public void ValidateBindings(string packageId, string packageVersion, string manifestHash)
    {
        if (!string.Equals(PackageId, packageId, StringComparison.Ordinal)
            || !string.Equals(PackageVersion, packageVersion, StringComparison.Ordinal)
            || !string.Equals(ManifestHash, manifestHash, StringComparison.Ordinal))
        {
            throw new DomainRuleException(
                DomainErrorCode.PACKAGE_MISMATCH,
                "该存档绑定的剧本版本与当前包不一致。");
        }
    }
}

public enum SessionState
{
    Prepared = 0,
    Playing = 1,
    Paused = 2,
    Revealed = 3,
    Completed = 4,
}

public sealed record VoteSummary
{
    public required IReadOnlyList<VoteCount> Counts { get; init; }

    public int Total => Counts.Sum(c => c.Count);

    public VoteSummary Validate(int participantCount)
    {
        if (Counts is null || Counts.Count == 0)
        {
            throw new DomainRuleException(DomainErrorCode.VOTE_INVALID, "票数汇总不能为空。");
        }

        foreach (var count in Counts)
        {
            SourceFile.RequireNonEmpty(count.Option, nameof(count.Option));
            if (count.Count < 0)
            {
                throw new DomainRuleException(DomainErrorCode.VOTE_INVALID, "票数必须为非负整数。");
            }
        }

        if (Total != participantCount)
        {
            throw new DomainRuleException(
                DomainErrorCode.VOTE_INVALID,
                $"票数汇总总数必须等于参与人数 {participantCount}。");
        }

        var duplicated = Counts
            .GroupBy(c => c.Option, StringComparer.Ordinal)
            .Any(g => g.Count() > 1);
        if (duplicated)
        {
            throw new DomainRuleException(DomainErrorCode.VOTE_INVALID, "票数选项不能重复。");
        }

        return this;
    }
}

public sealed record VoteCount
{
    public required string Option { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// 命令载荷只含该命令需要的明确数据：groupReady、confirmed、票数或材料 ID。
/// 不含自由代码，也不含模型可写的状态。
/// </summary>
public sealed record GameCommand
{
    public required string SessionId { get; init; }

    public required string OperationId { get; init; }

    public required int ExpectedRevision { get; init; }

    public required GameCommandType Type { get; init; }

    /// <summary>确认大家已口头准备好（Start / Advance）。</summary>
    public bool GroupReady { get; init; }

    /// <summary>二次确认（SubmitVoteSummary / Reveal）。</summary>
    public bool Confirmed { get; init; }

    public IReadOnlyList<string> MaterialIds { get; init; } = [];

    public VoteSummary? Votes { get; init; }

    public void Validate()
    {
        SourceFile.RequireNonEmpty(SessionId, nameof(SessionId));
        SourceFile.RequireNonEmpty(OperationId, nameof(OperationId));
        if (ExpectedRevision < 0)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "expectedRevision 不能为负。");
        }
    }

    /// <summary>
    /// 相同 operationId 的内容比较：只比较影响结果的字段，用于判断是重放还是冲突。
    /// </summary>
    public string PayloadFingerprint()
    {
        var materials = string.Join(',', MaterialIds.OrderBy(m => m, StringComparer.Ordinal));
        var votes = Votes is null
            ? "-"
            : string.Join(',', Votes.Counts
                .OrderBy(c => c.Option, StringComparer.Ordinal)
                .Select(c => $"{c.Option}={c.Count}"));
        return string.Join(
            '|',
            Type,
            ExpectedRevision,
            GroupReady ? '1' : '0',
            Confirmed ? '1' : '0',
            materials,
            votes);
    }
}

public enum GameCommandType
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

public sealed record GameEvent
{
    public required string EventId { get; init; }

    public required string SessionId { get; init; }

    public required string OperationId { get; init; }

    public required int Revision { get; init; }

    public required GameCommandType Type { get; init; }

    /// <summary>脱敏载荷：不拷贝完整真相，只记录阶段、编号与计数。</summary>
    public required string SafePayload { get; init; }

    public required long CommittedAtUnixMs { get; init; }
}

/// <summary>
/// 一次已提交操作的凭证。同一 session 内 operationId 唯一。
/// </summary>
public sealed record OperationReceipt
{
    public required string SessionId { get; init; }

    public required string OperationId { get; init; }

    public required string PayloadFingerprint { get; init; }

    public required int Revision { get; init; }

    public required GameCommandType Type { get; init; }

    public required long CommittedAtUnixMs { get; init; }
}
