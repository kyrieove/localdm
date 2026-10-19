using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.Tests;

/// <summary>
/// T039：同 ID 十次重试、同 ID 不同 payload、过期 revision、事务前后强退恢复。
/// </summary>
public sealed class IdempotencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localdm-idem-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptPackage _package = SyntheticPackage.Create();

    public IdempotencyTests() => Directory.CreateDirectory(_root);

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

    private async Task<(SessionStore Store, CommandDispatcher Dispatcher)> NewDispatcherAsync()
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.SaveSessionAsync(SyntheticPackage.NewSession(), TestContext.Current.CancellationToken);
        return (store, new CommandDispatcher(store));
    }

    [Fact]
    public async Task Ten_retries_with_same_operation_id_commit_exactly_once()
    {
        var (store, dispatcher) = await NewDispatcherAsync();
        using var _ = store;
        var command = SyntheticPackage.Command(GameCommandType.Start, 0, "op-retry-10", groupReady: true);

        var outcomes = new List<DispatchOutcome>();
        for (var i = 0; i < 10; i++)
        {
            var result = await dispatcher.DispatchAsync(command, _package, TestContext.Current.CancellationToken);
            outcomes.Add(result.Outcome);
        }

        Assert.Equal(DispatchOutcome.Committed, outcomes[0]);
        Assert.All(outcomes.Skip(1), o => Assert.Equal(DispatchOutcome.Duplicate, o));

        var events = await store.ReadEventsAsync("session-1", TestContext.Current.CancellationToken);
        Assert.Single(events);

        var session = await store.LoadSessionAsync("session-1", TestContext.Current.CancellationToken);
        Assert.Equal(1, session!.Revision);
    }

    [Fact]
    public async Task Same_id_different_payload_is_a_conflict()
    {
        var (store, dispatcher) = await NewDispatcherAsync();
        using var _ = store;

        await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-same", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);

        var conflicting = await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Advance, 0, "op-same", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);

        Assert.Equal(DispatchOutcome.Rejected, conflicting.Outcome);
        Assert.Equal(DomainErrorCode.OPERATION_CONFLICT, conflicting.Rejection!.Code);
        Assert.Single(await store.ReadEventsAsync("session-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stale_revision_is_rejected_without_new_event()
    {
        var (store, dispatcher) = await NewDispatcherAsync();
        using var _ = store;

        await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-a", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);

        var stale = await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Advance, 0, "op-b", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);

        Assert.Equal(DomainErrorCode.STALE_REVISION, stale.Rejection!.Code);
        Assert.Single(await store.ReadEventsAsync("session-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_commit_keeps_previous_state_and_retry_with_new_id_succeeds()
    {
        var (store, dispatcher) = await NewDispatcherAsync();
        using var _ = store;

        store.FaultInjector = point =>
        {
            if (point == TransactionFaultPoint.BeforeCommit)
            {
                throw new InvalidOperationException("simulated crash before commit");
            }
        };

        var failed = await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-crash", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);

        Assert.Equal(DispatchOutcome.Rejected, failed.Outcome);
        Assert.Equal(DomainErrorCode.STORAGE_FAILED, failed.Rejection!.Code);

        store.FaultInjector = null;
        var session = await store.LoadSessionAsync("session-1", TestContext.Current.CancellationToken);
        Assert.Equal(0, session!.Revision);
        Assert.Empty(await store.ReadEventsAsync("session-1", TestContext.Current.CancellationToken));

        // 崩溃的操作不补执行；用新 operationId 重做
        var retry = await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-after-crash", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);
        Assert.Equal(DispatchOutcome.Committed, retry.Outcome);
        Assert.Equal(1, (await store.LoadSessionAsync("session-1", TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task Full_flow_persists_every_step_in_revision_order()
    {
        var (store, dispatcher) = await NewDispatcherAsync();
        using var _ = store;

        var session = SyntheticPackage.NewSession();
        session = await CommitAsync(dispatcher, GameCommandType.Start, session.Revision, "op-1", groupReady: true);
        session = await CommitAsync(dispatcher, GameCommandType.Advance, session.Revision, "op-2", groupReady: true);
        session = await CommitAsync(dispatcher, GameCommandType.ReleaseMaterials, session.Revision, "op-3", materials: ["M1"]);

        var events = await store.ReadEventsAsync("session-1", TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3], events.Select(e => e.Revision));
        Assert.Equal(["op-1", "op-2", "op-3"], events.Select(e => e.OperationId));
    }

    private async Task<Session> CommitAsync(
        CommandDispatcher dispatcher,
        GameCommandType type,
        int revision,
        string operationId,
        bool groupReady = false,
        IReadOnlyList<string>? materials = null)
    {
        var result = await dispatcher.DispatchAsync(
            SyntheticPackage.Command(type, revision, operationId, groupReady: groupReady, materials: materials),
            _package,
            TestContext.Current.CancellationToken);
        Assert.Equal(DispatchOutcome.Committed, result.Outcome);
        return (await dispatcher.LoadSessionAsync("session-1", TestContext.Current.CancellationToken))!;
    }
}
