using LocalDm.Core.Domain;

namespace LocalDm.Core.Import;

/// <summary>
/// 应用层业务校验（与工具层 Complete 分离）。
/// 只有 Ready 可以开局；NeedsReview、Failed、Cancelled 不可以开局。
/// 关键缺漏、矛盾、来源缺失或未完成续读一律阻止开局，且不补写事实、不猜凶手。
/// </summary>
public static class ScriptValidator
{
    /// <summary>
    /// 对一次提取结果做应用层校验并给出脱敏报告。
    /// </summary>
    public static ReadinessReport Validate(
        string publicTitle,
        int playerCount,
        IReadOnlyList<SourceFile> files,
        IReadOnlyList<SourceSpan> spans,
        IReadOnlyList<CanonicalSegment> segments,
        IReadOnlyList<KeyFact> keyFacts,
        bool toolComplete,
        bool continuationPending)
    {
        var issues = new List<string>();
        var coverage = new List<FilePageCoverage>();

        if (playerCount is not (4 or 5 or 6))
        {
            issues.Add("PLAYER_COUNT_UNSUPPORTED");
        }

        foreach (var file in files)
        {
            var processed = spans
                .Where(s => s.FileId == file.FileId)
                .Select(s => s.PageNumber)
                .Distinct()
                .Count();
            coverage.Add(new FilePageCoverage
            {
                FileId = file.FileId,
                ExpectedPages = file.PageCount,
                ProcessedPages = processed,
            });

            if (processed != file.PageCount)
            {
                issues.Add("PAGE_COVERAGE_INCOMPLETE");
            }

            if (file.ParseState is FileParseState.Failed or FileParseState.Incomplete)
            {
                issues.Add("SOURCE_FILE_INCOMPLETE");
            }
        }

        if (!toolComplete)
        {
            issues.Add("TOOL_NOT_COMPLETE");
        }

        if (continuationPending)
        {
            issues.Add("CONTINUATION_PENDING");
        }

        // 每个规范段必须能回指存在的页块
        var spanIds = spans.Select(s => s.SpanId).ToHashSet(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (segment.SourceSpanIds.Count == 0
                || segment.SourceSpanIds.Any(id => !spanIds.Contains(id)))
            {
                issues.Add("MISSING_SOURCE");
                break;
            }
        }

        // 关键事实必须全部有来源；未知事实保留 NeedsReview
        foreach (var fact in keyFacts)
        {
            if (fact.SourceSpanIds.Count == 0)
            {
                issues.Add(fact.IsResolved ? "MISSING_SOURCE" : "KEYFACT_NEEDS_REVIEW");
            }
        }

        var distinct = issues.Distinct(StringComparer.Ordinal).ToArray();
        var blocking = distinct.Length > 0;
        return new ReadinessReport
        {
            PublicTitle = publicTitle,
            PlayerCount = playerCount,
            FilePageCoverage = coverage,
            IssueCategories = distinct,
            CanStart = !blocking,
            Status = blocking ? ReadinessStatus.NeedsReview : ReadinessStatus.Ready,
        };
    }

    /// <summary>把模型/工具边界输出清洗成规范段；可见性由程序决定，不接受模型声明。</summary>
    public static CanonicalSegment CreateSegment(
        string segmentId,
        string originalText,
        SegmentVisibility visibility,
        IEnumerable<string> sourceSpanIds,
        string? releasePhaseId = null)
    {
        var segment = new CanonicalSegment
        {
            SegmentId = segmentId,
            OriginalText = originalText,
            SourceSpanIds = [.. sourceSpanIds],
            Visibility = visibility,
            ReleasePhaseId = releasePhaseId,
        };
        segment.Validate();
        return segment;
    }
}

/// <summary>
/// 关键事实：必须有非空来源。IsResolved=false 表示尚未确认，保留 NeedsReview。
/// </summary>
public sealed record KeyFact
{
    public required string FactId { get; init; }

    public required string Text { get; init; }

    public required IReadOnlyList<string> SourceSpanIds { get; init; }

    public required bool IsResolved { get; init; }
}
