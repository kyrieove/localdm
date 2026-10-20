using System.IO;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.Tests;

/// <summary>
/// 流程由剧本包驱动：阶段数量与顺序、动作名与可见性都随剧本数据变化。
/// 用来锁住「不再套固定七步」「没有投票就不出现投票入口」这两件事。
/// </summary>
public sealed class ScriptFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localdm-flow-" + Guid.NewGuid().ToString("N"));

    public ScriptFlowTests() => Directory.CreateDirectory(_root);

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

    /// <summary>双轮搜证、无投票的剧本包：全部内容都是合成占位。</summary>
    internal static ScriptPackage DoubleRoundPackage()
    {
        var pages = new[]
        {
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "组织", PageNumber = 1, PageLabel = "1", Text = "公共序章", FileId = "f0", SpanId = "sp0",
            },
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "A01", GroupKind = RealScriptPackageBuilder.ExtractedGroupKind.Material,
                PageNumber = 1, PageLabel = "1", Text = "第一轮线索", FileId = "f1", SpanId = "sp1",
            },
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "B01", GroupKind = RealScriptPackageBuilder.ExtractedGroupKind.Material,
                PageNumber = 1, PageLabel = "1", Text = "第二轮线索", FileId = "f2", SpanId = "sp2",
            },
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "组织", PageNumber = 9, PageLabel = "9", Text = "合成结局文本（不得提前出现）",
                FileId = "f3", SpanId = "sp3",
            },
        };
        var sources = pages.Select(p => new SourceFile
        {
            FileId = p.FileId,
            PrivatePath = p.FileId,
            ContentHash = new string('a', 64),
            PageCount = 1,
            Origin = SourceOrder.ManifestOrder,
            ToolIdentity = "test",
            ParseState = FileParseState.Complete,
        }).ToArray();

        var flow = TestScriptFlow.DoubleRoundWithoutVote("A01", "B01");
        return RealScriptPackageBuilder.Build("双轮搜证合成剧本", 5, pages, sources, flow: flow).Package;
    }

    private static Session PreparedSession() => new()
    {
        SessionId = "session-flow",
        PackageId = "pkg-double",
        PackageVersion = "1",
        ManifestHash = "hash",
        State = SessionState.Prepared,
        CurrentPhaseId = null,
        Revision = 0,
        ReleasedMaterialIds = [],
    };

    private static Session NewSessionFor(ScriptPackage package) => PreparedSession() with
    {
        PackageId = package.PackageId,
        PackageVersion = package.Version,
        ManifestHash = package.ManifestHash,
        CurrentPhaseId = package.Phases[0].PhaseId,
    };

    [Fact]
    public void Double_round_flow_comes_from_the_declaration_not_a_fixed_template()
    {
        var package = DoubleRoundPackage();

        Assert.Equal(6, package.Phases.Count);
        Assert.Equal(
            ["prologue", "search-one", "talk-one", "search-two", "talk-two", "ending"],
            package.Phases.Select(p => p.PhaseId).ToArray());

        // 没有投票环节：整包都不允许提交票数
        Assert.DoesNotContain(package.Phases, p => p.IsVotePhase);
        Assert.DoesNotContain(package.Phases, p => p.AllowedActions.Contains(PublicAction.SubmitVoteSummary));

        // 两个搜证环节各自只针对本轮材料
        var firstRound = package.Phases[1];
        var secondRound = package.Phases[3];
        Assert.Equal(["A01"], firstRound.MaterialIds);
        Assert.Equal(["B01"], secondRound.MaterialIds);
        Assert.True(firstRound.RequiresAllMaterials);
        Assert.True(secondRound.RequiresAllMaterials);

        // 动作名随剧本走：写清进哪儿，而不是永远「下一环节」
        Assert.Equal("进入第一轮搜证", package.Phases[0].AdvanceLabel);
        Assert.Equal("进入第一轮讨论", firstRound.AdvanceLabel);
        Assert.Equal("进入结局与复盘", package.Phases[4].AdvanceLabel);

        // 最后一环没有「下一步」
        Assert.DoesNotContain(PublicAction.Advance, package.Phases[^1].AllowedActions);
        Assert.Equal("公开结局", package.Phases[^1].RevealLabel);
    }

    [Fact]
    public void Requires_all_materials_blocks_advance_until_every_id_is_released()
    {
        var package = DoubleRoundPackage();
        var session = GameEngine.Apply(
            NewSessionFor(package), package, Command(GameCommandType.Start, 0, "op-start"));
        session = GameEngine.Apply(session, package, Command(GameCommandType.Advance, 1, "op-adv-1"));
        Assert.Equal("search-one", session.CurrentPhaseId);

        // 第一轮材料还没发：推进被拒，且什么都没发出去
        var blocked = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            session, package, Command(GameCommandType.Advance, session.Revision, "op-adv-2")));
        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, blocked.Code);
        Assert.Contains("没有发放", blocked.PublicReason, StringComparison.Ordinal);

        var released = GameEngine.Apply(
            session, package, Command(GameCommandType.ReleaseMaterials, session.Revision, "op-mat-1", materials: ["A01"]));
        Assert.Contains("A01", released.ReleasedMaterialIds);

        var advanced = GameEngine.Apply(
            released, package, Command(GameCommandType.Advance, released.Revision, "op-adv-3"));
        Assert.Equal("talk-one", advanced.CurrentPhaseId);
    }

    [Fact]
    public void Flow_without_vote_can_reveal_without_any_vote_summary()
    {
        var package = DoubleRoundPackage();
        var session = GameEngine.Apply(
            NewSessionFor(package), package, Command(GameCommandType.Start, 0, "op-start"));

        session = Advance(session, package, "op-1");
        session = Release(session, package, "A01");
        session = Advance(session, package, "op-2");
        session = Advance(session, package, "op-3");
        Assert.Equal("search-two", session.CurrentPhaseId);
        session = Release(session, package, "B01");
        session = Advance(session, package, "op-4");
        session = Advance(session, package, "op-5");
        Assert.Equal("ending", session.CurrentPhaseId);
        Assert.Null(session.VoteSummary);

        var revealed = GameEngine.Apply(
            session, package, Command(GameCommandType.Reveal, session.Revision, "op-reveal"));
        Assert.Equal(SessionState.Revealed, revealed.State);
    }

    [Fact]
    public async Task Session_view_actions_and_labels_follow_the_package()
    {
        var package = DoubleRoundPackage();
        using var store = await NewStoreAsync(package, NewSessionFor(package));
        var view = new SessionViewModel(new CommandDispatcher(store));
        view.Attach(package, NewSessionFor(package));

        Assert.Equal("序章共读", view.PhaseTitle);
        Assert.Equal("1 / 6", view.PhaseProgress);
        Assert.Equal("序章共读环节当前提示", view.Hint);
        Assert.True(view.IsStartVisible);
        Assert.False(view.IsAdvanceVisible, "待开局时只给「开始这一局」。");
        Assert.False(view.IsVoteVisible, "这个剧本没有投票环节。");
        Assert.False(view.IsRevealVisible);

        await view.ExecuteAsync(GameCommandType.Start);
        Assert.Equal("进入第一轮搜证", view.AdvanceLabel);
        Assert.True(view.CanAdvance);

        await view.ExecuteAsync(GameCommandType.Advance);
        Assert.Equal("第一轮搜证", view.PhaseTitle);
        Assert.Equal("2 / 6", view.PhaseProgress);
        Assert.Equal("发放材料 A01", view.ReleaseLabel);
        Assert.False(view.CanAdvance, "本轮材料还没发完，推进按钮先灰掉。");
        Assert.Contains("待发放", view.ActionHint, StringComparison.Ordinal);

        await view.ExecuteAsync(GameCommandType.ReleaseMaterials, ["A01"]);
        Assert.True(view.CanAdvance, "材料发完后按钮恢复。");
        Assert.Empty(view.PendingMaterials);
        Assert.Contains("A01", view.ReleasedMaterials);

        await view.ExecuteAsync(GameCommandType.Advance);
        Assert.Equal("第一轮讨论", view.PhaseTitle);
        Assert.False(view.IsReleaseVisible, "讨论环节不该出现发牌动作。");

        await view.ExecuteAsync(GameCommandType.Advance);
        Assert.Equal("第二轮搜证", view.PhaseTitle);
        Assert.Equal("发放材料 B01", view.ReleaseLabel);
        Assert.DoesNotContain("A01", view.PendingMaterials);

        await view.ExecuteAsync(GameCommandType.ReleaseMaterials, ["B01"]);
        await view.ExecuteAsync(GameCommandType.Advance);
        await view.ExecuteAsync(GameCommandType.Advance);
        Assert.Equal("结局与复盘", view.PhaseTitle);

        // 揭秘动作名来自剧本：这个流程写的是「公开结局」
        Assert.Equal("公开结局", view.RevealLabel);
        Assert.True(view.IsRevealVisible);
        Assert.Contains("所有玩家", view.RevealWarning, StringComparison.Ordinal);
        Assert.False(view.IsVoteVisible, "无投票流程到这里也不该冒出投票入口。");

        await view.ExecuteAsync(GameCommandType.Reveal);
        Assert.Equal("Revealed", view.SessionStateText);
        Assert.True(view.IsCompleteVisible);
    }

    [Fact]
    public async Task One_click_increments_revision_exactly_once()
    {
        var package = DoubleRoundPackage();
        using var store = await NewStoreAsync(package, NewSessionFor(package));
        var view = new SessionViewModel(new CommandDispatcher(store));
        view.Attach(package, NewSessionFor(package));

        await view.ExecuteAsync(GameCommandType.Start);
        Assert.Equal(1, view.Revision);
        Assert.Equal("序章共读", view.PhaseTitle);

        await view.ExecuteAsync(GameCommandType.Advance);
        Assert.Equal(2, view.Revision);
        Assert.Equal("第一轮搜证", view.PhaseTitle);
    }

    [Fact]
    public async Task A_second_click_while_the_first_is_in_flight_does_not_commit_twice()
    {
        var package = DoubleRoundPackage();
        using var inner = await NewStoreAsync(package, NewSessionFor(package));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedStore(inner, gate.Task);
        var view = new SessionViewModel(new CommandDispatcher(store));
        view.Attach(package, NewSessionFor(package));

        var first = view.ExecuteAsync(GameCommandType.Start);
        // 第一次还在派发（被 gate 挡住）：第二次点击必须被忙碌闸拦下
        await view.ExecuteAsync(GameCommandType.Start);
        Assert.Contains("还在进行", view.Status, StringComparison.Ordinal);

        gate.SetResult();
        await first;

        Assert.Equal(1, view.Revision);
        Assert.Equal(1, store.CommitCount);
        Assert.Equal("Playing", view.SessionStateText);
    }

    private async Task<SessionStore> NewStoreAsync(ScriptPackage package, Session session)
    {
        var store = new SessionStore(Path.Combine(_root, "sessions-" + Guid.NewGuid().ToString("N") + ".db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.SaveSessionAsync(session, TestContext.Current.CancellationToken);
        await store.RegisterPackageAsync(package, TestContext.Current.CancellationToken);
        return store;
    }

    private static GameCommand Command(
        GameCommandType type,
        int revision,
        string operationId,
        IReadOnlyList<string>? materials = null) => new()
    {
        SessionId = "session-flow",
        OperationId = operationId,
        ExpectedRevision = revision,
        Type = type,
        MaterialIds = materials ?? [],
    };

    private static Session Advance(Session session, ScriptPackage package, string operationId) =>
        GameEngine.Apply(session, package, Command(GameCommandType.Advance, session.Revision, operationId));

    private static Session Release(Session session, ScriptPackage package, string materialId) =>
        GameEngine.Apply(
            session,
            package,
            Command(GameCommandType.ReleaseMaterials, session.Revision, "op-release-" + materialId, materials: [materialId]));

    /// <summary>第一次写事务卡在 gate 上，用来验证「连点不会提交两次」。</summary>
    private sealed class GatedStore : ISessionStore
    {
        private readonly ISessionStore _inner;
        private readonly Task _gate;

        public GatedStore(ISessionStore inner, Task gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public int CommitCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => _inner.InitializeAsync(cancellationToken);

        public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            _inner.LoadSessionAsync(sessionId, cancellationToken);

        public Task<OperationReceipt?> FindReceiptAsync(string sessionId, string operationId, CancellationToken cancellationToken) =>
            _inner.FindReceiptAsync(sessionId, operationId, cancellationToken);

        public async Task CommitAsync(
            Session current,
            Session next,
            GameCommand command,
            GameEvent gameEvent,
            OperationReceipt receipt,
            CancellationToken cancellationToken)
        {
            await _gate;
            CommitCount++;
            await _inner.CommitAsync(current, next, command, gameEvent, receipt, cancellationToken);
        }

        public Task<IReadOnlyList<GameEvent>> ReadEventsAsync(string sessionId, CancellationToken cancellationToken) =>
            _inner.ReadEventsAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<StoredSessionRef>> ListSessionsAsync(CancellationToken cancellationToken) =>
            _inner.ListSessionsAsync(cancellationToken);

        public Task SaveSessionAsync(Session session, CancellationToken cancellationToken) =>
            _inner.SaveSessionAsync(session, cancellationToken);

        public Task SaveDmConversationAsync(string sessionId, DmConversationSnapshot snapshot, CancellationToken cancellationToken) =>
            _inner.SaveDmConversationAsync(sessionId, snapshot, cancellationToken);

        public Task<DmConversationSnapshot?> LoadDmConversationAsync(string sessionId, CancellationToken cancellationToken) =>
            _inner.LoadDmConversationAsync(sessionId, cancellationToken);
    }
}
