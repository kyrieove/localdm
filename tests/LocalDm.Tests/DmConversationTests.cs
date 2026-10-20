using System.IO;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Tests;

/// <summary>
/// 上下文管理：按模型的上下文窗口计量占用，快满时把较早的对话整理成备忘。
///
/// 自动整理、手动整理、整理失败三条路径都要能测，所以这里用一个可控的假网关：
/// 用量可调、整理请求可失败、每次请求都留痕。
/// </summary>
public sealed class DmConversationTests : IDisposable
{
    private const int Window = 10_000;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-dmconv-" + Guid.NewGuid().ToString("N"));

    public DmConversationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
    }

    // ===== 估算 =====

    [Fact]
    public void Token_estimate_counts_chinese_and_latin_characters()
    {
        Assert.Equal(75, ContextBudget.EstimateTokens(new string('中', 100)));
        Assert.Equal(30, ContextBudget.EstimateTokens(new string('a', 100)));
    }

    [Fact]
    public void Real_usage_is_preferred_over_estimating()
    {
        var conversation = new DmConversation();
        conversation.AppendUser("问一句");
        conversation.AppendAssistant("答一句", new MagpieUsage(1000, 200, null));

        var estimate = ContextBudget.EstimateNextPromptTokens(conversation, "系统提示词", "下一条");

        Assert.Equal(1000 + 200 + ContextBudget.EstimateTokens("下一条"), estimate);
    }

    // ===== 自动整理 =====

    [Fact]
    public async Task Occupancy_below_eighty_percent_sends_without_compacting()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        host.InputText = "现在该做什么？";
        await host.SendAsync();

        // 下一轮要发的最后一条 user 消息：占用比例要按它算。
        var nextTokens = ContextBudget.EstimateTokens(gateway.Requests[0].Messages[^1].Content);
        var usable = ContextBudget.UsableTokens(Window);

        // 上一轮返回的用量决定这一轮要不要整理：先发一轮把用量放进去。
        gateway.Usage = new MagpieUsage((int)(usable * 0.79) - nextTokens - 1, 0, null);
        host.InputText = "现在该做什么？";
        await host.SendAsync();
        host.InputText = "现在该做什么？";
        await host.SendAsync();

        Assert.Empty(CompactionRequests(gateway));
        Assert.Equal(0, host.Conversation.CompactionCount);
    }

    [Fact]
    public async Task Occupancy_at_eighty_percent_compacts_before_sending()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        host.InputText = "现在该做什么？";
        await host.SendAsync();

        var nextTokens = ContextBudget.EstimateTokens(gateway.Requests[0].Messages[^1].Content);
        var usable = ContextBudget.UsableTokens(Window);

        // 79%：先不整理
        gateway.Usage = new MagpieUsage((int)(usable * 0.79) - nextTokens - 1, 0, null);
        host.InputText = "现在该做什么？";
        await host.SendAsync();
        host.InputText = "现在该做什么？";
        await host.SendAsync();
        Assert.Empty(CompactionRequests(gateway));

        // 80%：这一轮先整理再发
        gateway.Usage = new MagpieUsage((int)Math.Ceiling(usable * 0.80) - nextTokens, 0, null);
        host.InputText = "现在该做什么？";
        await host.SendAsync();
        host.InputText = "现在该做什么？";
        await host.SendAsync();

        var compactions = CompactionRequests(gateway);
        Assert.Single(compactions);
        Assert.Equal(1, host.Conversation.CompactionCount);
    }

    [Fact]
    public async Task Automatic_compaction_keeps_at_least_the_last_two_rounds_and_the_first_kept_turn_is_a_player_turn()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        // 先聊几轮，让历史足够长
        for (var i = 0; i < 4; i++)
        {
            host.InputText = $"第 {i} 轮问题";
            await host.SendAsync();
        }

        var nextTokens = ContextBudget.EstimateTokens(gateway.Requests[0].Messages[^1].Content);
        var usable = ContextBudget.UsableTokens(Window);
        gateway.Usage = new MagpieUsage((int)(usable * 0.85) - nextTokens, 0, null);

        host.InputText = "再问一句";
        await host.SendAsync();
        host.InputText = "再问一句";
        await host.SendAsync();

        // 更早的轮次被备忘取代
        Assert.Equal(1, host.Conversation.CompactionCount);
        Assert.Equal("本局备忘：已经查过书房。", host.Conversation.Memo);
        Assert.True(host.Conversation.Turns.Count >= 4, "至少要保留最近 2 轮。");
        Assert.Equal("user", host.Conversation.Turns[0].Role);
        Assert.DoesNotContain(
            host.Conversation.Turns,
            turn => turn.Content.Contains("第 0 轮问题", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Second_compaction_contains_the_previous_memo_and_replaces_it()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        var nextTokens = ContextBudget.EstimateTokens(gateway.Requests.Count > 0 ? gateway.Requests[0].Messages[^1].Content : "x");
        var usable = ContextBudget.UsableTokens(Window);
        gateway.Usage = new MagpieUsage((int)(usable * 0.85) - nextTokens, 0, null);

        host.InputText = "第一轮";
        await host.SendAsync();
        host.InputText = "第二轮";
        await host.SendAsync();
        host.InputText = "第三轮";
        await host.SendAsync();

        gateway.CompactionMemo = "第二份备忘";
        gateway.Usage = new MagpieUsage((int)(usable * 0.85) - nextTokens, 0, null);
        host.InputText = "第四轮";
        await host.SendAsync();
        host.InputText = "第五轮";
        await host.SendAsync();

        var compactions = CompactionRequests(gateway);
        Assert.True(compactions.Count >= 2, "应该整理过两次。");
        Assert.Contains("本局备忘：已经查过书房。", compactions[1].Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal("第二份备忘", host.Conversation.Memo);
    }

    [Fact]
    public async Task Compaction_request_uses_the_same_system_message_as_a_normal_turn()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        host.InputText = "现在该做什么？";
        await host.SendAsync();

        var nextTokens = ContextBudget.EstimateTokens(gateway.Requests[0].Messages[^1].Content);
        var usable = ContextBudget.UsableTokens(Window);
        gateway.Usage = new MagpieUsage((int)(usable * 0.85) - nextTokens, 0, null);
        host.InputText = "现在该做什么？";
        await host.SendAsync();
        host.InputText = "现在该做什么？";
        await host.SendAsync();

        var compaction = Assert.Single(CompactionRequests(gateway));
        var turn = gateway.Requests.Last(r => !IsCompaction(r));
        Assert.Equal(turn.Messages[0].Content, compaction.Messages[0].Content);
    }

    // ===== 整理失败 =====

    [Fact]
    public async Task Failed_compaction_keeps_the_conversation_and_still_sends_the_turn()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        host.InputText = "现在该做什么？";
        await host.SendAsync();

        var nextTokens = ContextBudget.EstimateTokens(gateway.Requests[0].Messages[^1].Content);
        var usable = ContextBudget.UsableTokens(Window);
        gateway.Usage = new MagpieUsage((int)(usable * 0.85) - nextTokens, 0, null);
        gateway.FailCompaction = true;

        var before = gateway.Requests.Count;
        host.InputText = "现在该做什么？";
        await host.SendAsync();
        host.InputText = "现在该做什么？";
        await host.SendAsync();

        // 记忆没被动过，这一轮照常发出去
        Assert.Equal(0, host.Conversation.CompactionCount);
        Assert.Null(host.Conversation.Memo);
        Assert.True(gateway.Requests.Count > before, "这一轮仍然要发给模型。");
        Assert.Contains(
            host.Messages,
            m => m.IsEvent && m.Text.Contains("整理较早的对话失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_compaction_drops_the_oldest_rounds_when_the_context_is_already_full()
    {
        // 用量按请求内容算：一轮真实占用就靠说话的长短堆出来。
        var gateway = new ScriptedGateway { FailCompaction = true };
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        // 每轮说很多话：聊够几轮就真的把窗口占满了。
        var longText = new string('话', 1200);
        var sends = 0;
        while (sends < 12
               && !host.Messages.Any(m => m.IsEvent && m.Text.Contains("已丢弃最早的", StringComparison.Ordinal)))
        {
            host.InputText = longText;
            await host.SendAsync();
            sends++;
        }

        var drop = host.Messages.FirstOrDefault(m => m.IsEvent && m.Text.Contains("已丢弃最早的", StringComparison.Ordinal));
        Assert.NotNull(drop);
        Assert.Contains("轮对话以继续主持", drop!.Text, StringComparison.Ordinal);

        // 丢掉的确实是最早的轮次：说过那么多轮，记忆里剩下的却更少。
        Assert.True(host.Conversation.Turns.Count < sends * 2, "最早的几轮应该已经被丢掉。");

        // 丢弃之后紧接着发出去的那一轮，占用必须降到 90% 以下。
        var turnAfterDrop = gateway.Requests.Last(r => !IsCompaction(r));
        var requestTokens = ContextBudget.EstimateTokens(
            string.Join("\n", turnAfterDrop.Messages.Select(m => m.Content)));
        Assert.True(
            requestTokens < ContextBudget.UsableTokens(Window) * ContextBudget.DropTargetRatio,
            $"丢弃之后占用必须低于 90%。实际 {requestTokens} / {ContextBudget.UsableTokens(Window)}。");
    }

    // ===== 手动整理 =====

    [Fact]
    public async Task Manual_compaction_keeps_only_the_last_two_rounds()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        for (var i = 0; i < 4; i++)
        {
            host.InputText = $"第 {i} 轮问题";
            await host.SendAsync();
        }

        Assert.True(host.CompactCommand.CanExecute(null));

        host.CompactCommand.Execute(null);
        await WaitUntilAsync(() => host.Conversation.CompactionCount == 1);

        Assert.Equal(1, host.Conversation.CompactionCount);
        Assert.Equal("本局备忘：已经查过书房。", host.Conversation.Memo);
        Assert.Equal(4, host.Conversation.Turns.Count);
        Assert.Equal("user", host.Conversation.Turns[0].Role);
    }

    [Fact]
    public async Task Compact_command_is_unavailable_when_there_is_too_little_to_compact()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        Assert.False(host.CompactCommand.CanExecute(null));

        host.InputText = "一句话";
        await host.SendAsync();

        // 一轮对话（两条）还太少：整理不出什么来。
        Assert.False(host.CompactCommand.CanExecute(null));
    }

    // ===== 记忆与消息 =====

    [Fact]
    public async Task A_failed_turn_does_not_put_the_player_words_into_the_memory()
    {
        var gateway = new ScriptedGateway { FailTurn = true };
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        host.InputText = "这一句不该进记忆";
        await host.SendAsync();

        Assert.Empty(host.Conversation.Turns);
    }

    [Fact]
    public void Adjacent_player_turns_are_merged_into_one_message()
    {
        // 某次请求失败时玩家的话会留在记忆里而没有回复，下一轮就出现两条 user 挨着。
        var conversation = new DmConversation();
        conversation.AppendUser("第一句");
        conversation.AppendUser("第二句");

        var messages = HostNarration.BuildAskMessages(
            SyntheticPackage.Create(),
            PhaseFlowTests.Started(),
            "现在的问题",
            conversation);

        Assert.Equal(new[] { "system", "user" }, messages.Select(m => m.Role).ToArray());
        Assert.Contains("第一句", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("第二句", messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_memo_sits_in_the_last_user_message_before_the_current_situation()
    {
        var conversation = new DmConversation();
        conversation.ApplyCompaction("查过书房与地下室。", 0);

        var messages = HostNarration.BuildAskMessages(
            SyntheticPackage.Create(),
            PhaseFlowTests.Started(),
            "现在该做什么？",
            conversation);

        var system = messages[0].Content;
        var last = messages[^1].Content;

        Assert.DoesNotContain("查过书房与地下室。", system, StringComparison.Ordinal);
        Assert.Contains("【本局备忘（较早的对话已整理）】", last, StringComparison.Ordinal);
        Assert.Contains("查过书房与地下室。", last, StringComparison.Ordinal);
        Assert.True(
            last.IndexOf("【本局备忘（较早的对话已整理）】", StringComparison.Ordinal)
            < last.IndexOf("【当前局面】", StringComparison.Ordinal),
            "备忘必须在当前局面之前。");
    }

    [Fact]
    public void Snapshot_round_trip_keeps_memo_turns_and_compaction_count()
    {
        var conversation = new DmConversation();
        conversation.AppendUser("问一句");
        conversation.AppendAssistant("答一句", new MagpieUsage(120, 30, 100));
        conversation.ApplyCompaction("一份备忘", 0);

        var restored = DmConversation.FromSnapshot(conversation.ToSnapshot());

        Assert.Equal(conversation.Memo, restored.Memo);
        Assert.Equal(conversation.Turns.Count, restored.Turns.Count);
        Assert.Equal(
            conversation.Turns.Select(t => (t.Role, t.Content)).ToArray(),
            restored.Turns.Select(t => (t.Role, t.Content)).ToArray());
        Assert.Equal(conversation.CompactionCount, restored.CompactionCount);
    }

    // ===== 夹具 =====

    private async Task<HostViewModel> NewHostAsync(ScriptedGateway gateway)
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var settings = ToolSettings.Default with
        {
            ModelId = "test-model",
            AutoRead = false,
            ContextWindowTokens = Window,
        };

        return new HostViewModel(
            new HostDirector(gateway, new SilentTts()),
            () => settings,
            new HostIntentRouter(),
            new CommandDispatcher(store),
            store,
            new SilentTts());
    }

    private static List<MagpieCompletionRequest> CompactionRequests(ScriptedGateway gateway) =>
        gateway.Requests.Where(IsCompaction).ToList();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }

    private static bool IsCompaction(MagpieCompletionRequest request) =>
        request.Messages.Any(m => m.Content.Contains("【整理本局记忆】", StringComparison.Ordinal));

    /// <summary>可控的假网关：用量可调、整理可失败、每轮回复可指定。</summary>
    private sealed class ScriptedGateway : IMagpieGateway
    {
        public List<MagpieCompletionRequest> Requests { get; } = [];

        /// <summary>每次模型调用返回的用量；为空时按请求内容估算。</summary>
        public MagpieUsage? Usage { get; set; }

        /// <summary>整理请求要返回的备忘。</summary>
        public string CompactionMemo { get; set; } = "本局备忘：已经查过书房。";

        /// <summary>整理请求要失败。</summary>
        public bool FailCompaction { get; set; }

        /// <summary>正常主持请求要失败。</summary>
        public bool FailTurn { get; set; }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ReplyFor(request));

        public Task<MagpieCompletion> CompleteWithUsageAsync(
            MagpieCompletionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (IsCompaction(request) && FailCompaction)
            {
                throw new DomainRuleException(DomainErrorCode.MODEL_FAILED, "整理本局记忆失败。");
            }

            if (!IsCompaction(request) && FailTurn)
            {
                throw new DomainRuleException(DomainErrorCode.MODEL_FAILED, "模型这次没有给出可用内容。");
            }

            return Task.FromResult(new MagpieCompletion(ReplyFor(request), Usage ?? RequestUsage(request)));
        }

        /// <summary>按请求内容估算用量：真实服务端的读数就是这样随对话变长。</summary>
        private static MagpieUsage RequestUsage(MagpieCompletionRequest request) => new(
            ContextBudget.EstimateTokens(string.Join("\n", request.Messages.Select(m => m.Content))),
            20,
            null);

        private string ReplyFor(MagpieCompletionRequest request) =>
            IsCompaction(request) ? CompactionMemo : "这是测试用的公开主持文字。";
    }

    private sealed class SilentTts : ITtsService
    {
        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop()
        {
        }
    }
}
