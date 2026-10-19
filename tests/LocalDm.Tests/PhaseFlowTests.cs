using LocalDm.Core.Domain;
using LocalDm.Core.Game;

namespace LocalDm.Tests;

/// <summary>T038：未确认/原本条件/提前揭秘/私密投影。</summary>
public sealed class PhaseFlowTests
{
    private static readonly ScriptPackage Package = SyntheticPackage.Create();

    [Fact]
    public void Start_requires_group_ready()
    {
        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            SyntheticPackage.NewSession(),
            Package,
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1")));

        Assert.Equal(DomainErrorCode.GROUP_NOT_READY, ex.Code);
    }

    [Fact]
    public void Start_moves_to_first_phase_and_bumps_revision()
    {
        var next = GameEngine.Apply(
            SyntheticPackage.NewSession(),
            Package,
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1", groupReady: true));

        Assert.Equal(SessionState.Playing, next.State);
        Assert.Equal("phase-read", next.CurrentPhaseId);
        Assert.Equal(1, next.Revision);
    }

    [Fact]
    public void Advance_without_group_ready_keeps_current_phase()
    {
        var playing = Started();

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            playing,
            Package,
            SyntheticPackage.Command(GameCommandType.Advance, 1, "op-2")));

        Assert.Equal(DomainErrorCode.GROUP_NOT_READY, ex.Code);
    }

    [Fact]
    public void Advance_goes_to_exactly_the_next_allowed_phase()
    {
        var playing = Started();

        var next = GameEngine.Apply(
            playing,
            Package,
            SyntheticPackage.Command(GameCommandType.Advance, 1, "op-2", groupReady: true));

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
            SyntheticPackage.Command(GameCommandType.Advance, vote.Revision, "op-x", groupReady: true)));

        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, ex.Code);
    }

    [Fact]
    public void Early_reveal_is_rejected()
    {
        var playing = Started();

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            playing,
            Package,
            SyntheticPackage.Command(GameCommandType.Reveal, playing.Revision, "op-early", confirmed: true)));

        Assert.Equal(DomainErrorCode.PHASE_CONDITION_UNMET, ex.Code);
    }

    [Fact]
    public void Reveal_requires_confirmation()
    {
        var revealPhase = AdvancedTo(5);

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            revealPhase,
            Package,
            SyntheticPackage.Command(GameCommandType.Reveal, revealPhase.Revision, "op-reveal", confirmed: false)));

        Assert.Equal(DomainErrorCode.CONFIRMATION_REQUIRED, ex.Code);
    }

    [Fact]
    public void Reveal_without_vote_summary_is_rejected_even_when_confirmed()
    {
        // 直接推进到揭秘阶段（绕过投票阶段的 Advance 校验），确认规则核心仍拦得住
        var playing = Started();
        var revealPhase = playing with { CurrentPhaseId = "phase-reveal", Revision = 3 };

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            revealPhase,
            Package,
            SyntheticPackage.Command(GameCommandType.Reveal, 3, "op-reveal", confirmed: true)));

        Assert.Equal(DomainErrorCode.VOTE_INVALID, ex.Code);
    }

    [Fact]
    public void Vote_submission_requires_confirmation_and_valid_total()
    {
        var votePhase = AdvancedTo(4);

        var noConfirm = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            votePhase,
            Package,
            SyntheticPackage.Command(
                GameCommandType.SubmitVoteSummary,
                votePhase.Revision,
                "op-vote",
                votes: Votes(("A", 3), ("B", 2)))));
        Assert.Equal(DomainErrorCode.CONFIRMATION_REQUIRED, noConfirm.Code);

        var wrongTotal = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            votePhase,
            Package,
            SyntheticPackage.Command(
                GameCommandType.SubmitVoteSummary,
                votePhase.Revision,
                "op-vote2",
                confirmed: true,
                votes: Votes(("A", 3), ("B", 1)))));
        Assert.Equal(DomainErrorCode.VOTE_INVALID, wrongTotal.Code);

        var ok = GameEngine.Apply(
            votePhase,
            Package,
            SyntheticPackage.Command(
                GameCommandType.SubmitVoteSummary,
                votePhase.Revision,
                "op-vote3",
                confirmed: true,
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
                confirmed: true,
                votes: Votes(("A", 6), ("B", -1)))));

        Assert.Equal(DomainErrorCode.VOTE_INVALID, ex.Code);
    }

    [Fact]
    public void Release_material_must_be_allowed_by_current_phase()
    {
        var search = AdvancedTo(2);

        var ok = GameEngine.Apply(
            search,
            Package,
            SyntheticPackage.Command(GameCommandType.ReleaseMaterials, search.Revision, "op-mat", materials: ["M1"]));
        Assert.Contains("M1", ok.ReleasedMaterialIds);

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
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1", groupReady: true)));

        Assert.Equal(DomainErrorCode.PACKAGE_MISMATCH, ex.Code);
    }

    [Fact]
    public void Not_ready_package_cannot_start()
    {
        var needsReview = Package with { Validation = PackageValidationState.NeedsReview };

        var ex = Assert.Throws<DomainRuleException>(() => GameEngine.Apply(
            SyntheticPackage.NewSession(),
            needsReview,
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1", groupReady: true)));

        Assert.Equal(DomainErrorCode.NOT_READY, ex.Code);
    }

    internal static Session Started() => GameEngine.Apply(
        SyntheticPackage.NewSession(),
        Package,
        SyntheticPackage.Command(GameCommandType.Start, 0, "op-start", groupReady: true));

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
                        confirmed: true,
                        votes: Votes(("A", 3), ("B", 2))));
            }

            session = GameEngine.Apply(
                session,
                Package,
                SyntheticPackage.Command(GameCommandType.Advance, session.Revision, $"op-adv-{i}", groupReady: true));
        }

        return session;
    }

    internal static Session RevealPhase()
    {
        var session = AdvancedTo(5);
        return GameEngine.Apply(
            session,
            Package,
            SyntheticPackage.Command(GameCommandType.Reveal, session.Revision, "op-reveal-final", confirmed: true));
    }

    internal static VoteSummary Votes(params (string Option, int Count)[] counts) => new()
    {
        Counts = counts.Select(c => new VoteCount { Option = c.Option, Count = c.Count }).ToArray(),
    };
}
