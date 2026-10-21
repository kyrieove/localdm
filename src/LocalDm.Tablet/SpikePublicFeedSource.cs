using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 探针用的公开消息来源：合成内容，用来把 HTTP 契约先跑通。
///
/// 真机上这一条由桌面那边换成存档里的真实公开消息（同一个接口）。
/// </summary>
public sealed class SpikePublicFeedSource : IPublicFeedSource
{
    private readonly IReadOnlyList<PublicMessage> _messages;

    public SpikePublicFeedSource()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 20, 0, 0, TimeSpan.FromHours(3));
        _messages = new[]
        {
            new PublicMessage
            {
                SessionId = "session-spike",
                MessageId = "spike-1",
                Author = PublicMessageAuthor.Player,
                Text = "我们去检查一下码头。",
                CreatedAt = createdAt,
                Kind = PublicMessageKind.PlayerSpeech,
                Sequence = 1,
            },
            new PublicMessage
            {
                SessionId = "session-spike",
                MessageId = "spike-2",
                Author = PublicMessageAuthor.Host,
                Text = "好的，翻牌的时候把线索卡摆开。",
                CreatedAt = createdAt.AddMinutes(1),
                Kind = PublicMessageKind.HostReply,
                Sequence = 2,
            },
            new PublicMessage
            {
                SessionId = "session-spike",
                MessageId = "spike-3",
                Author = PublicMessageAuthor.System,
                Text = "进入搜证",
                CreatedAt = createdAt.AddMinutes(2),
                Kind = PublicMessageKind.FlowEvent,
                Sequence = 3,
            },
        };
    }

    public Task<PublicFeedSnapshot> ReadAsync(long after, CancellationToken cancellationToken)
    {
        var messages = _messages.Where(m => m.Sequence > after).OrderBy(m => m.Sequence).ToArray();
        var next = messages.Length > 0 ? messages[^1].Sequence : after;
        return Task.FromResult(new PublicFeedSnapshot
        {
            SessionId = "session-spike",
            PhaseId = "phase-search",
            PhaseTitle = "搜证",
            Messages = messages,
            NextSequence = next,
        });
    }
}
