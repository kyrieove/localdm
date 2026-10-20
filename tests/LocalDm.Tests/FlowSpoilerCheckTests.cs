using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Core.Import;

namespace LocalDm.Tests;

/// <summary>
/// 剧透检查：用合成文本验证「照抄真相就拦住」。
/// 真相页原文全部是占位句子，不含任何真实剧本内容。
/// </summary>
public sealed class FlowSpoilerCheckTests
{
    private const string TruthPage =
        "凶手在子夜换掉了走廊尽头那盏灯，然后从侧门离开海砂馆，把凶器埋在后院的老井旁边。";

    [Fact]
    public void Hint_copying_seven_characters_or_more_is_caught()
    {
        var flow = Flow(
            hint: "从侧门离开海砂馆，把凶器埋在后院",
            title: "搜证",
            conditions: []);

        var hits = FlowSpoilerCheck.Check(flow, [TruthPage]);

        var hit = Assert.Single(hits);
        Assert.Equal(0, hit.StepIndex);
        Assert.Equal("提示", hit.Field);
        Assert.Equal("这条提示可能含剧情，已隐藏，请重写", hit.Display);
        // 命中的条目不把原文交给界面。
        Assert.DoesNotContain("侧门", hit.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Six_characters_of_overlap_is_not_caught()
    {
        // 只重合「从侧门离开海」六个字：门限是 7，不该命中。
        var flow = Flow(hint: "从侧门离开海", title: "搜证", conditions: []);

        Assert.Empty(FlowSpoilerCheck.Check(flow, [TruthPage]));
    }

    [Fact]
    public void Common_titles_like_read_or_vote_are_not_caught()
    {
        var flow = Flow(
            hint: "拿到角色本，读到写着先不要翻页的地方为止。",
            title: "阅本",
            conditions: ["全员读完自己的部分", "投票用纸笔写"]);

        Assert.Empty(FlowSpoilerCheck.Check(flow, [TruthPage]));
    }

    [Fact]
    public void Conditions_are_checked_too()
    {
        var flow = Flow(
            hint: "翻开线索卡。",
            title: "搜证",
            conditions: ["确认凶器埋在后院的老井旁边"]);

        var hit = Assert.Single(FlowSpoilerCheck.Check(flow, [TruthPage]));
        Assert.Equal("条件 1", hit.Field);
    }

    [Fact]
    public void Threshold_is_the_same_constant_as_the_memo_check()
    {
        // 两处共用一个门限：改分数值就会让备忘与剧透检查的宽严不一致。
        Assert.Equal(DmMemoCheck.VerbatimRunLength, FlowSpoilerCheck.VerbatimRunLength);
        Assert.Equal(7, FlowSpoilerCheck.VerbatimRunLength);
    }

    private static ScriptFlow Flow(string hint, string title, IReadOnlyList<string> conditions) => new()
    {
        Steps =
        [
            new ScriptFlowStep
            {
                StepId = "step-1",
                Title = title,
                Kind = ScriptStepKind.Search,
                Hint = hint,
                Conditions = conditions,
                SourceRef = "组织者手册第 2 页",
            },
        ],
    };
}
