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

    /// <summary>小到主持资料自己就占九成以上的窗口。</summary>
    private const int TinyWindow = 8_192;

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

    // ===== 保留原话的起点 =====

    [Fact]
    public void Round_start_counts_player_turns_from_the_end()
    {
        // 10 条交替消息（5 轮）：最近 2 轮的起点是第 3 轮的 user，下标 6。
        var conversation = ShortRounds(5);
        var turns = conversation.Turns;
        Assert.Equal(10, turns.Count);

        Assert.Equal(6, ContextBudget.RoundStart(turns, 2));
        Assert.Equal(8, ContextBudget.RoundStart(turns, 1));
        Assert.Equal(2, ContextBudget.RoundStart(turns, 4));
    }

    [Fact]
    public void Round_start_is_zero_when_there_are_not_enough_rounds()
    {
        var conversation = ShortRounds(1);

        Assert.Equal(0, ContextBudget.RoundStart(conversation.Turns, 2));
        Assert.Equal(0, ContextBudget.RoundStart(Array.Empty<MagpieMessage>(), 2));
    }

    [Fact]
    public void Large_window_keeps_more_than_the_minimum_two_rounds()
    {
        // 窗口很大、轮次不长：15% 的预算能装下好几轮，就该多留几轮原话。
        // 10 轮 × 每轮 150 token，预算 989：落在第 7 轮附近，比最少 2 轮多得多。
        var conversation = ShortRounds(10);
        var usable = ContextBudget.UsableTokens(Window);
        var budget = (int)(usable * ContextBudget.KeepRatio);

        var index = ContextBudget.KeepFromIndex(conversation, Window);
        var kept = conversation.Turns.Skip(index).ToArray();
        var keptTokens = kept.Sum(turn => ContextBudget.EstimateTokens(turn.Content));

        Assert.True(kept.Length > ContextBudget.MinKeptRounds * 2, "预算够时不该只保留 2 轮。");
        Assert.True(keptTokens >= budget, "保留部分应当累加到 15% 预算。");
        Assert.Equal("user", kept[0].Role);
    }

    [Fact]
    public void A_long_last_reply_still_keeps_the_last_two_rounds_starting_with_a_player_turn()
    {
        // 最后一条 assistant 一句话就超过 15% 预算：预算在它身上就用完了。
        // 这时仍然要留下最近 2 轮，而且第一条是 user，不能因为「后移找 user」一条都不留。
        var conversation = ShortRounds(10);
        conversation.AppendAssistant(new string('长', 10_000), null);
        var usable = ContextBudget.UsableTokens(Window);
        var budget = (int)(usable * ContextBudget.KeepRatio);
        Assert.True(ContextBudget.EstimateTokens(conversation.Turns[^1].Content) > budget, "最后一条要真的超过预算。");

        var index = ContextBudget.KeepFromIndex(conversation, Window);
        var kept = conversation.Turns.Skip(index).ToArray();

        Assert.True(kept.Length >= ContextBudget.MinKeptRounds * 2 - 1, "至少保留最近 2 轮。");
        Assert.Equal("user", kept[0].Role);
        // 最后一条超长回复本身必须被留下
        Assert.Equal(conversation.Turns[^1].Content, kept[^1].Content);
    }

    [Fact]
    public void Manual_keep_start_lands_on_a_player_turn()
    {
        // 条数是单数时，「最近 2 轮」的起点会落在 assistant 上：要向前移到 user。
        var conversation = new DmConversation();
        conversation.AppendAssistant("开场白", null);
        conversation.AppendUser("第一问");
        conversation.AppendAssistant("第一答", null);
        conversation.AppendUser("第二问");
        conversation.AppendAssistant("第二答", null);

        var index = ContextBudget.ManualKeepFromIndex(conversation);

        Assert.Equal("user", conversation.Turns[index].Role);
        // 起点向前移到「第一问」，开场白与它一起留给整理
        Assert.Equal(1, index);
        Assert.True(ContextBudget.CanCompact(conversation));
    }

    [Fact]
    public void Only_two_rounds_mean_nothing_to_compact()
    {
        // 4 条正好是最近 2 轮：起点落在第一条 user 上，前面没有可整理的内容。
        var conversation = ShortRounds(2);

        Assert.Equal(0, ContextBudget.ManualKeepFromIndex(conversation));
        Assert.False(ContextBudget.CanCompact(conversation));

        conversation.AppendUser("第三问");
        conversation.AppendAssistant("第三答", null);
        Assert.True(ContextBudget.CanCompact(conversation));
    }

    /// <summary>若干轮对话，每轮 2 条、约 150 token（200 个汉字加一句回复）。</summary>
    private static DmConversation ShortRounds(int rounds)
    {
        var conversation = new DmConversation();
        for (var i = 0; i < rounds; i++)
        {
            conversation.AppendUser($"{i}：" + new string('问', 200));
            conversation.AppendAssistant("答", null);
        }

        return conversation;
    }

    // ===== 自动整理 =====

    [Fact]
    public async Task Occupancy_below_eighty_percent_sends_without_compacting()
    {
        // 对话不长，占用一直低于 80%：一轮都不整理。
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        for (var i = 0; i < 4; i++)
        {
            await SendAsync(host, "现在该做什么？");
        }

        Assert.True(LastRatio(host) < ContextBudget.AutoCompactRatio, "这一组用例要求占用低于 80%。");
        Assert.Empty(CompactionRequests(gateway));
        Assert.Equal(0, host.Conversation.CompactionCount);
    }

    [Fact]
    public async Task Occupancy_at_eighty_percent_compacts_before_sending()
    {
        // 一直聊到占用越过 80%：整理会自动发生，而且发生在越线那一轮之前。
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        await TalkUntilFirstCompactionAsync(host);

        Assert.Equal(1, host.Conversation.CompactionCount);
        Assert.Single(CompactionRequests(gateway));
        // 整理之后占用必须真的降下来
        Assert.True(LastRatio(host) < ContextBudget.AutoCompactRatio, "整理之后占用应当回到 80% 以下。");
    }


    [Fact]
    public async Task Automatic_compaction_keeps_at_least_the_last_two_rounds_and_the_first_kept_turn_is_a_player_turn()
    {
        // 靠对话本身变长把占用顶上去，不用假用量。
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        await TalkUntilAsync(host, h => h.Conversation.CompactionCount >= 1);

        // 更早的轮次被备忘取代；保留部分从玩家的提问开始，且不少于最近 2 轮
        Assert.Equal(1, host.Conversation.CompactionCount);
        Assert.Equal("本局备忘：已经查过书房。", host.Conversation.Memo);
        Assert.True(host.Conversation.Turns.Count >= 4, "至少要保留最近 2 轮。");
        Assert.Equal("user", host.Conversation.Turns[0].Role);

        // 最早那一轮必须已经被整理进备忘
        Assert.DoesNotContain(
            host.Conversation.Turns,
            turn => turn.Role == "user" && turn.Content.StartsWith("第 0 轮", StringComparison.Ordinal));
    }


    [Fact]
    public async Task Second_compaction_contains_the_previous_memo_and_replaces_it()
    {
        // 靠对话变长连续整理两次，第二份备忘里要能看到第一份。
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        await TalkUntilAsync(host, h => h.Conversation.CompactionCount >= 1);
        Assert.Equal(1, host.Conversation.CompactionCount);
        Assert.Equal("本局备忘：已经查过书房。", host.Conversation.Memo);

        gateway.CompactionMemo = "第二份备忘";
        await TalkUntilAsync(host, h => h.Conversation.CompactionCount >= 2);

        Assert.Equal(2, host.Conversation.CompactionCount);
        Assert.Equal("第二份备忘", host.Conversation.Memo);

        // 第二次整理请求里带着旧备忘
        var compactions = CompactionRequests(gateway);
        Assert.Contains("本局备忘：已经查过书房。", compactions[1].Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compaction_request_uses_the_same_system_message_as_a_normal_turn()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        await TalkUntilAsync(host, h => h.Conversation.CompactionCount >= 1);

        var compaction = Assert.Single(CompactionRequests(gateway));
        var turn = gateway.Requests.Last(r => !IsCompaction(r));
        Assert.Equal(turn.Messages[0].Content, compaction.Messages[0].Content);
    }

    [Fact]
    public async Task Compaction_request_forbids_filling_in_details_from_the_host_materials()
    {
        // 人工验收里备忘把未发放的卡面细节写了进去：整理请求必须明确禁止从主持资料补充。
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        await TalkUntilAsync(host, h => h.Conversation.CompactionCount >= 1);

        var compaction = Assert.Single(CompactionRequests(gateway));
        var prompt = compaction.Messages[^1].Content;
        Assert.Contains(
            "只能写【要整理的对话】和【之前的备忘】里实际出现过的内容，不能从主持资料补充任何细节",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("玩家没有亲口读出来的卡面内容一律不写", prompt, StringComparison.Ordinal);
    }

    // ===== 整理失败 =====

    [Fact]
    public async Task Failed_compaction_keeps_the_conversation_and_still_sends_the_turn()
    {
        var gateway = new ScriptedGateway { FailCompaction = true };
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        var before = gateway.Requests.Count;
        await TalkUntilAsync(host, h => h.Messages.Any(m => m.IsEvent && m.Text.Contains("整理较早的对话失败", StringComparison.Ordinal)));

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
    public async Task Compact_command_is_unavailable_when_there_is_nothing_left_to_compact()    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        Assert.False(host.CompactCommand.CanExecute(null));

        // 1 轮 = 2 条：保留 2 轮之后前面什么都不剩
        host.InputText = "第一问";
        await host.SendAsync();
        Assert.False(host.CompactCommand.CanExecute(null));

        // 2 轮 = 4 条：起点被挪到第一条 user 上，前面仍然没有可整理的内容
        host.InputText = "第二问";
        await host.SendAsync();
        Assert.Equal(4, host.Conversation.Turns.Count);
        Assert.False(host.CompactCommand.CanExecute(null));

        // 3 轮 = 6 条：前面还剩一条 assistant 与它之前的 user
        host.InputText = "第三问";
        await host.SendAsync();
        Assert.Equal(6, host.Conversation.Turns.Count);
        Assert.True(host.CompactCommand.CanExecute(null));
    }

    [Fact]
    public async Task Manual_compaction_sends_the_rounds_before_the_last_two()
    {
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        for (var i = 0; i < 3; i++)
        {
            host.InputText = $"第 {i} 问";
            await host.SendAsync();
        }

        Assert.True(host.CompactCommand.CanExecute(null));

        host.CompactCommand.Execute(null);
        await WaitUntilAsync(() => host.Conversation.CompactionCount == 1);

        // 整理请求里必须真有要整理的对话，而不是一份空清单
        var compaction = Assert.Single(CompactionRequests(gateway));
        var prompt = compaction.Messages[^1].Content;
        var dialogue = prompt[prompt.IndexOf("【要整理的对话】", StringComparison.Ordinal)..];
        Assert.Contains("玩家：第 0 问", dialogue, StringComparison.Ordinal);
        Assert.DoesNotContain("玩家：第 2 问", dialogue, StringComparison.Ordinal);
    }

    // ===== 窗口本身就装不下 =====

    [Fact]
    public async Task A_tiny_window_sends_a_hint_instead_of_compacting()
    {
        // 主持资料自己就占了九成以上：最近 2 轮之外没有可整理的内容，
        // 压缩帮不上忙，只能提示调大窗口，而且不丢任何轮次。
        var gateway = new ScriptedGateway();
        var host = await NewHostAsync(gateway, TinyWindow);
        await host.LoadPackageAsync(HeavyHandbookPackage());

        Assert.True(LastRatio(host) >= 1.0, "这一组用例要求主持资料自己就把窗口占满。");
        Assert.Equal(0, ContextBudget.KeepFromIndex(host.Conversation, TinyWindow));

        await SendAsync(host, "第一个问题");
        await SendAsync(host, "第二个问题");

        Assert.Empty(CompactionRequests(gateway));
        Assert.Contains(
            host.Messages,
            m => m.IsEvent && m.Text.Contains("占满上下文窗口", StringComparison.Ordinal));
        // 不丢任何轮次：说过的都在
        Assert.Equal(4, host.Conversation.Turns.Count);
    }

    /// <summary>主持资料本身就撑满小窗口的剧本包：手册段写得很长。</summary>
    private static ScriptPackage HeavyHandbookPackage()
    {
        var handbook = new string('册', 4_000);
        var page = (int n) => new CanonicalSegment
        {
            SegmentId = $"seg-重手册-{n}",
            OriginalText = handbook,
            SourceSpanIds = [$"hs{n}"],
            Visibility = SegmentVisibility.Private,
        };

        var spans = Enumerable.Range(1, 3).Select(n => new SourceSpan
        {
            SpanId = $"hs{n}",
            FileId = "heavy-file",
            PageNumber = n,
            BlockNumber = 1,
            ToolLocator = $"doc:heavy/page:{n}/block:1",
            TextHash = ManifestFingerprint.ComputeSha256Text("重手册页 " + n),
            OriginalText = "重手册页 " + n,
        }).ToArray();

        return new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = "pkg-heavy-handbook",
            Version = "1",
            Title = "重手册合成剧本",
            PlayerCount = 5,
            ManifestHash = new string('c', 64),
            SourceFiles =
            [
                new SourceFile
                {
                    FileId = "heavy-file",
                    PrivatePath = "heavy.pdf",
                    ContentHash = new string('a', 64),
                    PageCount = 3,
                    Origin = SourceOrder.PageOrder,
                    ToolIdentity = "synthetic",
                    ParseState = FileParseState.Complete,
                },
            ],
            Spans = spans,
            Segments = [page(1), page(2), page(3)],
            Roles = [],
            Phases =
            [
                new ScriptPhase
                {
                    PhaseId = "phase-read",
                    Title = "阅本",
                    PublicSegmentIds = [],
                    AllowedActions = [PublicAction.Start, PublicAction.Advance, PublicAction.Pause],
                    Conditions = ["读完自己的本"],
                    MaterialIds = [],
                    IsVotePhase = false,
                    IsRevealPhase = false,
                    SourceSpanIds = [],
                },
            ],
            Materials = [],
            Truth = new TruthSection
            {
                RevealedSegmentIds = [],
                PrivateText = string.Empty,
                IsConfirmedByHuman = false,
            },
            Validation = PackageValidationState.Ready,
        };
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

    private async Task<HostViewModel> NewHostAsync(ScriptedGateway gateway) =>
        await NewHostAsync(gateway, Window);

    private async Task<HostViewModel> NewHostAsync(ScriptedGateway gateway, int contextWindow)
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var settings = ToolSettings.Default with
        {
            ModelId = "test-model",
            AutoRead = false,
            ContextWindowTokens = contextWindow,
        };

        var host = new HostViewModel(
            new HostDirector(gateway, new SilentTts()),
            () => settings,
            new HostIntentRouter(),
            new CommandDispatcher(store),
            store,
            new SilentTts());
        gateway.Attach(host);
        return host;
    }

    private static List<MagpieCompletionRequest> CompactionRequests(ScriptedGateway gateway) =>
        gateway.Requests.Where(IsCompaction).ToList();

    private static async Task SendAsync(HostViewModel host, string text)
    {
        host.InputText = text;
        await host.SendAsync();
    }

    /// <summary>一条足够长的玩家提问：约 400 个汉字，逼近真实的长句子。</summary>
    private static string LongQuestion(int index) => $"第 {index} 轮：" + new string('问', 400);

    /// <summary>
    /// 一直聊下去，直到条件成立（最多 40 轮）。
    /// 占用靠对话本身变长堆起来，不靠假用量。
    /// </summary>
    private static async Task TalkUntilAsync(HostViewModel host, Func<HostViewModel, bool> done)
    {
        for (var i = 0; i < 40 && !done(host); i++)
        {
            await SendAsync(host, LongQuestion(i));
        }
    }

    /// <summary>一直聊到刚好发生第一次整理为止。</summary>
    private static async Task TalkUntilFirstCompactionAsync(HostViewModel host)
    {
        for (var i = 0; i < 40 && host.Conversation.CompactionCount == 0; i++)
        {
            await SendAsync(host, LongQuestion(i));
        }
    }

    /// <summary>当前上下文占用比例，算法与主持台内部一致。</summary>
    private static double LastRatio(HostViewModel host) =>
        ContextBudget.UsageRatio(
            host.Conversation,
            HostNarration.BuildSystemPrompt(host.Session.Package!),
            BuildNextUserMessage(host),
            Window);

    /// <summary>主持台下一轮会发出的最后一条 user 消息，用来算占用。</summary>
    private static string BuildNextUserMessage(HostViewModel host) =>
        HostNarration.BuildAskUserMessage(
            host.Session.Package!,
            host.Session.Session!,
            host.InputText,
            host.Conversation.Memo);

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

        /// <summary>
        /// 每次模型调用返回的用量；为空时按请求内容估算。
        ///
        /// 需要稳定复现某个占用比例时改设 <see cref="UsageFor"/>：
        /// 固定值只在它自己那一轮生效，下一轮再发就换成下一轮的读数了。
        /// </summary>
        public MagpieUsage? Usage { get; set; }

        /// <summary>按这一轮的情况算用量，优先级高于 <see cref="Usage"/>。</summary>
        public Func<HostViewModel, MagpieCompletionRequest, MagpieUsage?>? UsageFor { get; set; }

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

            var usage = (UsageFor is { } provider ? provider(_host!, request) : null) ?? Usage ?? RequestUsage(request);
            return Task.FromResult(new MagpieCompletion(ReplyFor(request), usage));
        }

        /// <summary>当前这套夹具正在跑的宿主，UsageFor 用它读对话状态。</summary>
        private HostViewModel? _host;

        public void Attach(HostViewModel host) => _host = host;

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
