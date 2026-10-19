namespace LocalDm.Core.Domain;

/// <summary>
/// 剧本准备相关类型。
/// 应用数据模型 C02 / C03 / C05 / C12：工具层 Complete 与应用层 Ready 必须分开。
/// </summary>
public sealed record ImportJob
{
    public required string JobId { get; init; }

    /// <summary>源清单 hash：绑定所有输入文件的 fileId/hash/pageCount 与顺序。</summary>
    public required string SourceManifestHash { get; init; }

    public required string ToolIdentity { get; init; }

    /// <summary>隐私预检结论；未知边界不得进入 Parsing。</summary>
    public required PrivacyDecision PrivacyDecision { get; init; }

    public required ImportState State { get; init; }

    /// <summary>已处理完成的页（fileId + page），用于覆盖核对。</summary>
    public required IReadOnlyList<ProcessedPage> ProcessedPages { get; init; }

    /// <summary>工具返回的下一请求；不为空表示提取尚未完成。</summary>
    public ContinuationRequest? Continuation { get; init; }

    /// <summary>脱敏错误代码，不含原始 stderr 全文。</summary>
    public required IReadOnlyList<string> ErrorCodes { get; init; }
}

public sealed record ProcessedPage
{
    public required string FileId { get; init; }

    public required int PageNumber { get; init; }
}

public enum ImportState
{
    Created = 0,

    /// <summary>仅在确认有效工具规则与本地/远程边界后成立。</summary>
    PrivacyChecked = 1,

    Parsing = 2,

    /// <summary>提取已完整完成（工具层 Complete），但应用层 Ready 另需业务校验。</summary>
    Validating = 3,

    Ready = 4,
    NeedsReview = 5,
    Failed = 6,
    Cancelled = 7,
}

public enum PrivacyDecision
{
    /// <summary>未检查：不得调用真实材料。</summary>
    Unknown = 0,

    /// <summary>已确认只做本机处理且规则与遥测状态明确。</summary>
    LocalOnly = 1,

    /// <summary>需要远程处理且已有绑定授权。</summary>
    RemoteAuthorized = 2,

    /// <summary>边界未知或未获授权：禁止调用。</summary>
    Blocked = 3,
}

public sealed record ContinuationRequest
{
    public required ContinuationKind Kind { get; init; }

    /// <summary>parse 续读的页范围，例如 "1-10"。</summary>
    public string? PageRange { get; init; }

    /// <summary>parse 续读的 after 偏移。</summary>
    public string? After { get; init; }

    /// <summary>read 续读的定位符 doc:ID/tier:TIER/page:N/block:N/char:OFFSET。</summary>
    public string? Locator { get; init; }
}

public enum ContinuationKind
{
    Parse = 0,
    Read = 1,
}

/// <summary>
/// 脱敏准备报告：只返回公共标题、人数、文件/页覆盖、脱敏问题代码与 canStart。
/// </summary>
public sealed record ReadinessReport
{
    public required string PublicTitle { get; init; }

    public required int PlayerCount { get; init; }

    public required IReadOnlyList<FilePageCoverage> FilePageCoverage { get; init; }

    public required IReadOnlyList<string> IssueCategories { get; init; }

    /// <summary>
    /// 只有 Ready 且没有阻塞问题时为 true。NeedsReview / Failed / Cancelled 一律不可以开局。
    /// </summary>
    public required bool CanStart { get; init; }

    public required ReadinessStatus Status { get; init; }
}

public sealed record FilePageCoverage
{
    public required string FileId { get; init; }

    public required int ExpectedPages { get; init; }

    public required int ProcessedPages { get; init; }
}

public enum ReadinessStatus
{
    Ready = 0,
    NeedsReview = 1,
    Failed = 2,
    Cancelled = 3,
}

/// <summary>
/// 工具完整提取后的应用包：formatVersion=1。
/// </summary>
public sealed record PreparedEnvelope
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string ToolIdentity { get; init; }

    public required IReadOnlyList<ExtractionBatch> Batches { get; init; }

    public required EnvelopeCompletion Completion { get; init; }

    public required IReadOnlyList<SourceFile> SourceFiles { get; init; }

    public required IReadOnlyList<SourceSpan> Spans { get; init; }
}

public enum EnvelopeCompletion
{
    /// <summary>仍有未处理的输入或未完成的续读请求。</summary>
    Incomplete = 0,

    /// <summary>所有输入完整处理、无待继续请求且覆盖核对通过。</summary>
    Complete = 1,
}

public sealed record ExtractionBatch
{
    public required string Locator { get; init; }

    public required string Text { get; init; }

    /// <summary>字符偏移；工具未给数字偏移时为 0，并由 StartLocator 保留原值。</summary>
    public required int StartOffset { get; init; }

    public required int EndOffset { get; init; }

    public required string PageRange { get; init; }

    /// <summary>工具原文的 start 定位符（doc:ID/tier:TIER/page:N/block:N/char:OFFSET），可能为空。</summary>
    public string? StartLocator { get; init; }

    /// <summary>工具原文的 end 定位符，可能为空。</summary>
    public string? EndLocator { get; init; }
}
