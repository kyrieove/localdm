using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 没有公开消息可给时的来源：返回空的一段。
///
/// 独立运行的平板服务看不到桌面那一局的对话存档，所以不编造内容 ——
/// 网页上就说「还没有公开消息」，不要拿合成对话冒充真实本局。
/// 服务接进 WPF 之后换成真正的存档来源。
/// </summary>
public sealed class EmptyPublicFeedSource : IPublicFeedSource
{
    public Task<PublicFeedSnapshot> ReadAsync(long after, CancellationToken cancellationToken) =>
        Task.FromResult(new PublicFeedSnapshot
        {
            SessionId = string.Empty,
            Messages = [],
            NextSequence = after,
        });
}
