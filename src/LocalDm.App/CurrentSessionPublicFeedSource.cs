using LocalDm.App.ViewModels;
using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;

namespace LocalDm.App;

/// <summary>
/// 桌面这一局的公开消息来源：给平板的那一段。
///
/// 消息从存档里按序号取（<see cref="IPublicMessageFeed"/>），
/// 当前环节取主持台现在的投影。平板只读这一份，不碰模型记忆。
/// </summary>
public sealed class CurrentSessionPublicFeedSource : IPublicFeedSource
{
    /// <summary>一次最多给多少条。平板按序号补发，不需要一次拿全。</summary>
    public const int PageSize = 200;

    private readonly IPublicMessageFeed _feed;
    private readonly Func<SessionViewModel> _session;

    public CurrentSessionPublicFeedSource(IPublicMessageFeed feed, Func<SessionViewModel> session)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task<PublicFeedSnapshot> ReadAsync(long after, CancellationToken cancellationToken)
    {
        var view = _session();
        var sessionId = view.Session?.SessionId ?? string.Empty;
        if (string.IsNullOrEmpty(sessionId))
        {
            // 还没装载任何一局：没有「本局」可给。
            return new PublicFeedSnapshot
            {
                SessionId = string.Empty,
                Messages = [],
                NextSequence = after,
            };
        }

        var messages = await _feed.ReadPublicMessagesAsync(sessionId, after, PageSize, cancellationToken);

        // 下一序号就是这一段的最后一个序号；没有新消息时保持原样，绝不跳号。
        var next = messages.Count > 0 ? messages[^1].Sequence : after;

        return new PublicFeedSnapshot
        {
            SessionId = sessionId,
            PhaseId = view.Session?.CurrentPhaseId ?? string.Empty,
            PhaseTitle = view.PhaseTitle,
            Messages = messages,
            NextSequence = next,
        };
    }
}
