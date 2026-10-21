namespace LocalDm.Core.Tablet;

/// <summary>
/// 公开消息的作者：这条桌面上公开的话是谁说的。
/// </summary>
public enum PublicMessageAuthor
{
    /// <summary>玩家说的话。</summary>
    Player = 0,

    /// <summary>主持人的公开回复。</summary>
    Host = 1,

    /// <summary>程序写入的公开流程事件（环节推进、发放材料等）。</summary>
    System = 2,
}

/// <summary>
/// 公开消息的类型。平板据此决定怎么显示，不去猜正文。
/// </summary>
public enum PublicMessageKind
{
    /// <summary>玩家发言。</summary>
    PlayerSpeech = 0,

    /// <summary>主持回复。</summary>
    HostReply = 1,

    /// <summary>公开流程事件。</summary>
    FlowEvent = 2,
}

/// <summary>
/// 一条公开消息：桌面上当着所有人说的话。
///
/// 不可变：复制成不可变对象之后才能交给网页线程，界面改不了已经发出去的消息。
///
/// 和模型记忆分开存：这里没有备忘、没有角色本、没有真相，
/// 也不会从 AI 备忘里重建 —— 备忘是给模型看的，不是给桌上看的。
/// </summary>
public sealed record PublicMessage
{
    /// <summary>本局 ID。不同局的序号与消息互不相干。</summary>
    public required string SessionId { get; init; }

    /// <summary>消息 ID。同一局内重复写入只存一次，重试不会多出一条。</summary>
    public required string MessageId { get; init; }

    /// <summary>作者。</summary>
    public required PublicMessageAuthor Author { get; init; }

    /// <summary>正文：桌上公开说过的话。</summary>
    public required string Text { get; init; }

    /// <summary>产生时间。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>类型。</summary>
    public required PublicMessageKind Kind { get; init; }

    /// <summary>
    /// 本局内的单调序号，从 1 开始。平板按它去重、按它补发。
    /// 0 表示还没入库：序号由存储层分配，调用方不用自己算。
    /// </summary>
    public long Sequence { get; init; }
}
