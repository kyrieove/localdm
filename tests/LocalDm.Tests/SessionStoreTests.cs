using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.Tests;

/// <summary>T011 / T039：receipt 唯一、revision 从 0 起恰加 1、事务失败三者均不变。</summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localdm-tests-" + Guid.NewGuid().ToString("N"));

    public SessionStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响断言结论
        }
    }

    private SessionStore NewStore() => new(Path.Combine(_root, "sessions.db"));

    private static Session NewSession(string id = "s1") => new()
    {
        SessionId = id,
        PackageId = "pkg",
        PackageVersion = "1",
        ManifestHash = new string('a', 64),
        State = SessionState.Prepared,
        CurrentPhaseId = null,
        Revision = 0,
        ReleasedMaterialIds = [],
    };

    [Fact]
    public async Task Revision_starts_at_zero_and_increments_by_exactly_one()
    {
        using var store = NewStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var current = NewSession();
        await store.SaveSessionAsync(current, TestContext.Current.CancellationToken);

        var next = current with { Revision = current.Revision + 1, State = SessionState.Playing };
        var command = new GameCommand
        {
            SessionId = current.SessionId,
            OperationId = "op1",
            ExpectedRevision = 0,
            Type = GameCommandType.Start,
        };
        await store.CommitAsync(
            current,
            next,
            command,
            NewEvent(next, command),
            NewReceipt(next, command),
            TestContext.Current.CancellationToken);

        var loaded = await store.LoadSessionAsync("s1", TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.Revision);
        Assert.Equal(SessionState.Playing, loaded.State);
    }

    [Fact]
    public async Task Deleting_a_session_removes_its_progress_and_conversation_but_not_other_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = NewStore();
        await store.InitializeAsync(ct);
        foreach (var id in new[] { "s1", "s2" })
        {
            var current = NewSession(id);
            await store.SaveSessionAsync(current, ct);
            var next = current with { Revision = 1, State = SessionState.Playing };
            var command = new GameCommand
            {
                SessionId = id,
                OperationId = "op1",
                ExpectedRevision = 0,
                Type = GameCommandType.Start,
            };
            await store.CommitAsync(current, next, command, NewEvent(next, command), NewReceipt(next, command), ct);
            await store.SaveDmConversationAsync(id, new DmConversationSnapshot { Memo = "备忘", Turns = [] }, ct);
        }

        await store.DeleteSessionAsync("s1", ct);

        Assert.Null(await store.LoadSessionAsync("s1", ct));
        Assert.Empty(await store.ReadEventsAsync("s1", ct));
        Assert.Null(await store.FindReceiptAsync("s1", "op1", ct));
        Assert.Null(await store.LoadDmConversationAsync("s1", ct));
        Assert.Equal(SessionState.Playing, (await store.LoadSessionAsync("s2", ct))!.State);
        Assert.NotNull(await store.LoadDmConversationAsync("s2", ct));
    }

    [Fact]
    public async Task Commit_with_wrong_revision_is_stale_and_changes_nothing()
    {
        using var store = NewStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var current = NewSession();
        await store.SaveSessionAsync(current, TestContext.Current.CancellationToken);

        var next = current with { Revision = 1, State = SessionState.Playing };
        var command = new GameCommand
        {
            SessionId = "s1",
            OperationId = "op-stale",
            ExpectedRevision = 5,
            Type = GameCommandType.Start,
        };

        await Assert.ThrowsAsync<DomainRuleException>(() => store.CommitAsync(
            current,
            next,
            command,
            NewEvent(next, command),
            NewReceipt(next, command),
            TestContext.Current.CancellationToken));

        Assert.Empty(await store.ReadEventsAsync("s1", TestContext.Current.CancellationToken));
        Assert.Null(await store.FindReceiptAsync("s1", "op-stale", TestContext.Current.CancellationToken));
        var loaded = await store.LoadSessionAsync("s1", TestContext.Current.CancellationToken);
        Assert.Equal(0, loaded!.Revision);
    }

    [Fact]
    public async Task Fault_before_event_write_leaves_all_three_unchanged()
    {
        await AssertFaultLeavesNothingAsync(TransactionFaultPoint.BeforeEventWrite);
    }

    [Fact]
    public async Task Fault_after_event_write_rolls_back_event_and_receipt()
    {
        await AssertFaultLeavesNothingAsync(TransactionFaultPoint.AfterEventWrite);
    }

    [Fact]
    public async Task Fault_before_commit_rolls_back_everything()
    {
        await AssertFaultLeavesNothingAsync(TransactionFaultPoint.BeforeCommit);
    }

    [Fact]
    public async Task Same_operation_id_twice_returns_duplicate_not_second_revision()
    {
        using var store = NewStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var current = NewSession();
        await store.SaveSessionAsync(current, TestContext.Current.CancellationToken);

        var next = current with { Revision = 1, State = SessionState.Playing };
        var command = new GameCommand
        {
            SessionId = "s1",
            OperationId = "op-dup",
            ExpectedRevision = 0,
            Type = GameCommandType.Start,
        };
        await store.CommitAsync(current, next, command, NewEvent(next, command), NewReceipt(next, command), TestContext.Current.CancellationToken);

        // 第二次同 ID：唯一约束必须阻止第二次写入
        var retry = next with { Revision = 2 };
        var dup = await Assert.ThrowsAsync<DuplicateOperationException>(() => store.CommitAsync(
            next,
            retry,
            command,
            NewEvent(retry, command),
            NewReceipt(retry, command),
            TestContext.Current.CancellationToken));

        var events = await store.ReadEventsAsync("s1", TestContext.Current.CancellationToken);
        Assert.Single(events);
        Assert.Equal(1, dup.Original.Revision);
        Assert.Equal(1, (await store.LoadSessionAsync("s1", TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task Integrity_check_passes_on_fresh_database()
    {
        using var store = NewStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await store.CheckIntegrityAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dm_conversation_snapshot_round_trips()
    {
        using var store = NewStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var snapshot = new DmConversationSnapshot
        {
            Memo = "查过书房与地下室。",
            Turns =
            [
                new MagpieMessage { Role = "user", Content = "我们去调查书房" },
                new MagpieMessage { Role = "assistant", Content = "请翻开 1 楼那一组卡。" },
            ],
            CompactionCount = 2,
            LastUsage = new MagpieUsage(1234, 56, 1000),
        };

        await store.SaveDmConversationAsync("s1", snapshot, TestContext.Current.CancellationToken);
        var loaded = await store.LoadDmConversationAsync("s1", TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("查过书房与地下室。", loaded!.Memo);
        Assert.Equal(2, loaded.CompactionCount);
        Assert.Equal(2, loaded.Turns.Count);
        Assert.Equal("user", loaded.Turns[0].Role);
        Assert.Equal("我们去调查书房", loaded.Turns[0].Content);
        Assert.Equal("请翻开 1 楼那一组卡。", loaded.Turns[1].Content);
        Assert.Equal(1234, loaded.LastUsage!.PromptTokens);
        Assert.Equal(1000, loaded.LastUsage.CacheHitTokens);
    }

    [Fact]
    public async Task Saving_the_same_session_twice_replaces_the_first_snapshot()
    {
        using var store = NewStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var first = new DmConversationSnapshot { Memo = "第一份备忘", CompactionCount = 1 };
        var second = new DmConversationSnapshot { Memo = "第二份备忘", CompactionCount = 2 };

        await store.SaveDmConversationAsync("s1", first, TestContext.Current.CancellationToken);
        await store.SaveDmConversationAsync("s1", second, TestContext.Current.CancellationToken);

        var loaded = await store.LoadDmConversationAsync("s1", TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("第二份备忘", loaded!.Memo);
        Assert.Equal(2, loaded.CompactionCount);
    }

    [Fact]
    public async Task Missing_dm_conversation_returns_null()
    {
        using var store = NewStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadDmConversationAsync("还没有这一局", TestContext.Current.CancellationToken));
    }

    private async Task AssertFaultLeavesNothingAsync(TransactionFaultPoint point)
    {
        using var store = NewStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var current = NewSession();
        await store.SaveSessionAsync(current, TestContext.Current.CancellationToken);
        store.FaultInjector = p =>
        {
            if (p == point)
            {
                throw new InvalidOperationException("injected fault at " + p);
            }
        };

        var next = current with { Revision = 1, State = SessionState.Playing };
        var command = new GameCommand
        {
            SessionId = "s1",
            OperationId = "op-" + point,
            ExpectedRevision = 0,
            Type = GameCommandType.Start,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(
            current,
            next,
            command,
            NewEvent(next, command),
            NewReceipt(next, command),
            TestContext.Current.CancellationToken));

        store.FaultInjector = null;
        Assert.Empty(await store.ReadEventsAsync("s1", TestContext.Current.CancellationToken));
        Assert.Null(await store.FindReceiptAsync("s1", command.OperationId, TestContext.Current.CancellationToken));
        var loaded = await store.LoadSessionAsync("s1", TestContext.Current.CancellationToken);
        Assert.Equal(0, loaded!.Revision);
        Assert.Equal(SessionState.Prepared, loaded.State);
    }

    private static GameEvent NewEvent(Session s, GameCommand c) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        SessionId = s.SessionId,
        OperationId = c.OperationId,
        Revision = s.Revision,
        Type = c.Type,
        SafePayload = "{}",
        CommittedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static OperationReceipt NewReceipt(Session s, GameCommand c) => new()
    {
        SessionId = s.SessionId,
        OperationId = c.OperationId,
        PayloadFingerprint = c.PayloadFingerprint(),
        Revision = s.Revision,
        Type = c.Type,
        CommittedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };
}
