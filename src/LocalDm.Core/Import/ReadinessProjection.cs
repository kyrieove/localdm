using LocalDm.Core.Domain;

namespace LocalDm.Core.Import;

/// <summary>
/// 脱敏准备投影：只返回公共标题、人数、文件/页覆盖、脱敏问题代码及 canStart。
/// 不返回 truth、murderer、privateStory、原始 secret span、原始路径或未清洗的工具 stderr。
/// </summary>
public sealed record PublicReadinessView
{
    public required string PublicTitle { get; init; }

    public required int PlayerCount { get; init; }

    public required IReadOnlyList<PublicCoverageRow> Coverage { get; init; }

    public required IReadOnlyList<string> IssueCodes { get; init; }

    public required bool CanStart { get; init; }

    public required string StatusText { get; init; }

    public static PublicReadinessView From(ReadinessReport report) => new()
    {
        PublicTitle = report.PublicTitle,
        PlayerCount = report.PlayerCount,
        Coverage = report.FilePageCoverage
            .Select(c => new PublicCoverageRow(c.FileId, c.ProcessedPages, c.ExpectedPages))
            .ToArray(),
        IssueCodes = report.IssueCategories,
        CanStart = report.CanStart,
        StatusText = report.Status switch
        {
            ReadinessStatus.Ready => "可以开局。",
            ReadinessStatus.NeedsReview => "需要人工复核，暂时不能开局。",
            ReadinessStatus.Failed => "准备失败，不能开局。",
            _ => "准备已取消，不能开局。",
        },
    };
}

public sealed record PublicCoverageRow(string FileId, int ProcessedPages, int ExpectedPages);

/// <summary>
/// 公共投影构造：从冻结包生成公共信息，绝不包含私有/真相段内容。
/// </summary>
public static class ReadinessProjection
{
    public static PublicSessionProjection ToPublicSession(
        ScriptPackage package,
        Session session)
    {
        var phase = package.Phases.FirstOrDefault(p => p.PhaseId == session.CurrentPhaseId)
                    ?? package.Phases.FirstOrDefault()
                    ?? throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "剧本包没有可用阶段。");

        var unlocked = session.State is SessionState.Revealed or SessionState.Completed;
        var revealedTexts = unlocked
            ? package.Truth.RevealedSegmentIds
                .Select(id => package.Segments.FirstOrDefault(s => s.SegmentId == id)?.OriginalText)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Cast<string>()
                .ToArray()
            : null;

        return new PublicSessionProjection
        {
            SessionId = session.SessionId,
            PublicTitle = package.Title,
            PlayerCount = package.PlayerCount,
            State = session.State.ToString(),
            Revision = session.Revision,
            CurrentPhase = new PublicPhaseView
            {
                PhaseId = phase.PhaseId,
                Title = phase.Title,
                PublicSegmentIds = phase.PublicSegmentIds,
                AllowedActions = phase.AllowedActions,
            },
            ReleasedMaterialIds = session.ReleasedMaterialIds,
            VoteSummary = session.VoteSummary is null
                ? null
                : new PublicVoteSummaryView
                {
                    Counts = session.VoteSummary.Counts
                        .Select(c => new PublicVoteCount { Option = c.Option, Count = c.Count })
                        .ToArray(),
                    TotalVotes = session.VoteSummary.Total,
                },
            RevealedTexts = revealedTexts,
        };
    }
}
