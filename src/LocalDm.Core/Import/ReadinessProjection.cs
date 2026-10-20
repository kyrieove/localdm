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
