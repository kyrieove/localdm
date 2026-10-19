namespace LocalDm.Core.Domain;

/// <summary>
/// 来源文件：原始剧本材料的文件级指纹与页数记录。
/// 应用数据模型 C01 / C02：ID 非空，每个文件都必须记录 hash 与页数。
/// </summary>
public sealed record SourceFile
{
    public required string FileId { get; init; }

    /// <summary>原始引用，仅私有存储可见；不进入公共投影、日志或发布包。</summary>
    public required string PrivatePath { get; init; }

    /// <summary>来源文件内容 hash（SHA-256，十六进制小写）。</summary>
    public required string ContentHash { get; init; }

    public required int PageCount { get; init; }

    /// <summary>页序来源：PDF 按实际页序，图片由 manifest 明确指定。</summary>
    public required SourceOrder Origin { get; init; }

    public required string ToolIdentity { get; init; }

    public required FileParseState ParseState { get; init; }

    public void Validate()
    {
        RequireNonEmpty(FileId, nameof(FileId));
        RequireNonEmpty(ContentHash, nameof(ContentHash));
        RequireNonEmpty(PrivatePath, nameof(PrivatePath));
        if (PageCount <= 0)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "文件页数必须大于 0。");
        }

        if (!IsSha256Hex(ContentHash))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "文件 hash 必须是 64 位十六进制 SHA-256。");
        }
    }

    internal static void RequireNonEmpty(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, $"{field} 不能为空。");
        }
    }

    internal static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
}

public enum SourceOrder
{
    /// <summary>PDF：按页面自然顺序。</summary>
    PageOrder = 0,

    /// <summary>图片：顺序完全由 manifest 明确指定，不按文件名字典序猜剧情。</summary>
    ManifestOrder = 1,
}

public enum FileParseState
{
    Pending = 0,
    Complete = 1,
    Incomplete = 2,
    Failed = 3,
}

/// <summary>
/// 页块来源：把规范文本回指到某个来源文件的实际页面与块/字符定位符。
/// </summary>
public sealed record SourceSpan
{
    public required string SpanId { get; init; }

    public required string FileId { get; init; }

    public required int PageNumber { get; init; }

    public required int BlockNumber { get; init; }

    /// <summary>工具原始定位符，形如 doc:ID/tier:TIER/page:N/block:N/char:OFFSET。</summary>
    public required string ToolLocator { get; init; }

    public required string TextHash { get; init; }

    public required string OriginalText { get; init; }

    public void Validate(IReadOnlyDictionary<string, SourceFile> files)
    {
        SourceFile.RequireNonEmpty(SpanId, nameof(SpanId));
        SourceFile.RequireNonEmpty(FileId, nameof(FileId));
        SourceFile.RequireNonEmpty(ToolLocator, nameof(ToolLocator));
        if (PageNumber < 1)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "页号必须从 1 开始。");
        }

        if (BlockNumber < 0)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "块号不能为负。");
        }

        if (!files.TryGetValue(FileId, out var file))
        {
            throw new DomainRuleException(DomainErrorCode.SOURCE_MISMATCH, "SourceSpan 引用了不存在的文件。");
        }

        if (PageNumber > file.PageCount)
        {
            throw new DomainRuleException(DomainErrorCode.SOURCE_MISMATCH, "SourceSpan 引用了不存在的页。");
        }

        SourceFile.RequireNonEmpty(OriginalText, nameof(OriginalText));
        if (!SourceFile.IsSha256Hex(TextHash))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "原文 hash 必须是 64 位十六进制 SHA-256。");
        }
    }
}

/// <summary>
/// 规范文本段。可见性由程序根据阶段释放决定，不接受模型决定。
/// </summary>
public sealed record CanonicalSegment
{
    public required string SegmentId { get; init; }

    public required string OriginalText { get; init; }

    public required IReadOnlyList<string> SourceSpanIds { get; init; }

    public required SegmentVisibility Visibility { get; init; }

    /// <summary>该段允许公开的阶段；Truth 段为空表示只在揭秘后可见。</summary>
    public string? ReleasePhaseId { get; init; }

    public void Validate()
    {
        SourceFile.RequireNonEmpty(SegmentId, nameof(SegmentId));
        SourceFile.RequireNonEmpty(OriginalText, nameof(OriginalText));
        if (SourceSpanIds is null || SourceSpanIds.Count == 0)
        {
            throw new DomainRuleException(
                DomainErrorCode.MISSING_SOURCE,
                "关键文本段必须有非空 sourceSpanIds。");
        }

        if (SourceSpanIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "sourceSpanIds 不能含空值。");
        }
    }
}

public enum SegmentVisibility
{
    Public = 0,
    Private = 1,
    Truth = 2,
}
