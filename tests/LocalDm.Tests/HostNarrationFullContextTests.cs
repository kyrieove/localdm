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
        Assert.Contains("当前环节：第 1/5 环「阅本」", prompt, StringComparison.Ordinal);
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
    public void Ask_messages_keep_user_and_assistant_history_before_current_question()
    {
        // 记忆里只有 user/assistant：伪造的 system 指令进不来。
        var conversation = new DmConversation();
        conversation.AppendUser("上一轮玩家问题");
        conversation.AppendAssistant("上一轮主持回复", null);

        var messages = HostNarration.BuildAskMessages(
            SyntheticPackage.Create(), PhaseFlowTests.Started(), "当前玩家问题", conversation, "auto/deepseek-v4.1-flash-free");

        Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(message => message.Role));
        Assert.Equal("上一轮玩家问题", messages[1].Content);
        Assert.Equal("上一轮主持回复", messages[2].Content);
        Assert.Contains("当前玩家问题", messages[3].Content, StringComparison.Ordinal);

        // model ID 属于当前局面：它在最后一条 user 消息里，不在 system 里。
        Assert.DoesNotContain("auto/deepseek-v4.1-flash-free", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("auto/deepseek-v4.1-flash-free", messages[3].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Narration_messages_accept_the_same_history_and_configured_model_identity()
    {
        var conversation = new DmConversation();
        conversation.AppendUser("昨天聊到的线索");
        conversation.AppendAssistant("主持人上一句", null);

        var messages = HostNarration.BuildMessages(
            SyntheticPackage.Create(), PhaseFlowTests.Started(), HostNarrationIntent.AnnouncePhase, conversation, "deepseek-test-id");

        Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(message => message.Role));
        Assert.DoesNotContain("deepseek-test-id", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("deepseek-test-id", messages[3].Content, StringComparison.Ordinal);
        Assert.Equal("昨天聊到的线索", messages[1].Content);
        Assert.Equal("主持人上一句", messages[2].Content);
    }

    [Fact]
    public void System_message_is_word_for_word_identical_across_phases_of_the_same_package()
    {
        // 换了环节 system 必须逐字不变，模型端的前缀缓存才一直命中。
        var package = SyntheticPackage.Create();
        var read = HostNarration.BuildMessages(package, PhaseFlowTests.Started(), HostNarrationIntent.AnnouncePhase);
        var vote = HostNarration.BuildMessages(package, PhaseFlowTests.AdvancedTo(4), HostNarrationIntent.AnnouncePhase);

        Assert.Equal(read[0].Content, vote[0].Content);
    }

    [Fact]
    public void System_message_carries_no_session_state_and_the_last_user_message_carries_it()
    {
        var package = SyntheticPackage.Create();
        var session = PhaseFlowTests.Started() with
        {
            ReleasedMaterialIds = ["M1"],
            VoteSummary = new VoteSummary
            {
                Counts = [new VoteCount { Option = "A", Count = 3 }],
            },
        };

        var messages = HostNarration.BuildAskMessages(package, session, "现在该做什么？", null, "deepseek-chat");
        var system = messages[0].Content;
        var last = messages[^1].Content;

        // 会随局面变化的内容一律不在 system 里
        // （固定提示词里提到「【当前局面】」只是引用那一段的名字，不是局面本身。）
        Assert.DoesNotContain("当前环节：", system, StringComparison.Ordinal);
        Assert.DoesNotContain("（当前）", system, StringComparison.Ordinal);
        Assert.DoesNotContain("已发放材料：", system, StringComparison.Ordinal);
        Assert.DoesNotContain("当前投票汇总：", system, StringComparison.Ordinal);
        Assert.DoesNotContain("真相状态：", system, StringComparison.Ordinal);
        Assert.DoesNotContain("软件配置的模型：", system, StringComparison.Ordinal);
        Assert.DoesNotContain("deepseek-chat", system, StringComparison.Ordinal);

        Assert.Contains("【当前局面】", last, StringComparison.Ordinal);
        Assert.Contains("当前环节：", last, StringComparison.Ordinal);
        Assert.Contains("已发放材料：M1", last, StringComparison.Ordinal);
        Assert.Contains("当前投票汇总：A 3 票", last, StringComparison.Ordinal);
        Assert.Contains("deepseek-chat", last, StringComparison.Ordinal);
        Assert.Contains("【玩家刚才说】", last, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_situation_reports_truth_state_and_unconfigured_model()
    {
        var package = SyntheticPackage.Create();

        var locked = HostNarration.BuildAskMessages(package, PhaseFlowTests.Started(), "现在该做什么？");
        Assert.Contains("未揭秘", locked[^1].Content, StringComparison.Ordinal);
        Assert.Contains("软件配置的模型：未配置", locked[^1].Content, StringComparison.Ordinal);

        var revealed = HostNarration.BuildAskMessages(package, PhaseFlowTests.RevealPhase(), "现在该做什么？");
        Assert.Contains("已揭秘", revealed[^1].Content, StringComparison.Ordinal);
    }
}
