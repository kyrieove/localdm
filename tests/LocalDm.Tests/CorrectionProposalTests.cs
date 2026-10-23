using LocalDm.Core.Hosting;

namespace LocalDm.Tests;

public sealed class CorrectionProposalTests
{
    [Fact]
    public void Revert_tag_is_removed_from_its_own_line()
    {
        var result = CorrectionProposal.Extract("好的，刚才推进早了。\n〔提议：回到上一环节〕");

        Assert.Equal("好的，刚才推进早了。", result.Text);
        Assert.Equal(CorrectionKind.RevertPhase, result.Kind);
    }

    [Fact]
    public void Vote_tag_accepts_windows_newline_and_surrounding_whitespace()
    {
        var result = CorrectionProposal.Extract("票数我记下了。\r\n  〔提议：重新录入票数〕  ");

        Assert.Equal("票数我记下了。", result.Text);
        Assert.Equal(CorrectionKind.CorrectVote, result.Kind);
    }

    [Fact]
    public void Inline_tag_is_preserved_without_a_proposal()
    {
        const string reply = "要不要〔提议：回到上一环节〕呢";
        var result = CorrectionProposal.Extract(reply);

        Assert.Null(result.Kind);
        Assert.Equal(reply, result.Text);
    }

    [Fact]
    public void Ordinary_reply_is_unchanged()
    {
        var result = CorrectionProposal.Extract("普通回答");

        Assert.Equal("普通回答", result.Text);
        Assert.Null(result.Kind);
    }
}
