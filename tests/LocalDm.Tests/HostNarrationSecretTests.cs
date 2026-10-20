using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;

namespace LocalDm.Tests;

/// <summary>
/// DM 内部可获得完整剧本；公共 grounding 仍只包含当前可公开内容。
/// </summary>
public sealed class HostNarrationSecretTests
{
    private const string TruthText = "这是合成真相，只用于验证揭秘投影。";
    private const string PrivateText = "这是合成角色的秘密，公共界面不应出现。";

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

    private static string Prompt(Session session) =>
        string.Join("\n", HostNarration.BuildMessages(
            SyntheticPackage.Create(), session, HostNarrationIntent.RevealTruth).Select(m => m.Content));

    [Fact]
    public void Locked_session_gives_full_script_to_dm_but_keeps_public_grounding_filtered()
    {
        var playing = Revealed() with { State = SessionState.Playing };

        var prompt = Prompt(playing);
        var grounding = HostNarration.BuildGroundingText(SyntheticPackage.Create(), playing);

        Assert.Contains(TruthText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(TruthText, grounding, StringComparison.Ordinal);
        Assert.Contains(PrivateText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateText, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Revealed_session_gives_dm_truth_and_private_role_material()
    {
        var revealed = Revealed();

        var prompt = Prompt(revealed);
        var grounding = HostNarration.BuildGroundingText(SyntheticPackage.Create(), revealed);

        Assert.Contains(TruthText, prompt, StringComparison.Ordinal);
        Assert.Contains(TruthText, grounding, StringComparison.Ordinal);
        Assert.Contains(PrivateText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateText, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_in_reveal_phase_keeps_truth_unlocked()
    {
        // 引擎的 Complete 把「暂停且 ResumeState=Revealed」视为已揭秘；
        // 提示词必须用同一套判定，否则主持人一按暂停真相就消失了。
        var paused = Revealed() with { State = SessionState.Paused, ResumeState = SessionState.Revealed };

        var prompt = Prompt(paused);
        var grounding = HostNarration.BuildGroundingText(SyntheticPackage.Create(), paused);

        Assert.Contains(TruthText, prompt, StringComparison.Ordinal);
        Assert.Contains(TruthText, grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_before_reveal_keeps_full_script_in_dm_context()
    {
        var paused = Revealed() with
        {
            State = SessionState.Paused,
            ResumeState = SessionState.Playing,
            CurrentPhaseId = "phase-discuss",
        };

        var prompt = Prompt(paused);

        Assert.Contains(TruthText, prompt, StringComparison.Ordinal);
        Assert.Contains(PrivateText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(TruthText, HostNarration.BuildGroundingText(SyntheticPackage.Create(), paused), StringComparison.Ordinal);
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
}
