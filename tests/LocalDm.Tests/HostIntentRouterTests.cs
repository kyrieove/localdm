using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;

namespace LocalDm.Tests;

/// <summary>
/// 输入分流：整句说出来才是命令，问句一律交给模型。
///
/// 「下一步我该做什么？」是最容易出错的一句：它是在问，不是在推进环节。
/// </summary>
public sealed class HostIntentRouterTests
{
    private readonly HostIntentRouter _router = new();

    [Theory]
    [InlineData("下一环节")]
    [InlineData("进入下一阶段。")]
    [InlineData("推进！")]
    [InlineData("进入下一环节")]
    [InlineData("开始下一轮")]
    public void Advance_commands_propose_advancing(string text)
    {
        var intent = _router.Route(text);

        Assert.Equal(HostIntentKind.ProposeCommand, intent.Kind);
        Assert.Equal(PublicAction.Advance, intent.ProposedAction);
    }

    [Theory]
    [InlineData("揭秘")]
    [InlineData("公开真相？")]
    [InlineData("公布真相")]
    [InlineData("揭露真相")]
    public void Reveal_commands_propose_revealing(string text)
    {
        var intent = _router.Route(text);

        Assert.Equal(HostIntentKind.ProposeCommand, intent.Kind);
        Assert.Equal(PublicAction.Reveal, intent.ProposedAction);
    }

    [Theory]
    [InlineData("下一步我该做什么？")]
    [InlineData("下一步")]
    [InlineData("我们什么时候推进到投票？")]
    [InlineData("揭秘之前还要做什么")]
    [InlineData("谁是凶手？")]
    [InlineData("帮我压缩上下文吧")]
    [InlineData("直接揭秘吧，谁是凶手")]
    [InlineData("我们进入下一环节吧")]
    public void Questions_stay_questions(string text)
    {
        var intent = _router.Route(text);

        Assert.Equal(HostIntentKind.PublicQuestion, intent.Kind);
        Assert.Null(intent.ProposedAction);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("压缩上下文")]
    public void Compact_commands_ask_for_compaction(string text)
    {
        var intent = _router.Route(text);

        Assert.Equal(HostIntentKind.Compact, intent.Kind);
        Assert.Null(intent.ProposedAction);
    }
}
