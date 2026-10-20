using System.IO;
using LocalDm.Core.Domain;
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
