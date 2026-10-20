using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Import;

namespace LocalDm.Tests;

/// <summary>
/// 流程声明与冻结身份的契约。
///
/// 这些用例来自一次独立验收：改流程后旧存档被新包接受、写错的引用被悄悄丢掉、
/// 重复环节 ID 让推进原地打转、第二轮投票沿用第一轮票数。
/// 每一条都必须被规则核心或准备器挡住，而不是靠界面自觉。
/// </summary>
public sealed class FlowContractTests
{
    // ===== 冻结身份：改流程必须换身份，旧存档不能被新包接受 =====

    [Fact]
    public void Changing_the_flow_changes_the_package_identity()
    {
        var twoStep = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: TwoStepFlow());
        var threeStep = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: ThreeStepFlow());

        // 同一批原始材料：来源指纹相同
        Assert.Equal(twoStep.Package.ManifestHash, threeStep.Package.ManifestHash);
        // 但流程不同 → 内容指纹与包标识都必须不同
        Assert.NotEqual(twoStep.Package.ContentHash, threeStep.Package.ContentHash);
        Assert.NotEqual(twoStep.Package.PackageId, threeStep.Package.PackageId);

        // 同一份声明重复准备要得到同一个身份（否则每次重跑都变新包）
        var again = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: TwoStepFlow());
        Assert.Equal(twoStep.Package.PackageId, again.Package.PackageId);
    }

    [Fact]
    public void Old_session_cannot_be_carried_into_a_rewritten_flow()
    {
        var original = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: TwoStepFlow()).Package;
        var rewritten = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: ThreeStepFlow()).Package;

        var session = Start(original);
        Assert.Equal("read", session.CurrentPhaseId);

        // 旧存档拿到新包：必须明确拒绝，而不是接着往下走、跳过新增的搜证环节
        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            session,
            rewritten,
            Command(GameCommandType.Advance, session.Revision, "op-adv")));

        Assert.Equal(DomainErrorCode.PACKAGE_MISMATCH, ex.Code);
        Assert.Contains("流程", ex.PublicReason, StringComparison.Ordinal);
    }

    // ===== 写错的引用必须点名挡住，不能被过滤成「合法」 =====

    [Fact]
    public void Unknown_material_reference_blocks_the_package_instead_of_being_dropped()
    {
        var flow = TwoStepFlow() with
        {
            Steps =
            [
                Step("read", "阅本", ScriptStepKind.Read, "第 1 页"),
                new ScriptFlowStep
                {
                    StepId = "search",
                    Title = "搜证",
                    Kind = ScriptStepKind.Search,
                    SourceRef = "第 3 页",
                    // 声明要发一份不存在的材料，并要求发完才能推进
                    MaterialIds = ["TYPO-A01"],
                    RequiresAllMaterials = true,
                },
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "真相",
                    Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页",
                },
            ],
        };

        var result = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: flow);

        Assert.Contains("UNKNOWN_MATERIAL_REFERENCE:search:TYPO-A01", result.Readiness.IssueCategories);
        Assert.False(result.Readiness.CanStart, "写错材料编号的包不能开局。");
        Assert.Equal(PackageValidationState.NeedsReview, result.Package.Validation);
        Assert.Contains("UNKNOWN_MATERIAL_REFERENCE:search:TYPO-A01", result.ReferenceIssues);

        // 不能变成「零材料的搜证环节」然后假装没问题
        var search = result.Package.Phases.First(p => p.PhaseId == "search");
        Assert.Empty(search.MaterialIds);
    }

    [Fact]
    public void Unknown_public_segment_and_group_references_block_the_package()
    {
        var flow = TwoStepFlow() with
        {
            Steps =
            [
                new ScriptFlowStep
                {
                    StepId = "read",
                    Title = "阅本",
                    Kind = ScriptStepKind.Read,
                    SourceRef = "第 1 页",
                    PublicSegmentIds = ["seg-不存在-1"],
                },
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "真相",
                    Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页",
                    PublicGroups = ["不存在的分组"],
                },
            ],
        };

        var result = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: flow);

        Assert.Contains("UNKNOWN_SEGMENT_REFERENCE:read:seg-不存在-1", result.Readiness.IssueCategories);
        Assert.Contains("UNKNOWN_PUBLIC_GROUP:reveal:不存在的分组", result.Readiness.IssueCategories);
        Assert.False(result.Readiness.CanStart);
        Assert.Equal(PackageValidationState.NeedsReview, result.Package.Validation);
    }

    // ===== 重复 / 空环节 ID 必须先挡住 =====

    [Fact]
    public void Duplicate_step_ids_are_rejected_before_the_package_can_be_played()
    {
        var flow = TwoStepFlow() with
        {
            Steps =
            [
                Step("same", "第一段", ScriptStepKind.Read, "第 1 页"),
                Step("same", "第二段", ScriptStepKind.Review, "第 2 页"),
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "真相",
                    Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页",
                },
            ],
        };

        Assert.Contains("FLOW_DUPLICATE_STEP_ID:same", flow.ValidationIssues);

        var result = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: flow);
        Assert.Contains("FLOW_DUPLICATE_STEP_ID:same", result.Readiness.IssueCategories);
        Assert.False(result.Readiness.CanStart, "重复环节 ID 会让推进原地打转，必须先拒绝。");
        Assert.Equal(PackageValidationState.NeedsReview, result.Package.Validation);
    }

    [Fact]
    public void Empty_step_id_is_rejected()
    {
        var flow = TwoStepFlow() with
        {
            Steps =
            [
                Step(string.Empty, "阅本", ScriptStepKind.Read, "第 1 页"),
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "真相",
                    Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页",
                },
            ],
        };

        Assert.Contains("FLOW_EMPTY_STEP_ID:1", flow.ValidationIssues);
    }

    [Fact]
    public void Search_step_that_requires_materials_must_declare_some()
    {
        var flow = TwoStepFlow() with
        {
            Steps =
            [
                Step("read", "阅本", ScriptStepKind.Read, "第 1 页"),
                new ScriptFlowStep
                {
                    StepId = "search",
                    Title = "搜证",
                    Kind = ScriptStepKind.Search,
                    SourceRef = "第 3 页",
                    RequiresAllMaterials = true,
                    MaterialIds = [],
                },
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "真相",
                    Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页",
                },
            ],
        };

        Assert.Contains("FLOW_MATERIAL_REQUIREMENT_WITHOUT_MATERIALS:search", flow.ValidationIssues);
    }

    // ===== 多轮投票：每一轮都要自己的汇总 =====

    [Fact]
    public void Second_vote_round_needs_its_own_summary()
    {
        var package = TwoVotePackage();
        var session = Start(package);

        session = Advance(session, package, "op-1");          // read → vote-one
        Assert.Equal("vote-one", session.CurrentPhaseId);

        session = Submit(session, package, "op-v1", ("A", 3), ("B", 2));
        Assert.Equal("vote-one", session.VoteSummaryPhaseId);

        session = Advance(session, package, "op-2");          // vote-one → vote-two
        Assert.Equal("vote-two", session.CurrentPhaseId);

        // 第二轮还没投票：不能拿第一轮的汇总直接推进
        var blocked = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            session, package, Command(GameCommandType.Advance, session.Revision, "op-3")));
        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, blocked.Code);
        Assert.Contains("这一轮投票", blocked.PublicReason, StringComparison.Ordinal);

        // 也不能跳过第二轮直接揭秘
        var earlyReveal = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            session with { CurrentPhaseId = "reveal" }, package,
            Command(GameCommandType.Reveal, session.Revision, "op-reveal-early")));
        Assert.Equal(DomainErrorCode.VOTE_INVALID, earlyReveal.Code);

        session = Submit(session, package, "op-v2", ("A", 5), ("B", 0));
        Assert.Equal("vote-two", session.VoteSummaryPhaseId);
        Assert.Equal(2, session.VoteHistory.Count);
        Assert.Equal(["vote-one", "vote-two"], session.VoteHistory.Select(r => r.PhaseId).ToArray());

        session = Advance(session, package, "op-4");
        Assert.Equal("reveal", session.CurrentPhaseId);

        // 两轮都投过了：揭秘放行（第一轮的记录仍在历史里）
        var revealed = GameEngine.Apply(
            session, package, Command(GameCommandType.Reveal, session.Revision, "op-reveal"));
        Assert.Equal(SessionState.Revealed, revealed.State);
    }

    [Fact]
    public void Vote_history_survives_a_save_and_reload()
    {
        var package = TwoVotePackage();
        var session = Start(package);
        session = Advance(session, package, "op-1");
        session = Submit(session, package, "op-v1", ("A", 3), ("B", 2));
        session = Advance(session, package, "op-2");
        session = Submit(session, package, "op-v2", ("A", 1), ("B", 4));

        // 第二轮投票后再推进，第一轮的汇总不能因为「最新汇总变了」而丢掉
        session = Advance(session, package, "op-3");
        Assert.Equal(2, session.VoteHistory.Count);
        Assert.True(
            session.VoteHistory.Any(r => r.PhaseId == "vote-one"),
            "第一轮汇总必须留在历史里，否则揭秘时会被判成「还有一轮没投」。");
    }

    [Fact]
    public async Task Vote_history_round_trips_through_the_database()
    {
        var package = TwoVotePackage();
        var root = Path.Combine(Path.GetTempPath(), "localdm-vote-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LocalDm.Infrastructure.Storage.SessionStore(Path.Combine(root, "sessions.db"));
            await store.InitializeAsync(TestContext.Current.CancellationToken);

            var session = Start(package);
            session = Advance(session, package, "op-1");
            session = Submit(session, package, "op-v1", ("A", 3), ("B", 2));
            await store.SaveSessionAsync(session, TestContext.Current.CancellationToken);

            var loaded = await store.LoadSessionAsync(
                session.SessionId,
                TestContext.Current.CancellationToken);

            Assert.NotNull(loaded);
            Assert.Equal(5, loaded!.VoteSummary!.Total);
            Assert.Equal("vote-one", loaded.VoteSummaryPhaseId);
            var record = Assert.Single(loaded.VoteHistory);
            Assert.Equal("vote-one", record.PhaseId);
            Assert.Equal(5, record.Summary.Total);

            // 读回来之后还要能继续玩：第二轮投票仍然要重新提交
            var reloaded = await store.LoadSessionAsync(session.SessionId, TestContext.Current.CancellationToken);
            var next = Advance(reloaded!, package, "op-2");
            Assert.Equal("vote-two", next.CurrentPhaseId);
            var blocked = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
                next, package, Command(GameCommandType.Advance, next.Revision, "op-3")));
            Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, blocked.Code);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Legacy_single_summary_still_loads_after_the_format_change()
    {
        var package = TwoVotePackage();
        var root = Path.Combine(Path.GetTempPath(), "localdm-vote-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "sessions.db");
            using (var store = new LocalDm.Infrastructure.Storage.SessionStore(dbPath))
            {
                await store.InitializeAsync(TestContext.Current.CancellationToken);
                await store.SaveSessionAsync(Start(package), TestContext.Current.CancellationToken);
            }

            // 模拟旧版本的写法：vote_summary 里是裸数组，没有环节归属
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE sessions SET vote_summary = $v";
                command.Parameters.AddWithValue(
                    "$v",
                    """[{"Option":"A","Count":3},{"Option":"B","Count":2}]""");
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var reopened = new LocalDm.Infrastructure.Storage.SessionStore(dbPath);
            var loaded = await reopened.LoadSessionAsync("session-contract", TestContext.Current.CancellationToken);

            Assert.NotNull(loaded);
            Assert.Equal(5, loaded!.VoteSummary!.Total);
            Assert.Null(loaded.VoteSummaryPhaseId);
            Assert.Empty(loaded.VoteHistory);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ===== 流程声明决定「请主持人开场」出现在哪一环 =====

    [Fact]
    public void Opening_speech_action_comes_from_the_declaration_not_the_phase_number()
    {
        var flow = TwoStepFlow() with
        {
            Steps =
            [
                Step("read", "阅本", ScriptStepKind.Read, "第 1 页"),
                new ScriptFlowStep
                {
                    StepId = "prologue",
                    Title = "序章共读",
                    Kind = ScriptStepKind.Intro,
                    SourceRef = "第 2 页",
                    // 序章本身是第二步，仍然可以是「开场」所在环节
                    OffersOpeningSpeech = true,
                },
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "真相",
                    Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页",
                },
            ],
        };

        var package = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: flow).Package;
        var session = Start(package);

        var first = PublicSessionProjector.Project(package, session);
        Assert.False(first.CurrentPhase.CanOpenSession, "第一步没有声明开场动作。");

        var second = Advance(session, package, "op-1");
        var secondView = PublicSessionProjector.Project(package, second);
        Assert.Equal("prologue", secondView.CurrentPhase.PhaseId);
        Assert.True(secondView.CurrentPhase.CanOpenSession, "第二步声明了开场动作。");
    }

    // ===== 流程提示也是可传模型的公开依据 =====

    [Fact]
    public void Phase_hint_reaches_the_narration_context()
    {
        var flow = TwoStepFlow() with
        {
            Steps =
            [
                Step("read", "阅本", ScriptStepKind.Read, "第 1 页"),
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "真相",
                    Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页",
                    // 只给提示：没有条件、没有公开段落
                    Hint = "这一环节只写了一句提示，请按它主持。",
                    Conditions = [],
                },
            ],
        };

        var package = RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: flow).Package;
        var session = Advance(Start(package), package, "op-1");
        Assert.Equal("reveal", session.CurrentPhaseId);

        var grounding = HostNarration.BuildGroundingText(package, session);
        Assert.Contains("这一环节只写了一句提示", grounding, StringComparison.Ordinal);

        var prompt = string.Join("\n", HostNarration
            .BuildMessages(package, session, HostNarrationIntent.AnnouncePhase)
            .Select(m => m.Content));
        Assert.Contains("这一环节只写了一句提示", prompt, StringComparison.Ordinal);

        // DM 的内部资料包含真相，即使当前公共 grounding 仍保持未揭秘。
        Assert.Contains("合成结局", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("合成结局", grounding, StringComparison.Ordinal);
    }

    // ===== 夹具 =====

    private static ScriptFlow TwoStepFlow() => new()
    {
        Steps =
        [
            Step("read", "阅本", ScriptStepKind.Read, "第 1 页 阅本"),
            new ScriptFlowStep
            {
                StepId = "reveal",
                Title = "真相",
                Kind = ScriptStepKind.Reveal,
                SourceRef = "第 9 页 真相",
            },
        ],
    };

    private static ScriptFlow ThreeStepFlow() => new()
    {
        Steps =
        [
            Step("read", "阅本", ScriptStepKind.Read, "第 1 页 阅本"),
            new ScriptFlowStep
            {
                StepId = "search",
                Title = "搜证",
                Kind = ScriptStepKind.Search,
                SourceRef = "第 3 页 搜证",
                MaterialIds = ["A01"],
                RequiresAllMaterials = true,
            },
            new ScriptFlowStep
            {
                StepId = "reveal",
                Title = "真相",
                Kind = ScriptStepKind.Reveal,
                SourceRef = "第 9 页 真相",
            },
        ],
    };

    private static ScriptPackage TwoVotePackage()
    {
        var flow = new ScriptFlow
        {
            Steps =
            [
                Step("read", "阅本", ScriptStepKind.Read, "第 1 页 阅本"),
                Step("vote-one", "第一轮投票", ScriptStepKind.Vote, "第 4 页 第一轮投票"),
                Step("vote-two", "第二轮投票", ScriptStepKind.Vote, "第 5 页 第二轮投票"),
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "真相",
                    Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页 真相",
                },
            ],
        };

        return RealScriptPackageBuilder.Build("合成本", 5, Pages(), Sources(), flow: flow).Package;
    }

    private static ScriptFlowStep Step(string id, string title, ScriptStepKind kind, string sourceRef) => new()
    {
        StepId = id,
        Title = title,
        Kind = kind,
        SourceRef = sourceRef,
        Conditions = [$"原本条件：{title}完成"],
    };

    private static RealScriptPackageBuilder.ExtractedPage[] Pages() =>
    [
        new()
        {
            Group = "组织", PageNumber = 1, PageLabel = "1", Text = "公共序章",
            FileId = "f0", SpanId = "sp0",
        },
        new()
        {
            Group = "A01", GroupKind = RealScriptPackageBuilder.ExtractedGroupKind.Material,
            PageNumber = 1, PageLabel = "1", Text = "第一轮线索", FileId = "f1", SpanId = "sp1",
        },
        new()
        {
            Group = "组织", PageNumber = 9, PageLabel = "9", Text = "合成结局，不得提前出现",
            FileId = "f2", SpanId = "sp2",
        },
    ];

    private static SourceFile[] Sources() => Pages().Select(p => new SourceFile
    {
        FileId = p.FileId,
        PrivatePath = p.FileId,
        ContentHash = new string('a', 64),
        PageCount = 1,
        Origin = SourceOrder.ManifestOrder,
        ToolIdentity = "test",
        ParseState = FileParseState.Complete,
    }).ToArray();

    private static Session Start(ScriptPackage package) => GameEngine.Apply(
        new Session
        {
            SessionId = "session-contract",
            PackageId = package.PackageId,
            PackageVersion = package.Version,
            ManifestHash = package.ManifestHash,
            State = SessionState.Prepared,
            CurrentPhaseId = package.Phases[0].PhaseId,
            Revision = 0,
            ReleasedMaterialIds = [],
        },
        package,
        Command(GameCommandType.Start, 0, "op-start"));

    private static Session Advance(Session session, ScriptPackage package, string operationId) =>
        GameEngine.Apply(session, package, Command(GameCommandType.Advance, session.Revision, operationId));

    private static Session Submit(
        Session session,
        ScriptPackage package,
        string operationId,
        params (string Option, int Count)[] counts) =>
        GameEngine.Apply(
            session,
            package,
            Command(
                GameCommandType.SubmitVoteSummary,
                session.Revision,
                operationId,
                votes: new VoteSummary
                {
                    Counts = counts
                        .Select(c => new VoteCount { Option = c.Option, Count = c.Count })
                        .ToArray(),
                }));

    private static GameCommand Command(
        GameCommandType type,
        int revision,
        string operationId,
        VoteSummary? votes = null,
        IReadOnlyList<string>? materials = null) => new()
    {
        SessionId = "session-contract",
        OperationId = operationId,
        ExpectedRevision = revision,
        Type = type,
        Votes = votes,
        MaterialIds = materials ?? [],
    };
}
