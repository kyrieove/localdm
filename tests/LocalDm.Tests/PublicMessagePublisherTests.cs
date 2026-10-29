using LocalDm.App.ViewModels;
using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;

namespace LocalDm.Tests;

/// <summary>重新开局时还在写的上一局消息：它随后失败，不能把新局标成同步异常。</summary>
public sealed class PublicMessagePublisherTests
{
    [Fact]
    public async Task Old_write_failing_after_restart_does_not_stop_the_new_game()
    {
        var feed = new GatedFeed();
        var publisher = new PublicMessagePublisher(feed);
        var committed = new TaskCompletionSource<PublicMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Committed += (_, message) => committed.TrySetResult(message);

        publisher.Publish(Message("old"));
        await feed.FirstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        publisher.Discard();
        publisher.Publish(Message("new"));
        feed.FailFirstWrite.SetResult();

        var stored = await committed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("new", stored.MessageId);
        Assert.False(publisher.HasFailure);
        Assert.Equal(0, publisher.PendingCount);
    }

    private static PublicMessage Message(string id) => new()
    {
        SessionId = "s1",
        MessageId = id,
        Author = PublicMessageAuthor.Player,
        Text = "合成发言",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Kind = PublicMessageKind.PlayerSpeech,
    };

    /// <summary>第一次写入挂起，放行时失败；之后的写入都成功。</summary>
    private sealed class GatedFeed : IPublicMessageFeed
    {
        private int _calls;

        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FailFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PublicMessage> AppendPublicMessageAsync(PublicMessage message, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstWriteStarted.SetResult();
                await FailFirstWrite.Task;
                throw new InvalidOperationException("合成写入失败");
            }

            return message with { Sequence = 1 };
        }

        public Task<IReadOnlyList<PublicMessage>> ReadPublicMessagesAsync(
            string sessionId,
            long after,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PublicMessage>>([]);
    }
}
