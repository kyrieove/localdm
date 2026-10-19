namespace LocalDm.Core.Domain;

/// <summary>
/// 主持台相关类型：请求 ID、生成 ID 与播音状态。
/// 应用数据模型 C10 / C11。
/// </summary>
public sealed record SpeechItem
{
    public required string SpeechId { get; init; }

    public required string RequestId { get; init; }

    /// <summary>已通过公开政策过滤、可直接朗读与显示的文字。</summary>
    public required string SafeText { get; init; }

    public required string GenerationId { get; init; }

    public void Validate()
    {
        SourceFile.RequireNonEmpty(SpeechId, nameof(SpeechId));
        SourceFile.RequireNonEmpty(RequestId, nameof(RequestId));
        SourceFile.RequireNonEmpty(GenerationId, nameof(GenerationId));
        SourceFile.RequireNonEmpty(SafeText, nameof(SafeText));
    }
}

/// <summary>
/// 播音状态。不存在录音状态：应用不采音。
/// </summary>
public enum SpeechState
{
    Idle = 0,
    GeneratingText = 1,
    Speaking = 2,
    Cancelled = 3,
    Error = 4,
}

/// <summary>
/// 一次主持请求的上下文。requestId 失效后，任何晚到输出都必须丢弃。
/// </summary>
public sealed record SpeechRequestContext
{
    public required string RequestId { get; init; }

    public required string GenerationId { get; init; }

    /// <summary>请求发出时的会话 revision；用于判断回复是否针对过期状态。</summary>
    public required int SessionRevision { get; init; }

    public static SpeechRequestContext Create(int sessionRevision) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        GenerationId = Guid.NewGuid().ToString("N"),
        SessionRevision = sessionRevision,
    };
}
