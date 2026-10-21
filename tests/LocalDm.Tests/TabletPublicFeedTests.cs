using System.IO;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.Tests;

/// <summary>
/// 平板公开消息记录：桌上公开说过的话单独存一份。
///
/// 和给模型看的对话记忆分开：不存备忘，也不会从 AI 备忘重建。
/// 序号在本局内单调递增，同一条消息只存一次，能按序号补发，按本局隔离。
/// </summary>
public sealed class TabletPublicFeedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-public-feed-" + Guid.NewGuid().ToString("N"));

    public TabletPublicFeedTests() => Directory.CreateDirectory(_root);

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
    public async Task Appended_messages_get_monotonic_sequences()
    {
        using var store = await NewStoreAsync();
        var ct = TestContext.Current.CancellationToken;

        var first = await store.AppendPublicMessageAsync(Message("session-a", "m1", "第一条"), ct);
        var second = await store.AppendPublicMessageAsync(Message("session-a", "m2", "第二条"), ct);
        var third = await store.AppendPublicMessageAsync(Message("session-a", "m3", "第三条"), ct);

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(3, third.Sequence);
        Assert.True(first.Sequence < second.Sequence && second.Sequence < third.Sequence);
    }

    [Fact]
    public async Task Every_public_field_survives_a_round_trip()
    {
        using var store = await NewStoreAsync();
        var ct = TestContext.Current.CancellationToken;
        var createdAt = new DateTimeOffset(2026, 10, 22, 21, 30, 45, 123, TimeSpan.FromHours(3));

        var stored = await store.AppendPublicMessageAsync(
            new PublicMessage
            {
                SessionId = "session-a",
                MessageId = "m1",
                Author = PublicMessageAuthor.Host,
                Kind = PublicMessageKind.HostReply,
                Text = "主持的公开回复",
                CreatedAt = createdAt,
            },
            ct);

        var feed = await store.ReadPublicMessagesAsync("session-a", 0, 100, ct);
        var message = Assert.Single(feed);
        Assert.Equal("session-a", message.SessionId);
        Assert.Equal("m1", message.MessageId);
        Assert.Equal(PublicMessageAuthor.Host, message.Author);
        Assert.Equal(PublicMessageKind.HostReply, message.Kind);
        Assert.Equal("主持的公开回复", message.Text);
        Assert.Equal(createdAt, message.CreatedAt);
        Assert.Equal(createdAt, stored.CreatedAt);
        Assert.Equal(stored.Sequence, message.Sequence);
    }

    [Fact]
    public async Task Same_message_id_is_stored_only_once()
    {
        using var store = await NewStoreAsync();
        var ct = TestContext.Current.CancellationToken;

        var first = await store.AppendPublicMessageAsync(Message("session-a", "m1", "同一句话"), ct);

        // 重试：同一条消息再写一次，不能多出一条，也不能多消耗一个序号
        var again = await store.AppendPublicMessageAsync(Message("session-a", "m1", "同一句话"), ct);
        Assert.Equal(first.Sequence, again.Sequence);

        var all = await store.ReadPublicMessagesAsync("session-a", 0, 100, ct);
        Assert.Single(all);

        // 接着写别的：序号紧跟着走，重试没有留下缺口
        var next = await store.AppendPublicMessageAsync(Message("session-a", "m2", "下一句"), ct);
        Assert.Equal(2, next.Sequence);
        Assert.Equal(2, (await store.ReadPublicMessagesAsync("session-a", 0, 100, ct)).Count);
    }

    [Fact]
    public async Task Read_after_returns_only_the_newer_messages()
    {
        using var store = await NewStoreAsync();
        var ct = TestContext.Current.CancellationToken;
        for (var i = 1; i <= 5; i++)
        {
            await store.AppendPublicMessageAsync(Message("session-a", "m" + i, "第" + i + "句"), ct);
        }

        var all = await store.ReadPublicMessagesAsync("session-a", 0, 100, ct);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, all.Select(m => m.Sequence));

        // 断线重连：只要序号大于 after 的那些
        var tail = await store.ReadPublicMessagesAsync("session-a", 3, 100, ct);
        Assert.Equal(new long[] { 4, 5 }, tail.Select(m => m.Sequence));
        Assert.Equal("第4句", tail[0].Text);

        // 已经是最新：没有新的可补
        Assert.Empty(await store.ReadPublicMessagesAsync("session-a", 5, 100, ct));

        // 分页：一次一页
        var firstPage = await store.ReadPublicMessagesAsync("session-a", 0, 2, ct);
        Assert.Equal(new long[] { 1, 2 }, firstPage.Select(m => m.Sequence));
        var secondPage = await store.ReadPublicMessagesAsync("session-a", firstPage[^1].Sequence, 2, ct);
        Assert.Equal(new long[] { 3, 4 }, secondPage.Select(m => m.Sequence));
    }

    [Fact]
    public async Task Different_sessions_are_isolated()
    {
        using var store = await NewStoreAsync();
        var ct = TestContext.Current.CancellationToken;

        await store.AppendPublicMessageAsync(Message("session-a", "a1", "A 局的话"), ct);
        await store.AppendPublicMessageAsync(Message("session-b", "b1", "B 局的话"), ct);
        await store.AppendPublicMessageAsync(Message("session-b", "b2", "B 局的另一句"), ct);

        var a = await store.ReadPublicMessagesAsync("session-a", 0, 100, ct);
        var b = await store.ReadPublicMessagesAsync("session-b", 0, 100, ct);

        Assert.Single(a);
        Assert.Equal("A 局的话", a[0].Text);
        Assert.Equal(2, b.Count);
        Assert.Equal(new long[] { 1, 2 }, b.Select(m => m.Sequence));

        // 别的局补发时，另一局的消息不会混进来
        var bTail = await store.ReadPublicMessagesAsync("session-b", 1, 100, ct);
        Assert.Single(bTail);
        Assert.Equal("B 局的另一句", bTail[0].Text);
    }

    [Fact]
    public async Task Messages_survive_a_restart()
    {
        var path = Path.Combine(_root, "sessions.db");
        using (var store = new SessionStore(path))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.AppendPublicMessageAsync(
                Message("session-a", "m1", "第一句"),
                TestContext.Current.CancellationToken);
            await store.AppendPublicMessageAsync(
                Message("session-a", "m2", "第二句"),
                TestContext.Current.CancellationToken);
        }

        // 强退：清空连接池后重新打开同一个文件
        SqliteConnection.ClearAllPools();

        using (var reopened = new SessionStore(path))
        {
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);
            var messages = await reopened.ReadPublicMessagesAsync(
                "session-a",
                0,
                100,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, messages.Count);
            Assert.Equal(new long[] { 1, 2 }, messages.Select(m => m.Sequence));
            Assert.Equal("第一句", messages[0].Text);

            // 重启后接着写：序号继续往下走
            var next = await reopened.AppendPublicMessageAsync(
                Message("session-a", "m3", "第三句"),
                TestContext.Current.CancellationToken);
            Assert.Equal(3, next.Sequence);
        }
    }

    [Fact]
    public async Task An_old_database_gets_the_public_message_table()
    {
        var path = Path.Combine(_root, "old-sessions.db");
        await CreateOldDatabaseAsync(path);

        using var store = new SessionStore(path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var stored = await store.AppendPublicMessageAsync(
            Message("session-old", "m1", "升级后写的消息"),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, stored.Sequence);

        var messages = await store.ReadPublicMessagesAsync(
            "session-old",
            0,
            100,
            TestContext.Current.CancellationToken);
        Assert.Single(messages);
        Assert.Equal("升级后写的消息", messages[0].Text);
    }

    [Fact]
    public async Task Public_messages_are_stored_apart_from_the_model_memory()
    {
        using var store = await NewStoreAsync();
        var ct = TestContext.Current.CancellationToken;

        // 模型记忆里有备忘：那是给模型看的
        await store.SaveDmConversationAsync(
            "session-a",
            new DmConversationSnapshot
            {
                Memo = "这是给模型看的备忘",
                Turns = [new MagpieMessage { Role = "user", Content = "玩家说的话" }],
                CompactionCount = 1,
            },
            ct);

        await store.AppendPublicMessageAsync(
            Message("session-a", "m1", "桌上公开说的话", PublicMessageKind.HostReply, PublicMessageAuthor.Host),
            ct);

        var feed = await store.ReadPublicMessagesAsync("session-a", 0, 100, ct);
        Assert.Single(feed);
        Assert.Equal("桌上公开说的话", feed[0].Text);

        // 公开消息里没有备忘，也不会从 AI 备忘重建
        var serialized = System.Text.Json.JsonSerializer.Serialize(feed);
        Assert.DoesNotContain("这是给模型看的备忘", serialized, StringComparison.Ordinal);

        // 两边各存各的：模型记忆还在
        var memory = await store.LoadDmConversationAsync("session-a", ct);
        Assert.NotNull(memory);
        Assert.Equal("这是给模型看的备忘", memory!.Memo);
        Assert.Equal(1, memory.CompactionCount);
    }

    [Fact]
    public async Task A_failing_write_throws_a_sync_exception()
    {
        var path = Path.Combine(_root, "failing.db");
        using (var store = new SessionStore(path))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
        }

        // 把公开消息表换成一条永远不通过的约束：写进去就失败
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DROP TABLE public_messages;" +
                "CREATE TABLE public_messages (" +
                "session_id TEXT NOT NULL, sequence INTEGER NOT NULL, message_id TEXT NOT NULL," +
                "author INTEGER NOT NULL, kind INTEGER NOT NULL, body TEXT NOT NULL, created_at TEXT NOT NULL," +
                "PRIMARY KEY (session_id, sequence), UNIQUE (session_id, message_id)," +
                "CHECK (body <> '写不进去'));";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var failing = new SessionStore(path);
        await failing.InitializeAsync(TestContext.Current.CancellationToken);

        // 写不进去必须抛出明确的异常：界面据此显示「同步异常」，不能吞掉
        var ex = await Assert.ThrowsAsync<PublicMessageSyncException>(
            () => failing.AppendPublicMessageAsync(
                Message("session-a", "m1", "写不进去"),
                TestContext.Current.CancellationToken));
        Assert.Contains("没有存下来", ex.PublicReason, StringComparison.Ordinal);
    }

    private async Task<SessionStore> NewStoreAsync()
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        return store;
    }

    /// <summary>造一个「还没有公开消息表」的旧库：只有当时的几个表。</summary>
    private static async Task CreateOldDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE schema_version (version INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE sessions (session_id TEXT NOT NULL PRIMARY KEY, package_id TEXT NOT NULL);" +
            "CREATE TABLE dm_conversations (" +
            "session_id TEXT NOT NULL PRIMARY KEY, snapshot_json TEXT NOT NULL, updated_at TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static PublicMessage Message(
        string sessionId,
        string messageId,
        string text,
        PublicMessageKind kind = PublicMessageKind.PlayerSpeech,
        PublicMessageAuthor author = PublicMessageAuthor.Player) => new()
        {
            SessionId = sessionId,
            MessageId = messageId,
            Author = author,
            Text = text,
            Kind = kind,
            CreatedAt = new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.FromHours(3)),
        };
}
