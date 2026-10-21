namespace LocalDm.Core.Tablet;

/// <summary>
/// 平板要的一段公开消息：当前环节、这一段的消息、下一序号。
///
/// 浏览器按序号去重、按序号补发：断线重连时带着上次的序号来要，就只拿新的。
/// </summary>
public sealed record PublicFeedSnapshot
{
    /// <summary>本局 ID。不同局的序号互不相干。</summary>
    public required string SessionId { get; init; }

    /// <summary>当前环节的编号；没有环节时为空。</summary>
    public string PhaseId { get; init; } = string.Empty;

    /// <summary>当前环节的名字，给平板显示。</summary>
    public string PhaseTitle { get; init; } = string.Empty;

    /// <summary>这一段的消息，按序号升序。</summary>
    public required IReadOnlyList<PublicMessage> Messages { get; init; }

    /// <summary>
    /// 下一序号：浏览器下次带着它来要。
    /// 没有新消息时就是它带来的那个序号，不会跳号。
    /// </summary>
    public required long NextSequence { get; init; }
}

/// <summary>
/// 公开消息的来源。
///
/// 桌面那边把这一局的公开消息交给平板；平板只读，不自己存，
/// 也不碰给模型看的对话记忆（那里面有备忘）。
/// </summary>
public interface IPublicFeedSource
{
    /// <summary>取本局在 after 之后的公开消息，以及当前环节。</summary>
    Task<PublicFeedSnapshot> ReadAsync(long after, CancellationToken cancellationToken);
}
