using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Import;

namespace LocalDm.Tests;

/// <summary>T038：未确认/原本条件/提前揭秘/私密投影。</summary>
public sealed class PhaseFlowTests
{
    private static readonly ScriptPackage Package = SyntheticPackage.Create();

    [Fact]
    public void RealScriptPackageBuilder_separates_roles_and_materials()
    {
        var pages = new[]
        {
            new RealScriptPackageBuilder.ExtractedPage { Group = "组织", PageNumber = 1, PageLabel = "1", Text = "开场信", FileId = "f0", SpanId = "sp0" },
            new RealScriptPackageBuilder.ExtractedPage { Group = "海", PageNumber = 1, PageLabel = "1", Text = "海角色本", FileId = "f1", SpanId = "sp1" },
            new RealScriptPackageBuilder.ExtractedPage { Group = "唐", PageNumber = 1, PageLabel = "1", Text = "唐角色本", FileId = "f2", SpanId = "sp2" },
            new RealScriptPackageBuilder.ExtractedPage { Group = "1楼51-55", PageNumber = 1, PageLabel = "51", Text = "1楼线索", FileId = "f3", SpanId = "sp3" },
            new RealScriptPackageBuilder.ExtractedPage { Group = "现场30-35", PageNumber = 1, PageLabel = "30", Text = "现场线索", FileId = "f4", SpanId = "sp4" },
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

        var flow = TestScriptFlow.SingleRoundWithVote("1楼51-55", "现场30-35");
        var result = RealScriptPackageBuilder.Build("测试剧本", 5, pages, sources, flow: flow);
        var pkg = result.Package;

        Assert.Equal(2, pkg.Roles.Count);
        Assert.Contains(pkg.Roles, r => r.RoleId == "海");
        Assert.Contains(pkg.Roles, r => r.RoleId == "唐");

        Assert.Equal(2, pkg.Materials.Count);
        Assert.Contains(pkg.Materials, m => m.MaterialId == "1楼51-55");
        Assert.Contains(pkg.Materials, m => m.MaterialId == "现场30-35");

        // 阶段来自流程声明，不是固定的七步模板。
        Assert.Equal(5, pkg.Phases.Count);
        Assert.Equal(
            ["read", "search", "talk", "vote", "reveal"],
            pkg.Phases.Select(p => p.PhaseId).ToArray());

        var searchPhase = pkg.Phases.First(p => p.PhaseId == "search");
        Assert.Contains(PublicAction.ReleaseMaterials, searchPhase.AllowedActions);
        Assert.Equal(2, searchPhase.MaterialIds.Count);
        Assert.True(searchPhase.RequiresAllMaterials, "本轮材料必须发完才能推进。");
        Assert.Equal("进入讨论", searchPhase.AdvanceLabel);

        // 第一个环节挂上公开段落，后面的环节没有声明就保持为空，不猜原文。
        Assert.Equal(["seg-组织-1"], pkg.Phases[0].PublicSegmentIds);
        Assert.Empty(searchPhase.PublicSegmentIds);

        Assert.True(result.FlowDeclared);
        Assert.Empty(result.StepsNeedingReview);
        Assert.Equal(PackageValidationState.Ready, pkg.Validation);
    }

    [Fact]
    public void RealScriptPackageBuilder_without_a_declared_flow_cannot_start()
    {
        // 没有流程声明时不编造默认流程：只生成一个「流程待确认」阶段，整包停在 NeedsReview。
        var pages = new[]
        {
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "组织", PageNumber = 1, PageLabel = "1", Text = "开场信", FileId = "f0", SpanId = "sp0",
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

        var result = RealScriptPackageBuilder.Build("测试剧本", 5, pages, sources);

        Assert.False(result.FlowDeclared);
        Assert.Contains("FLOW_NOT_DECLARED", result.Readiness.IssueCategories);
        Assert.False(result.Readiness.CanStart);
        Assert.Equal(PackageValidationState.NeedsReview, result.Package.Validation);

        var only = Assert.Single(result.Package.Phases);
        Assert.DoesNotContain(PublicAction.Start, only.AllowedActions);
    }

    [Fact]
    public void RealScriptPackageBuilder_marks_steps_without_a_source_as_needing_review()
    {
        var pages = new[]
        {
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "组织", PageNumber = 1, PageLabel = "1", Text = "开场信", FileId = "f0", SpanId = "sp0",
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

        var result = RealScriptPackageBuilder.Build(
            "测试剧本", 5, pages, sources, flow: TestScriptFlow.WithUnreviewedStep());

        Assert.True(result.FlowDeclared);
        Assert.Contains("第二轮", result.StepsNeedingReview);
        Assert.Contains("FLOW_STEP_NEEDS_REVIEW", result.Readiness.IssueCategories);
        Assert.False(result.Readiness.CanStart);
        Assert.Equal(PackageValidationState.NeedsReview, result.Package.Validation);
    }

    [Fact]
    public void Start_is_one_click_without_any_confirmation_field()
    {
        // 用户决定：点击「开始这一局」本身就是确认。命令里没有「大家准备好了」这个字段，
        // 也不再有单独的确认检查；真实条件仍然是「剧本包 Ready + 状态为待开局」。
        var next = GameEngine.Apply(
            SyntheticPackage.NewSession(),
            Package,
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1"));

        Assert.Equal(SessionState.Playing, next.State);
        Assert.Equal("phase-read", next.CurrentPhaseId);
    }

    [Fact]
    public void Start_moves_to_first_phase_and_bumps_revision()
    {
        var next = GameEngine.Apply(
            SyntheticPackage.NewSession(),
            Package,
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1"));

        Assert.Equal(SessionState.Playing, next.State);
        Assert.Equal("phase-read", next.CurrentPhaseId);
        Assert.Equal(1, next.Revision);
    }

    [Fact]
    public void Advance_moves_one_step_without_any_confirmation_flag()
    {
        // 推进同样是一次点击：没有额外勾选，也不需要第二次确认。
        var playing = Started();

        var next = GameEngine.Apply(
            playing,
            Package,
            SyntheticPackage.Command(GameCommandType.Advance, 1, "op-2"));

        Assert.Equal("phase-search", next.CurrentPhaseId);
        Assert.Equal(2, next.Revision);
    }

    [Fact]
    public void Advance_on_phase_without_advance_permission_is_rejected()
    {
        // 投票阶段本身允许 Advance，但需要有合法汇总
        var vote = AdvancedTo(4);

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            vote,
            Package,
            SyntheticPackage.Command(GameCommandType.Advance, vote.Revision, "op-x")));

        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, ex.Code);
    }

    [Fact]
    public void Early_reveal_is_rejected()
    {
        var playing = Started();

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            playing,
            Package,
            SyntheticPackage.Command(GameCommandType.Reveal, playing.Revision, "op-early")));

        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, ex.Code);
    }

    [Fact]
    public void Reveal_in_the_reveal_phase_needs_no_second_confirmation()
    {
        var revealPhase = AdvancedTo(5);

        // 点击「公开真相」就是确认：命令里没有第二个确认字段，也不弹第二次确认。
        var revealed = GameEngine.Apply(
            revealPhase,
            Package,
            SyntheticPackage.Command(GameCommandType.Reveal, revealPhase.Revision, "op-reveal"));

        Assert.Equal(SessionState.Revealed, revealed.State);
    }

    [Fact]
    public void Reveal_without_vote_summary_is_rejected()
    {
        // 直接推进到揭秘阶段（绕过投票阶段的 Advance 校验），规则核心仍要拦得住。
        var playing = Started();
        var revealPhase = playing with { CurrentPhaseId = "phase-reveal", Revision = 3 };

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            revealPhase,
            Package,
            SyntheticPackage.Command(GameCommandType.Reveal, 3, "op-reveal")));

        Assert.Equal(DomainErrorCode.VOTE_INVALID, ex.Code);
    }

    [Fact]
    public void Vote_summary_needs_no_confirmation_but_total_must_match()
    {
        var votePhase = AdvancedTo(4);

        // 票数不合法：仍然被拒（这是机器能验证的条件，不靠人再确认一次）。
        var wrongTotal = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            votePhase,
            Package,
            SyntheticPackage.Command(
                GameCommandType.SubmitVoteSummary,
                votePhase.Revision,
                "op-vote2",
                votes: Votes(("A", 3), ("B", 1)))));
        Assert.Equal(DomainErrorCode.VOTE_INVALID, wrongTotal.Code);

        // 合法汇总：直接生效，没有额外的「已核对」勾选。
        var ok = GameEngine.Apply(
            votePhase,
            Package,
            SyntheticPackage.Command(
                GameCommandType.SubmitVoteSummary,
                votePhase.Revision,
                "op-vote3",
                votes: Votes(("A", 3), ("B", 2))));
        Assert.Equal(5, ok.VoteSummary!.Total);
    }

    [Fact]
    public void Negative_vote_count_is_rejected()
    {
        var votePhase = AdvancedTo(4);

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            votePhase,
            Package,
            SyntheticPackage.Command(
                GameCommandType.SubmitVoteSummary,
                votePhase.Revision,
                "op-neg",
                votes: Votes(("A", 6), ("B", -1)))));

        Assert.Equal(DomainErrorCode.VOTE_INVALID, ex.Code);
    }

    [Fact]
    public void Release_material_must_be_allowed_by_current_phase()
    {
        var search = AdvancedTo(2);
        // 进入允许手动发放材料的搜证环节时，材料不会被自动释放
        Assert.DoesNotContain("M1", search.ReleasedMaterialIds);
        Assert.DoesNotContain("M2", search.ReleasedMaterialIds);

        var ok = GameEngine.Apply(
            search,
            Package,
            SyntheticPackage.Command(GameCommandType.ReleaseMaterials, search.Revision, "op-mat", materials: ["M1"]));
        Assert.Contains("M1", ok.ReleasedMaterialIds);
        Assert.DoesNotContain("M2", ok.ReleasedMaterialIds);

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            search,
            Package,
            SyntheticPackage.Command(GameCommandType.ReleaseMaterials, search.Revision, "op-mat2", materials: ["M9"])));
        Assert.Equal(DomainErrorCode.INVALID_INPUT, ex.Code);
    }

    [Fact]
    public void Public_projection_never_contains_private_or_truth_segments()
    {
        var playing = Started();
        var projection = PublicSessionProjector.Project(Package, playing);

        Assert.DoesNotContain("seg-secret", projection.CurrentPhase.PublicSegmentIds);
        Assert.DoesNotContain("seg-truth", projection.CurrentPhase.PublicSegmentIds);
        Assert.Null(projection.RevealedTexts);

        var serialized = System.Text.Json.JsonSerializer.Serialize(projection);
        Assert.DoesNotContain(SyntheticPackage.PublicTruth, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("合成角色的秘密", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("truth", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("murderer", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateStory", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Revealed_projection_contains_truth_only_after_unlock()
    {
        var revealed = RevealPhase();

        var projection = PublicSessionProjector.Project(Package, revealed);

        Assert.Equal(SessionState.Revealed.ToString(), projection.State);
        Assert.NotNull(projection.RevealedTexts);
        Assert.Contains(SyntheticPackage.PublicTruth, projection.RevealedTexts!);
    }

    [Fact]
    public void Pause_then_resume_restores_previous_state_without_overwriting_reveal()
    {
        var revealed = RevealPhase();

        var paused = GameEngine.Apply(revealed, Package, SyntheticPackage.Command(GameCommandType.Pause, revealed.Revision, "op-pause"));
        Assert.Equal(SessionState.Paused, paused.State);
        Assert.Equal(SessionState.Revealed, paused.ResumeState);

        var resumed = GameEngine.Apply(paused, Package, SyntheticPackage.Command(GameCommandType.Resume, paused.Revision, "op-resume"));
        Assert.Equal(SessionState.Revealed, resumed.State);
        Assert.Null(resumed.ResumeState);
    }

    [Fact]
    public void Package_mismatch_blocks_any_command()
    {
        var foreign = SyntheticPackage.NewSession() with { ManifestHash = new string('c', 64) };

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            foreign,
            Package,
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1")));

        Assert.Equal(DomainErrorCode.PACKAGE_MISMATCH, ex.Code);
    }

    [Fact]
    public void Not_ready_package_cannot_start()
    {
        var needsReview = Package with { Validation = PackageValidationState.NeedsReview };

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            SyntheticPackage.NewSession(),
            needsReview,
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1")));

        Assert.Equal(DomainErrorCode.NOT_READY, ex.Code);
    }

    internal static Session Started() => GameEngine.Apply(
        SyntheticPackage.NewSession(),
        Package,
        SyntheticPackage.Command(GameCommandType.Start, 0, "op-start"));

    /// <summary>推进到指定阶段序号（1..5）；经过投票阶段时先提交合法汇总。</summary>
    internal static Session AdvancedTo(int phaseIndex)
    {
        var session = Started();
        for (var i = 1; i < phaseIndex; i++)
        {
            if (session.CurrentPhaseId == "phase-vote" && session.VoteSummary is null)
            {
                session = GameEngine.Apply(
                    session,
                    Package,
                    SyntheticPackage.Command(
                        GameCommandType.SubmitVoteSummary,
                        session.Revision,
                        $"op-vote-auto-{i}",
                        votes: Votes(("A", 3), ("B", 2))));
            }

            session = GameEngine.Apply(
                session,
                Package,
                SyntheticPackage.Command(GameCommandType.Advance, session.Revision, $"op-adv-{i}"));
        }

        return session;
    }

    internal static Session RevealPhase()
    {
        var session = AdvancedTo(5);
        return GameEngine.Apply(
            session,
            Package,
            SyntheticPackage.Command(GameCommandType.Reveal, session.Revision, "op-reveal-final"));
    }

    [Fact]
    public void Complete_moves_to_review_phase_when_present()
    {
        var phasesWithReview = Package.Phases.Concat([
            new ScriptPhase
            {
                PhaseId = "phase-review",
                Title = "复盘",
                PublicSegmentIds = [],
                AllowedActions = [PublicAction.Advance],
                Conditions = ["复盘说明"],
                MaterialIds = [],
                IsVotePhase = false,
                IsRevealPhase = false,
                SourceSpanIds = [],
            }
        ]).ToArray();
        var packageWithReview = Package with { Phases = phasesWithReview };

        var revealed = RevealPhase();
        var completed = GameEngine.Apply(
            revealed,
            packageWithReview,
            SyntheticPackage.Command(GameCommandType.Complete, revealed.Revision, "op-comp"));

        Assert.Equal(SessionState.Completed, completed.State);
        Assert.Equal("phase-review", completed.CurrentPhaseId);
    }

    [Fact]
    public void Revealed_state_grounds_truth_text_in_narration()
    {
        var revealed = RevealPhase();
        var messages = HostNarration.BuildMessages(Package, revealed, HostNarrationIntent.RevealTruth);
        var prompt = string.Join("\n", messages.Select(m => m.Content));

        Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepared_state_includes_start_in_allowed_actions()
    {
        var session = SyntheticPackage.NewSession();
        var projection = PublicSessionProjector.Project(Package, session);

        Assert.Contains(PublicAction.Start, projection.CurrentPhase.AllowedActions);
    }

    internal static VoteSummary Votes(params (string Option, int Count)[] counts) => new()
    {
        Counts = counts.Select(c => new VoteCount { Option = c.Option, Count = c.Count }).ToArray(),
    };

    // ===== 秘密隔离的负向覆盖 =====
    // 只断言「已揭秘时真相出现」是不够的：真正要锁住的是
    // 「没揭秘时真相不得出现」「已揭秘时私有段仍不得出现」。

    private const string PrivateText = "这是合成角色的秘密，公共界面不应出现。";

    [Fact]
    public void Truth_is_available_to_dm_before_reveal_but_role_book_is_not_and_public_grounding_stays_filtered()
    {
        var playing = Started();

        var prompt = string.Join("\n", HostNarration
            .BuildMessages(Package, playing, HostNarrationIntent.AnnouncePhase)
            .Select(m => m.Content));
        var grounding = HostNarration.BuildGroundingText(Package, playing);

        Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPackage.PublicTruth, grounding, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateText, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Role_book_stays_out_of_the_dm_context_after_reveal()
    {
        var revealed = RevealPhase();

        var prompt = string.Join("\n", HostNarration
            .BuildMessages(Package, revealed, HostNarrationIntent.RevealTruth)
            .Select(m => m.Content));
        var grounding = HostNarration.BuildGroundingText(Package, revealed);

        Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
        // 揭秘之后角色本照样不给 DM：玩家手里的东西始终不给。
        Assert.DoesNotContain(PrivateText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateText, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Truth_stays_unlocked_while_paused_in_reveal_phase()
    {
        // 引擎的 Complete 把「暂停且 ResumeState = Revealed」视为已揭秘；
        // 口播依据必须用同一套判定，否则主持人一按暂停真相就消失了。
        var revealed = RevealPhase();
        var paused = GameEngine.Apply(
            revealed, Package, SyntheticPackage.Command(GameCommandType.Pause, revealed.Revision, "op-pause-reveal"));

        Assert.Equal(SessionState.Paused, paused.State);
        Assert.Equal(SessionState.Revealed, paused.ResumeState);

        var grounding = HostNarration.BuildGroundingText(Package, paused);
        Assert.Contains(SyntheticPackage.PublicTruth, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Grounding_text_lists_released_material_ids_only()
    {
        var search = AdvancedTo(2);
        Assert.DoesNotContain("M1", HostNarration.BuildGroundingText(Package, search), StringComparison.Ordinal);

        var released = GameEngine.Apply(
            search, Package, SyntheticPackage.Command(GameCommandType.ReleaseMaterials, search.Revision, "op-mat", materials: ["M1"]));

        var grounding = HostNarration.BuildGroundingText(Package, released);
        Assert.Contains("M1", grounding, StringComparison.Ordinal);
        Assert.DoesNotContain("M2", grounding, StringComparison.Ordinal);
    }

    // ===== 阶段推进的材料规则 =====

    [Fact]
    public void Advance_releases_materials_left_pending_in_the_phase_being_left()
    {
        // 允许手动发放的阶段，进入时不整批并入；但离开时必须补齐，
        // 否则主持人跳过环节后这些线索就永久无处可发。
        var afterSearch = AdvancedTo(3);

        Assert.Contains("M1", afterSearch.ReleasedMaterialIds);
        Assert.Contains("M2", afterSearch.ReleasedMaterialIds);
    }

    [Fact]
    public void Advance_still_auto_releases_materials_of_a_phase_without_manual_release()
    {
        // 下一阶段有材料但不允许手动发放（例如被动随阶段公开的材料包）→ 进入时自动释放。
        var phases = Package.Phases.Select(p =>
            p.PhaseId == "phase-discuss"
                ? p with { MaterialIds = ["M9"] }
                : p).ToArray();
        var package = Package with { Phases = phases };

        var session = Started();
        session = GameEngine.Apply(session, package,
            SyntheticPackage.Command(GameCommandType.Advance, session.Revision, "op-adv-read"));
        session = GameEngine.Apply(session, package,
            SyntheticPackage.Command(GameCommandType.Advance, session.Revision, "op-adv-search"));

        Assert.Equal("phase-discuss", session.CurrentPhaseId);
        Assert.Contains("M9", session.ReleasedMaterialIds);
    }

    [Fact]
    public void Complete_keeps_current_phase_when_the_next_phase_is_not_terminal()
    {
        // 真相之后还有两个阶段时，Complete 不能指向中间那个阶段：
        // 复盘是终态，指向中间阶段会让公开投影按它的标题渲染，内容错位。
        var phases = Package.Phases.Concat([
            new ScriptPhase
            {
                PhaseId = "phase-extra",
                Title = "额外环节",
                PublicSegmentIds = [],
                AllowedActions = [PublicAction.Advance],
                Conditions = ["额外环节说明"],
                MaterialIds = [],
                IsVotePhase = false,
                IsRevealPhase = false,
                SourceSpanIds = [],
            },
            new ScriptPhase
            {
                PhaseId = "phase-final",
                Title = "收尾",
                PublicSegmentIds = [],
                AllowedActions = [PublicAction.Advance],
                Conditions = ["收尾说明"],
                MaterialIds = [],
                IsVotePhase = false,
                IsRevealPhase = false,
                SourceSpanIds = [],
            }
        ]).ToArray();
        var package = Package with { Phases = phases };

        var revealed = RevealPhase();
        var completed = GameEngine.Apply(
            revealed, package, SyntheticPackage.Command(GameCommandType.Complete, revealed.Revision, "op-comp-mid"));

        Assert.Equal(SessionState.Completed, completed.State);
        Assert.Equal("phase-reveal", completed.CurrentPhaseId);
    }

    // ===== 角色本 / 线索卡分流 =====

    [Fact]
    public void RealScriptPackageBuilder_trusts_the_imported_group_kind_over_name_heuristics()
    {
        // 「NPC-A」按目录名会被当成线索卡堆，但它其实是角色本。
        // 导入源头知道真相时必须采信源头：否则私有角色本的分组名会进公共材料编号。
        var pages = new[]
        {
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "NPC-A",
                GroupKind = RealScriptPackageBuilder.ExtractedGroupKind.Role,
                PageNumber = 1, PageLabel = "1", Text = "角色本正文", FileId = "f1", SpanId = "sp1",
            },
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "第二轮",
                GroupKind = RealScriptPackageBuilder.ExtractedGroupKind.Material,
                PageNumber = 1, PageLabel = "1", Text = "线索正文", FileId = "f2", SpanId = "sp2",
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

        var flow = TestScriptFlow.SingleRoundWithVote("第二轮");
        var pkg = RealScriptPackageBuilder.Build("测试剧本", 5, pages, sources, flow: flow).Package;

        Assert.Contains(pkg.Roles, r => r.RoleId == "NPC-A");
        Assert.DoesNotContain(pkg.Materials, m => m.MaterialId == "NPC-A");
        Assert.Contains(pkg.Materials, m => m.MaterialId == "第二轮");

        var searchPhase = pkg.Phases.First(p => p.PhaseId == "search");
        Assert.DoesNotContain("NPC-A", searchPhase.MaterialIds);
        Assert.Contains("第二轮", searchPhase.MaterialIds);
    }

    [Fact]
    public void RealScriptPackageBuilder_keeps_release_action_when_no_material_group_found()
    {
        // 一个线索组都没识别出来时，搜证阶段仍保留 ReleaseMaterials：
        // 主持人按下只会看到「没有待发放的编号」，比悄悄拿掉动作更容易判断问题。
        var pages = new[]
        {
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "海",
                GroupKind = RealScriptPackageBuilder.ExtractedGroupKind.Role,
                PageNumber = 1, PageLabel = "1", Text = "角色本正文", FileId = "f1", SpanId = "sp1",
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

        var result = RealScriptPackageBuilder.Build(
            "测试剧本", 5, pages, sources, flow: TestScriptFlow.SingleRoundWithVote());

        Assert.Equal(0, result.MaterialGroupCount);
        var searchPhase = result.Package.Phases.First(p => p.PhaseId == "search");
        Assert.Contains(PublicAction.ReleaseMaterials, searchPhase.AllowedActions);
        Assert.Empty(searchPhase.MaterialIds);
    }
    [Fact]
    public void Revert_moves_back_one_phase_and_keeps_released_materials()
    {
        var current = AdvancedTo(3);
        var reverted = GameEngine.Apply(current, Package,
            SyntheticPackage.Command(GameCommandType.RevertPhase, current.Revision, "op-revert"));

        Assert.Equal("phase-search", reverted.CurrentPhaseId);
        Assert.Equal(current.Revision + 1, reverted.Revision);
        Assert.Equal(current.ReleasedMaterialIds, reverted.ReleasedMaterialIds);
    }

    [Fact]
    public void Revert_on_first_phase_is_rejected()
    {
        var current = Started();
        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(current, Package,
            SyntheticPackage.Command(GameCommandType.RevertPhase, current.Revision, "op-revert-first")));

        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, ex.Code);
    }

    [Fact]
    public void Revert_after_reveal_is_rejected()
    {
        var current = RevealPhase();
        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(current, Package,
            SyntheticPackage.Command(GameCommandType.RevertPhase, current.Revision, "op-revert-revealed")));

        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, ex.Code);
    }

    [Fact]
    public void Revert_while_paused_is_rejected()
    {
        var current = AdvancedTo(2);
        var paused = GameEngine.Apply(current, Package,
            SyntheticPackage.Command(GameCommandType.Pause, current.Revision, "op-pause-revert"));
        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(paused, Package,
            SyntheticPackage.Command(GameCommandType.RevertPhase, paused.Revision, "op-revert-paused")));

        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, ex.Code);
    }

    [Fact]
    public void Resubmitting_votes_in_the_same_phase_replaces_that_round()
    {
        var current = AdvancedTo(4);
        current = GameEngine.Apply(current, Package,
            SyntheticPackage.Command(GameCommandType.SubmitVoteSummary, current.Revision, "op-first-votes",
                votes: Votes(("A", 3), ("B", 2))));
        var corrected = GameEngine.Apply(current, Package,
            SyntheticPackage.Command(GameCommandType.SubmitVoteSummary, current.Revision, "op-correct-votes",
                votes: Votes(("A", 2), ("B", 3))));

        var record = Assert.Single(corrected.VoteHistory);
        Assert.Equal("phase-vote", record.PhaseId);
        Assert.Equal(new[] { ("A", 2), ("B", 3) }, record.Summary.Counts.Select(c => (c.Option, c.Count)));
        Assert.Equal(new[] { ("A", 2), ("B", 3) }, corrected.VoteSummary!.Counts.Select(c => (c.Option, c.Count)));
    }
}
