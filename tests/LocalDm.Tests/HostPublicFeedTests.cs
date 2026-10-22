using System.IO;
using LocalDm.App.Demo;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.Tests;

/// <summary>
/// 桌面这一侧的公开消息出口：桌上每落一条消息，就往存档写一条公开消息。
///
/// 写失败要显「同步异常」，不能对外说「已同步」；
/// 重试之后序号仍然连续（跳号意味着平板漏消息）。
/// </summary>
public sealed class HostPublicFeedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-public-outlet-" + Guid.NewGuid().ToString("N"));

    public HostPublicFeedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_chat_turn_lands_in_the_public_feed()
    {
        using var store = await NewStoreAsync();
        var host = NewHost(store, out var gateway);

        await host.LoadDemoAsync();
        host.InputText = "我们去检查一下码头。";
        await host.SendAsync();

        var feed = await WaitForFeedAsync(store, "session-demo", 2);
        Assert.Equal(new long[] { 1, 2 }, feed.Select(m => m.Sequence));

        // 玩家发言与主持回复都进去了
        Assert.Equal(PublicMessageKind.PlayerSpeech, feed[0].Kind);
        Assert.Equal(PublicMessageAuthor.Player, feed[0].Author);
        Assert.Equal("我们去检查一下码头。", feed[0].Text);

        Assert.Equal(PublicMessageKind.HostReply, feed[1].Kind);
        Assert.Equal(PublicMessageAuthor.Host, feed[1].Author);
        Assert.Equal(TestGateway.Reply, feed[1].Text);

        // 消息 ID 就是聊天那条的 ID：同一句话重复写只存一次
        Assert.Equal(host.Messages[host.Messages.Count - 2].Id, feed[0].MessageId);
        Assert.Equal(1, gateway.CompletionCount);
    }

    [Fact]
    public async Task Local_hints_do_not_enter_the_public_feed()
    {
        using var store = await NewStoreAsync();
        var host = NewHost(store, out _);

        await host.LoadDemoAsync();

        // 装载时的本机提示（「演示剧本已装载…」）不是桌上说的话，不该同步出去
        var feed = await store.ReadPublicMessagesAsync("session-demo", 0, 100, TestContext.Current.CancellationToken);
        Assert.Empty(feed);

        host.InputText = "那我们开始吧。";
        await host.SendAsync();

        var after = await WaitForFeedAsync(store, "session-demo", 2);
        Assert.DoesNotContain(after, m => m.Text.Contains("演示剧本已装载", StringComparison.Ordinal));
        Assert.DoesNotContain(after, m => m.Text.Contains("合成数据", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_write_shows_a_sync_error_and_retry_keeps_the_sequence_continuous()
    {
        var path = Path.Combine(_root, "sessions.db");
        using var store = new SessionStore(path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await BreakPublicMessageTableAsync(path);

        var host = NewHost(store, out _);
        await host.LoadDemoAsync();

        host.InputText = "记住这句话。";
        await host.SendAsync();

        // 写不进去要显出来：不能吞掉，也不能说「已同步」
        await WaitUntilAsync(() => host.HasPublicSyncError);
        Assert.Contains("同步异常", host.PublicSyncErrorText, StringComparison.Ordinal);
        Assert.Contains("同步异常", host.Status, StringComparison.Ordinal);
        Assert.True(host.RetryPublicSyncCommand.CanExecute(null), "同步异常时必须能重试。");

        // 停在断掉的状态时，一条都没有写进去
        var broken = await store.ReadPublicMessagesAsync("session-demo", 0, 100, TestContext.Current.CancellationToken);
        Assert.Empty(broken);

        // 修好存储再重试：两条都补上，序号连续，没有缺口
        await RepairPublicMessageTableAsync(path);
        host.RetryPublicSyncCommand.Execute(null);

        var feed = await WaitForFeedAsync(store, "session-demo", 2);
        Assert.Equal(new long[] { 1, 2 }, feed.Select(m => m.Sequence));
        Assert.Equal("记住这句话。", feed[0].Text);

        await WaitUntilAsync(() => !host.HasPublicSyncError);
        Assert.False(host.HasPublicSyncError);
    }

    [Fact]
    public async Task Two_sessions_keep_their_own_public_feeds()
    {
        using var store = await NewStoreAsync();
        var host = NewHost(store, out _);

        await host.LoadDemoAsync();
        host.InputText = "演示这一局说的话。";
        await host.SendAsync();
        await WaitForFeedAsync(store, "session-demo", 2);

        // 换一局：另一局的消息不能混进演示这一局
        var other = DemoPackage.Create() with { PackageId = "other-script", Title = "另一份合成剧本" };
        await host.LoadPackageAsync(other);
        host.InputText = "另一局说的话。";
        await host.SendAsync();
        await WaitForFeedAsync(store, "session-other-script", 2);

        var first = await store.ReadPublicMessagesAsync("session-demo", 0, 100, TestContext.Current.CancellationToken);
        var second = await store.ReadPublicMessagesAsync("session-other-script", 0, 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.All(first, m => Assert.Equal("session-demo", m.SessionId));
        Assert.All(second, m => Assert.Equal("session-other-script", m.SessionId));
        Assert.DoesNotContain(first, m => m.Text == "另一局说的话。");
    }

    [Fact]
    public async Task Local_diagnostics_do_not_enter_the_public_feed()
    {
        using var store = await NewStoreAsync();
        var host = NewHost(store, out _);

        await host.LoadDemoAsync();

        // 这些是给操作者看的本机提示：出现在电脑的聊天区，但不该上平板。
        host.ReportProblem("装载失败，请重试。");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var feed = await store.ReadPublicMessagesAsync("session-demo", 0, 100, TestContext.Current.CancellationToken);
        Assert.Empty(feed);

        // 提示本身还在电脑的聊天区里
        Assert.Contains(host.Messages, m => m.Text.Contains("装载失败", StringComparison.Ordinal));
    }

    private async Task<SessionStore> NewStoreAsync()
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        return store;
    }

    private static HostViewModel NewHost(ISessionStore store, out TestGateway gateway)
    {
        gateway = new TestGateway();
        var tts = new SilentTts();
        var settings = ToolSettings.Default with { ModelId = "test-model", AutoRead = false };
        return new HostViewModel(
            new HostDirector(gateway, tts),
            () => settings,
            new HostIntentRouter(),
            new CommandDispatcher(store),
            store,
            tts);
    }

    /// <summary>等公开消息写进去：写入是异步的，不能假设发完消息就已经落盘。</summary>
    private static async Task<IReadOnlyList<PublicMessage>> WaitForFeedAsync(
        SessionStore store,
        string sessionId,
        int expected)
    {
        for (var i = 0; i < 200; i++)
        {
            var feed = await store.ReadPublicMessagesAsync(sessionId, 0, 100, TestContext.Current.CancellationToken);
            if (feed.Count >= expected)
            {
                return feed;
            }

            await Task.Delay(25);
        }

        var last = await store.ReadPublicMessagesAsync(sessionId, 0, 100, TestContext.Current.CancellationToken);
        Assert.Fail($"公开消息一直没写够：期望 {expected} 条，实际 {last.Count} 条。");
        return last;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
    }

    /// <summary>把公开消息表换成一条永远不通过的约束：写进去就失败。</summary>
    private static async Task BreakPublicMessageTableAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DROP TABLE public_messages;" +
            "CREATE TABLE public_messages (" +
            "session_id TEXT NOT NULL, sequence INTEGER NOT NULL, message_id TEXT NOT NULL," +
            "author INTEGER NOT NULL, kind INTEGER NOT NULL, body TEXT NOT NULL, created_at TEXT NOT NULL," +
            "PRIMARY KEY (session_id, sequence), UNIQUE (session_id, message_id)," +
            "CHECK (1 = 0));";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>把约束去掉，恢复成能写的表。</summary>
    private static async Task RepairPublicMessageTableAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DROP TABLE public_messages;" +
            "CREATE TABLE public_messages (" +
            "session_id TEXT NOT NULL, sequence INTEGER NOT NULL, message_id TEXT NOT NULL," +
            "author INTEGER NOT NULL, kind INTEGER NOT NULL, body TEXT NOT NULL, created_at TEXT NOT NULL," +
            "PRIMARY KEY (session_id, sequence), UNIQUE (session_id, message_id));";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class TestGateway : IMagpieGateway
    {
        public const string Reply = "这是测试用的公开主持文字。";

        public int CompletionCount { get; private set; }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken)
        {
            CompletionCount++;
            return Task.FromResult(Reply);
        }
    }

    private sealed class SilentTts : ITtsService
    {
        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop()
        {
        }
    }
}
