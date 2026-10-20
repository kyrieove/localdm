using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;

namespace LocalDm.Tests;

/// <summary>
/// DM 读的是组织者手册、主持人专用页与线索卡，不读角色本：
/// 真人 DM 会提前读完手册，角色本始终在玩家手里，任何时候都不交给 AI。
/// 公共 grounding 仍只包含当前可公开内容。
/// </summary>
public sealed class HostNarrationSecretTests
{
    private static Session Revealed()
    {
        var session = new Session
        {
            SessionId = "session-1",
            PackageId = "pkg-synthetic",
            PackageVersion = "1",
            ManifestHash = new string('b', 64),
            State = SessionState.Revealed,
            CurrentPhaseId = "phase-reveal",
            Revision = 4,
            ReleasedMaterialIds = [],
        };
        return session;
    }

    /// <summary>四种局面：未揭秘、已揭秘、暂停（揭秘前）、暂停（揭秘后）。</summary>
    private static IReadOnlyList<Session> AllStates() =>
    [
        Revealed() with { State = SessionState.Playing, CurrentPhaseId = "phase-discuss" },
        Revealed(),
        Revealed() with { State = SessionState.Paused, ResumeState = SessionState.Playing, CurrentPhaseId = "phase-discuss" },
        Revealed() with { State = SessionState.Paused, ResumeState = SessionState.Revealed },
    ];

    private static string Prompt(Session session) =>
        string.Join("\n", HostNarration.BuildMessages(
            SyntheticPackage.Create(), session, HostNarrationIntent.RevealTruth).Select(m => m.Content));

    [Fact]
    public void Role_book_text_never_reaches_the_dm_in_any_state()
    {
        // 角色本在 DM 上下文构造处就被隔离：不是靠提示词让模型自己不看。
        foreach (var session in AllStates())
        {
            var prompt = Prompt(session);

            Assert.DoesNotContain(SyntheticPackage.RoleBookText, prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Truth_clue_cards_and_host_only_pages_reach_the_dm_in_any_state()
    {
        foreach (var session in AllStates())
        {
            var prompt = Prompt(session);

            Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
            Assert.Contains(SyntheticPackage.ClueCardText, prompt, StringComparison.Ordinal);
            Assert.Contains(SyntheticPackage.HostOnlyText, prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Uncovered_role_book_source_text_is_not_appended()
    {
        // 角色本的 span 没被任何段覆盖：它也不能从「未覆盖来源原文」这一段漏出去。
        var prompt = Prompt(Revealed() with { State = SessionState.Playing });

        var section = Section(prompt, "【未被规范文段完整覆盖的来源原文】");

        Assert.DoesNotContain("合成页 6", section, StringComparison.Ordinal);
        Assert.Contains("合成页 7", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Clue_card_segment_is_labelled_with_its_material_group()
    {
        var prompt = Prompt(Revealed() with { State = SessionState.Playing });

        Assert.Contains("类别=线索卡", prompt, StringComparison.Ordinal);
        Assert.Contains("分组：M1", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Locked_session_keeps_public_grounding_filtered()
    {
        var playing = Revealed() with { State = SessionState.Playing };

        var prompt = Prompt(playing);
        var grounding = HostNarration.BuildGroundingText(SyntheticPackage.Create(), playing);

        Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPackage.PublicTruth, grounding, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPackage.RoleBookText, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Revealed_session_gives_dm_truth_but_never_role_book()
    {
        var revealed = Revealed();

        var prompt = Prompt(revealed);
        var grounding = HostNarration.BuildGroundingText(SyntheticPackage.Create(), revealed);

        Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
        Assert.Contains(SyntheticPackage.PublicTruth, grounding, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPackage.RoleBookText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPackage.RoleBookText, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_in_reveal_phase_keeps_truth_unlocked()
    {
        // 引擎的 Complete 把「暂停且 ResumeState=Revealed」视为已揭秘；
        // 提示词必须用同一套判定，否则主持人一按暂停真相就消失了。
        var paused = Revealed() with { State = SessionState.Paused, ResumeState = SessionState.Revealed };

        var prompt = Prompt(paused);
        var grounding = HostNarration.BuildGroundingText(SyntheticPackage.Create(), paused);

        Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
        Assert.Contains(SyntheticPackage.PublicTruth, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_before_reveal_keeps_handbook_for_dm_but_no_truth_in_grounding()
    {
        var paused = Revealed() with
        {
            State = SessionState.Paused,
            ResumeState = SessionState.Playing,
            CurrentPhaseId = "phase-discuss",
        };

        var prompt = Prompt(paused);

        Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPackage.RoleBookText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPackage.PublicTruth, HostNarration.BuildGroundingText(SyntheticPackage.Create(), paused), StringComparison.Ordinal);
    }

    [Fact]
    public void Grounding_text_lists_released_material_ids_only()
    {
        var playing = Revealed() with
        {
            State = SessionState.Playing,
            CurrentPhaseId = "phase-search",
            ReleasedMaterialIds = ["M1"],
        };

        var grounding = HostNarration.BuildGroundingText(SyntheticPackage.Create(), playing);

        Assert.Contains("M1", grounding, StringComparison.Ordinal);
        Assert.DoesNotContain("M2", grounding, StringComparison.Ordinal);
    }

    /// <summary>取出某一段的内容：从标题到下一段标题之前。</summary>
    private static string Section(string prompt, string header)
    {
        var start = prompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var body = prompt[(start + header.Length)..];
        var next = body.IndexOf("\n【", StringComparison.Ordinal);
        return next < 0 ? body : body[..next];
    }
}
