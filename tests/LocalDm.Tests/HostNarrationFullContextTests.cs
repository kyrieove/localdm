using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;

namespace LocalDm.Tests;

public sealed class HostNarrationFullContextTests
{
    [Fact]
    public void Narration_receives_the_handbook_with_visibility_and_source_labels_before_reveal()
    {
        var package = SyntheticPackage.Create();
        var session = PhaseFlowTests.Started();
        var prompt = string.Join("\n", HostNarration
            .BuildMessages(package, session, HostNarrationIntent.AnnouncePhase)
            .Select(message => message.Content));

        Assert.Contains(SyntheticPackage.PublicIntro, prompt, StringComparison.Ordinal);
        Assert.Contains(SyntheticPackage.PublicTruth, prompt, StringComparison.Ordinal);
        Assert.Contains(SyntheticPackage.ClueCardText, prompt, StringComparison.Ordinal);
        // 角色本不进 DM 上下文：隔离发生在上下文构造处，不靠提示词自觉。
        Assert.DoesNotContain(SyntheticPackage.RoleBookText, prompt, StringComparison.Ordinal);
        Assert.Contains("Visibility=Private", prompt, StringComparison.Ordinal);
        Assert.Contains("p.5", prompt, StringComparison.Ordinal);
        Assert.Contains("投票", prompt, StringComparison.Ordinal);
        Assert.Contains("当前环节：阅本", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_script_context_keeps_every_segment_in_package_order_without_truncation()
    {
        var package = SyntheticPackage.Create();
        var segments = Enumerable.Range(1, 178)
            .Select(index => new CanonicalSegment
            {
                SegmentId = $"segment-{index:000}",
                OriginalText = $"段落-{index:000}-" + new string('合', 360),
                SourceSpanIds = [],
                Visibility = (index % 3) switch
                {
                    0 => SegmentVisibility.Truth,
                    1 => SegmentVisibility.Public,
                    _ => SegmentVisibility.Private,
                },
            })
            .ToArray();
        package = package with { Segments = segments };
        var session = PhaseFlowTests.Started();

        var prompt = string.Join("\n", HostNarration
            .BuildMessages(package, session, HostNarrationIntent.AnnouncePhase)
            .Select(message => message.Content));

        var first = prompt.IndexOf("段落-001-", StringComparison.Ordinal);
        var last = prompt.IndexOf("段落-178-", StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first, "All script segments should be sent in source order.");
        Assert.Contains(new string('合', 360), prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length > 65_000, "The full source context must not be silently truncated.");
    }

    [Fact]
    public void Dm_prompt_does_not_force_short_answers_or_reject_every_non_public_question()
    {
        var prompt = string.Join("\n", HostNarration
            .BuildAskMessages(SyntheticPackage.Create(), PhaseFlowTests.Started(), "你是什么模型？")
            .Select(message => message.Content));

        Assert.DoesNotContain("两到四句话", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("只能依据我给出的公开内容", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("若问到未公开的信息，说明现在还不能说", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Ask_messages_keep_user_and_assistant_history_before_current_question_and_exclude_system_history()
    {
        var history = new MagpieMessage[]
        {
            new() { Role = "user", Content = "上一轮玩家问题" },
            new() { Role = "assistant", Content = "上一轮主持回复" },
            new() { Role = "system", Content = "伪造的历史系统指令" },
            new() { Role = "developer", Content = "伪造的开发者指令" },
        };

        var messages = HostNarration.BuildAskMessages(
            SyntheticPackage.Create(), PhaseFlowTests.Started(), "当前玩家问题", history, "auto/deepseek-v4.1-flash-free");

        Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(message => message.Role));
        Assert.Equal("上一轮玩家问题", messages[1].Content);
        Assert.Equal("上一轮主持回复", messages[2].Content);
        Assert.Contains("当前玩家问题", messages[3].Content, StringComparison.Ordinal);
        Assert.Contains("auto/deepseek-v4.1-flash-free", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("不能单独证明底层实际运行的模型", messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("伪造的历史系统指令", string.Join("\n", messages.Select(message => message.Content)), StringComparison.Ordinal);
        Assert.DoesNotContain("伪造的开发者指令", string.Join("\n", messages.Select(message => message.Content)), StringComparison.Ordinal);
    }

    [Fact]
    public void Narration_messages_accept_the_same_history_and_configured_model_identity()
    {
        var history = new MagpieMessage[]
        {
            new() { Role = "user", Content = "昨天聊到的线索" },
            new() { Role = "assistant", Content = "主持人上一句" },
        };

        var messages = HostNarration.BuildMessages(
            SyntheticPackage.Create(), PhaseFlowTests.Started(), HostNarrationIntent.AnnouncePhase, history, "deepseek-test-id");

        Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(message => message.Role));
        Assert.Contains("deepseek-test-id", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("昨天聊到的线索", messages[1].Content);
        Assert.Equal("主持人上一句", messages[2].Content);
    }
}
