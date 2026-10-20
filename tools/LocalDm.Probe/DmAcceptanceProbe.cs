using System.Text;
using System.Text.Json;
using LocalDm.App;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Magpie;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 方案第 5 节的人工验收：由程序扮演坐在桌边操作软件的真人。
///
/// 它不另写一套消息拼装或压缩逻辑：拉起来的都是真实的 HostViewModel，
/// 提问、自动压缩、手动压缩、存档、恢复全走软件自己的那条路径。
/// 模型用真实的 MagpieGateway（地址、model ID、caller key 取自用户的设置），
/// 朗读换成一个不出声的替身，上下文长度只在内存里改。
/// </summary>
public static class DmAcceptanceProbe
{
    /// <summary>
    /// 第 9 条用的窗口：不另调小，就按用户设置里的真实值跑。
    /// 2026-10-20 第一次验收把窗口调到 32768，压缩只压掉 1 轮，看不出备忘有没有起效；
    /// 这回按真实值跑，触发一次压缩就会压下前面一大段。
    /// </summary>
    private static readonly int? SmallWindowTokens = null;

    /// <summary>第 9 条最多聊多少句，防止无限循环。</summary>
    private const int MaxChatTurns = 40;

    public static async Task<int> RunAsync(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var dataRoot = ArgumentValue(args, "--data")
                       ?? Path.Combine(repoRoot, ".appdata", "localdm-data");
        var settingsPath = ArgumentValue(args, "--settings")
                           ?? Path.Combine(dataRoot, "settings.json");
        var outDir = ArgumentValue(args, "--out")
                     ?? Path.Combine(repoRoot, ".local", "dm-acceptance");
        var phase = ArgumentValue(args, "--phase") ?? "story";
        var transcriptPath = Path.Combine(outDir, "transcript.md");

        Directory.CreateDirectory(outDir);

        var packagePath = LatestPackage(dataRoot);
        if (packagePath is null)
        {
            Console.WriteLine("没有找到任何 pkg-*.json 剧本包，停止。");
            return 2;
        }

        var package = LoadPackage(packagePath);
        Console.WriteLine($"剧本包：{Path.GetFileName(packagePath)}");
        Console.WriteLine($"剧本名：{package.Title}；玩家人数：{package.PlayerCount}；校验状态：{package.Validation}");
        Console.WriteLine($"角色本分组={package.Roles.Count} 线索卡分组={package.Materials.Count}");

        // 线索卡为 0 就没有可发放的编号：不继续。
        if (package.Materials.Count == 0)
        {
            Console.WriteLine("! 线索卡分组为 0：不要继续验收，先把剧本包准备好。");
            return 3;
        }

        var settingsStore = new SettingsStore(settingsPath, Path.Combine(dataRoot, "secrets"));
        var userSettings = settingsStore.Load();
        var modelOverride = ArgumentValue(args, "--model");
        Console.WriteLine($"模型：{userSettings.ModelId}（地址 {userSettings.MagpieBaseUri}）");
        if (modelOverride is not null)
        {
            // 上游聚合路由（auto/…、group/auto-…）会把 system 消息丢掉，
            // 验收必须用一个真的能收到主持资料的模型：只在内存里覆盖，不写回设置文件。
            Console.WriteLine($"验收覆盖模型：{modelOverride}");
        }

        // 存档复制一份出来用，原文件不动。
        var workDb = CopyDatabase(dataRoot, outDir);
        Console.WriteLine($"存档副本：{workDb}");

        var effectiveModelId = modelOverride ?? userSettings.ModelId;
        var writer = new TranscriptWriter(transcriptPath, package, effectiveModelId);
        try
        {
            return phase switch
            {
                "story" => await RunStoryPhaseAsync(package, settingsPath, dataRoot, workDb, writer, modelOverride),
                "chat" => await RunChatPhaseAsync(package, settingsPath, dataRoot, writer, modelOverride),
                _ => 2,
            };
        }
        finally
        {
            writer.Flush();
            Console.WriteLine();
            Console.WriteLine("对话记录：" + transcriptPath);
        }
    }

    private static async Task<int> RunStoryPhaseAsync(
        ScriptPackage package,
        string settingsPath,
        string dataRoot,
        string workDb,
        TranscriptWriter writer,
        string? modelOverride)
    {
        using var store = new SessionStore(workDb);
        await store.InitializeAsync(CancellationToken.None);
        await store.RegisterPackageAsync(package, CancellationToken.None);

        using var host = NewHost(settingsPath, dataRoot, workDb, modelOverride);
        await host.LoadPackageAsync(package);
        if (host.Session.Package is null)
        {
            Console.WriteLine("装载剧本包失败：" + host.Status);
            return 4;
        }

        var loaded = host.Session.Package;
        var settingsNow = EffectiveSettings(settingsPath, dataRoot, modelOverride);
        Console.WriteLine($"装载后：包={loaded.PackageId} 段数={loaded.Segments.Count} 角色={loaded.Roles.Count} 材料={loaded.Materials.Count}");
        Console.WriteLine($"system 长度={HostNarration.BuildSystemPrompt(loaded).Length} 字；窗口={settingsNow.ContextWindowTokens}；AutoRead={settingsNow.AutoRead}");

        // 开局：和真人点「开始这一局」走同一条路径。
        await StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.Start, null, null));
        writer.WriteHeader(host.Session.PhaseTitle, host.ContextUsageText);

        await AskAsync(host, writer, "1", "你好，你是什么模型？");
        await AskAsync(host, writer, "2", "我们刚开始，要先干什么？");
        await AskAsync(host, writer, "2b", "接下来呢？");
        await AskAsync(host, writer, "3", "不要再重复规则了，直接说现在该做什么");
        await AskAsync(host, writer, "4", "我们去调查书房");
        await AskAsync(host, writer, "5", "地下室一号房能直接进吗？");
        await AskAsync(host, writer, "6a", "谁是凶手？");
        await AskAsync(host, writer, "6b", "阿杰和阿海是不是同一个人？");

        // 第 7 条：问下一步，确认没有弹出推进提案。
        var revisionBefore = host.Session.Revision;
        await AskAsync(host, writer, "7", "下一步我该做什么？");
        writer.Note(
            "7",
            $"提案：{DescribeProposal()}；推进到下一环节的提案没有出现："
            + (host.Session.Revision == revisionBefore ? "是" : "否（revision 变了）"));

        // 第 8 条：按阶段面板的正常命令推进到揭秘并确认揭秘。
        var revealLog = await AdvanceToRevealAsync(host);
        writer.Note("8", revealLog);
        await AskAsync(host, writer, "8a", "讲一下真相");
        await AskAsync(host, writer, "8b", "凶器最后在哪里？");

        // 第 9–12 条另开一局：从未揭秘状态开始，换一份全新的存档。
        return await RunChatPhaseAsync(package, settingsPath, dataRoot, writer, modelOverride);
    }

    /// <summary>第 9–12 条：把窗口临时调到 32768，一直聊到自动压缩，再验记忆与恢复。</summary>
    private static async Task<int> RunChatPhaseAsync(
        ScriptPackage package,
        string settingsPath,
        string dataRoot,
        TranscriptWriter writer,
        string? modelOverride)
    {
        var freshDb = Path.Combine(Path.GetDirectoryName(writer.Path)!, "sessions-chat.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = freshDb + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        using var store = new SessionStore(freshDb);
        await store.InitializeAsync(CancellationToken.None);
        await store.RegisterPackageAsync(package, CancellationToken.None);

        using var host = NewHost(settingsPath, dataRoot, freshDb, modelOverride, SmallWindowTokens);
        await host.LoadPackageAsync(package);
        await StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.Start, null, null));
        writer.WriteChatHeader(host.Session.PhaseTitle, host.ContextUsageText, SmallWindowTokens);

        var triggeredAt = 0;
        var usageBefore = host.ContextUsageText;
        var chatStartedAt = host.Messages.Count;

        // 手动整理按钮要在第 9 条那段桌边对话之前问：那之后记忆里已经压成备忘，按钮随时可点。
        var manualOpenBeforeChat = host.CompactCommand.CanExecute(null);

        // 第 10 条的前置：先让玩家亲口说一句「别再重复规则了」，
        // 压缩之后再问「我之前让你别做什么」，问的是这句话还在不在。
        const string NoRepeatRequest = "别再重复规则了";
        var repeatBefore = host.Messages.Count;
        host.InputText = NoRepeatRequest;
        await host.SendAsync();
        await WaitIdleAsync(host);
        writer.AddChat(
            0,
            NoRepeatRequest,
            host.Messages.Skip(repeatBefore).FirstOrDefault(m => m.IsModelReply)?.Text ?? "（没有回复）",
            host.ContextUsageText,
            host.ContextUsageText,
            null);

        for (var i = 0; i < MaxChatTurns; i++)
        {
            var line = DeskLine(i, DeskLineReach());
            var before = host.Messages.Count;
            var usageAtStart = host.ContextUsageText;
            host.InputText = line;
            await host.SendAsync();
            await WaitIdleAsync(host);

            var reply = host.Messages.Skip(before).FirstOrDefault(m => m.IsModelReply)?.Text ?? "（没有回复）";
            var compactionEvent = host.Messages.Skip(before)
                .FirstOrDefault(m => m.IsEvent && m.Text.Contains("已整理较早的对话", StringComparison.Ordinal));
            if (compactionEvent is not null && triggeredAt == 0)
            {
                triggeredAt = i + 1;
                usageBefore = usageAtStart;
                writer.AddChat(i + 1, line, reply, usageAtStart, host.ContextUsageText, compactionEvent.Text);
                Console.WriteLine($"[9] 第 {triggeredAt} 句触发了自动压缩；压缩后占用 {host.ContextUsageText}");
                break;
            }

            writer.AddChat(i + 1, line, reply, usageAtStart, host.ContextUsageText, null);
        }

        writer.Note(
            "9",
            triggeredAt == 0
                ? $"聊了 {MaxChatTurns} 句都没有触发自动压缩；当前占用 {host.ContextUsageText}。"
                : $"第 {triggeredAt} 句之后出现「已整理较早的对话」；触发前 {usageBefore}，之后 {host.ContextUsageText}。");

        writer.Note("9-按钮", $"第 9 条开始前手动整理按钮可执行={manualOpenBeforeChat}。");

        // 第 10 条：压缩之后追问记忆。把第 9 条触发压缩到第 10 条之间的对话原样留在这里，
        // 评审要核对「备忘起效」还是「原话还在」，得看得见这段时间里问过什么。
        foreach (var message in host.Messages.Skip(chatStartedAt))
        {
            writer.AddChatLine(message.IsModelReply ? "主持" : message.IsEvent ? "事件" : "玩家", message.Text);
        }

        await AskAsync(host, writer, "10a", "我们查过哪些地方了？");
        await AskAsync(host, writer, "10b", "我之前让你别做什么？");

        // 第 11 条：手动压缩一次，只看界面上的次数与备忘长度（备忘正文不上屏，也不进评审记录）。
        // 按钮能不能点要在点之前问：RunTurnAsync 中途才放开忙碌状态，点完再问永远是 false。
        var manualOpen = host.CompactCommand.CanExecute(null);
        var compactionsBefore = host.Conversation.CompactionCount;
        host.CompactCommand.Execute(null);
        await WaitIdleAsync(host);
        await Task.Delay(500);
        writer.Note(
            "11",
            $"点击前 CompactCommand 可执行={manualOpen}；"
            + $"整理次数 {compactionsBefore} → {host.Conversation.CompactionCount}；"
            + $"界面显示「{host.CompactionCountText}」；"
            + $"备忘长度={(host.Memo is null ? 0 : host.Memo.Length)} 字；"
            + $"备忘里没依据的短句={UnsupportedMemoCount(host)} 条；"
            + $"最近事件：{string.Join(" / ", host.Messages.Where(m => m.IsEvent).Select(m => m.Text).TakeLast(3))}");

        // 第 12 条：释放这个主持台，用同一份存档新建一个，装载同一个包并恢复。
        writer.Note("12", "用同一份存档新建主持台并恢复：");
        var resumed = await ResumeInNewHostAsync(package, settingsPath, dataRoot, freshDb, modelOverride, writer);
        return resumed;
    }

    /// <summary>检查备忘有没有写进没依据的内容：沿用软件里那道护栏，只回报条数，不打印备忘。</summary>
    private static int UnsupportedMemoCount(HostViewModel host)
    {
        if (string.IsNullOrEmpty(host.Memo) || host.Session.Package is not { } package)
        {
            return 0;
        }

        return DmMemoCheck.UnsupportedTerms(host.Memo, host.Conversation, package).Count;
    }

    /// <summary>第 12 条：新建一个主持台，恢复同一局，检查屏幕没有重播，再接着问。</summary>
    private static async Task<int> ResumeInNewHostAsync(
        ScriptPackage package,
        string settingsPath,
        string dataRoot,
        string databasePath,
        string? modelOverride,
        TranscriptWriter writer)
    {
        using var reopened = NewHost(settingsPath, dataRoot, databasePath, modelOverride, SmallWindowTokens);
        await reopened.LoadPackageAsync(package);
        var screenAfterLoad = reopened.Messages.Count;
        await reopened.ResumeAsync();
        await WaitIdleAsync(reopened);
        var screenAfterResume = reopened.Messages.Count;

        var note = new StringBuilder();
        note.Append($"装载后屏幕消息={screenAfterLoad}，恢复后={screenAfterResume}；");
        note.Append($"恢复后环节={reopened.Session.PhaseTitle}，状态={reopened.Session.SessionStateText}；");
        note.Append($"旧对话没有重播：{(screenAfterResume - screenAfterLoad <= 2 ? "是" : "否")}；");

        await AskAsync(reopened, writer, "12", "我们刚才聊到哪了？");
        note.Append($"记忆条数={reopened.Conversation.Turns.Count}，备忘={(string.IsNullOrEmpty(reopened.Memo) ? "无" : "有")}；");
        writer.Note("12", note.ToString());
        return 0;
    }

    /// <summary>第 9 条用的桌边对话：问规则、讨论线索、互相怀疑、去调查不同地方，一句都不一样。</summary>
    /// <param name="index">第几句。</param>
    /// <param name="reach">这一句把同一件事说几遍，见 <see cref="DeskLineReach"/>。</param>
    private static string DeskLine(int index, int reach)
    {
        string[] lines =
        [
            "我们先把规则过一遍：调查一次要花多少时间？",
            "时间标记一共有多少个？用完了会怎么样？",
            "商量一下先去哪儿，我提议去二楼看看房间。",
            "二楼一共有几个房间？我们五个人分别住在哪？",
            "我觉得管家吉叔说话吞吞吐吐的，有点可疑。",
            "先别急着指认，先把一楼的线索翻一翻。",
            "去大厅看看，那边应该挂着钥匙。",
            "厨房里会有什么？要不要花十分钟查一下？",
            "厕所那个位置有点怪，为什么提到厕所门口？",
            "地下室的钥匙到底在哪里能找到？",
            "我们先派一个人去地下室门口确认一下能不能进。",
            "如果一间房进不去，是不是说明还缺一把钥匙？",
            "刚才那张卡上提到外衣，谁去查一下外衣在哪？",
            "我想确认一下书房到底隔不隔音，这很关键。",
            "书房里那把枪是谁的？有没有人注意到？",
            "我们是不是应该记录一下每人当时在哪？",
            "唐小姐说她一直在二楼，这个说法有问题吗？",
            "阿杰说他只过了一天，这话听着不对劲。",
            "舒望脾气很大，是不是跟她的经历有关？",
            "回忆卡是怎么触发的？关键词必须别人说出来吗？",
            "如果我说不出关键词，能不能直接翻自己的回忆？",
            "抉择卡上的提示是什么意思，要不要花时间去做？",
            "我们现在的线索里有没有提到长发？",
            "为什么馆里每个窗户都装了铁栏杆？",
            "我想查一下主人房，那间房能进吗？",
            "二楼走廊尽头那间是谁住的？",
            "玉馨房里的东西是不是该重点看看？",
            "刚才那张卡提到画像，画像去哪了？",
            "我们是不是漏了某个人没有问过话？",
            "再确认一次：投票是纸笔写，还是口头说？",
            "投票之后是直接进真相吗，还是还有一轮？",
            "这局大概还能查几个地方？时间够不够？",
            "谁还记得第一轮我们查过哪些地方？",
            "我建议把每个人的说法写在纸上对一遍。",
            "有没有人愿意主动说说自己那段时间在做什么？",
        ];

        return string.Join('，', Enumerable.Repeat(lines[index % lines.Length], reach)) + "。";
    }

    /// <summary>
    /// 这一句说几遍。
    ///
    /// 实测校准：真实窗口 65536、主持资料固定占 31%，一句 40 遍（约 1400 字）聊满 40 句只到 71%，
    /// 到不了自动整理的门槛；改成 120 遍（约 4200 字，一段长发言的量）后第 18 句触发，
    /// 占用从 79% 回落到 52%，第 10 条也才能证明备忘接住了被压掉的话。
    /// </summary>
    private static int DeskLineReach() => 120;

    /// <summary>推进到真相环节并确认揭秘，每一步都走阶段面板的同一条命令路径。</summary>
    private static async Task<string> AdvanceToRevealAsync(HostViewModel host)
    {
        var log = new StringBuilder();
        for (var guard = 0; guard < 12; guard++)
        {
            if (host.Session.IsRevealPhase && host.Session.SessionStateText == nameof(SessionState.Playing))
            {
                break;
            }

            foreach (var material in host.Session.PendingMaterials.ToArray())
            {
                await StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.ReleaseMaterials, [material], null));
            }

            if (host.Session.IsVotePhase && !host.Session.HasVoteSummary)
            {
                if (SessionViewModel.TryParseVotes(VoteText(host), out var votes, out var error))
                {
                    await StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.SubmitVoteSummary, null, votes));
                }
                else
                {
                    log.Append($"投票汇总无法解析（{error}）；");
                }
            }

            if (!host.Session.CanAdvance)
            {
                log.Append($"卡在「{host.Session.PhaseTitle}」(state={host.Session.SessionStateText}, "
                           + $"phase={host.Session.Projection?.CurrentPhase.PhaseId}, "
                           + $"actions={(host.Session.Projection is null ? "-" : string.Join(',', host.Session.Projection.CurrentPhase.AllowedActions))}, "
                           + $"canAdvance={host.Session.CanAdvance}, busy={host.Session.Busy}, "
                           + $"blockedByMaterials={host.Session.IsBlockedByMaterials}, "
                           + $"status={host.Session.Status})；");
                break;
            }

            await StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.Advance, null, null));
        }

        log.Append($"当前环节={host.Session.PhaseTitle}；状态={host.Session.SessionStateText}；");
        if (host.Session.IsRevealPhase)
        {
            await StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.Reveal, null, null));
            log.Append($"点「公开真相」后状态={host.Session.SessionStateText}；");
        }

        return log.ToString();
    }

    /// <summary>投票那一步用纸笔汇总的合成票数：五人平均分到五个角色上。</summary>
    private static string VoteText(HostViewModel host)
    {
        var roles = host.Session.Package?.Roles.Select(r => r.RoleId).ToArray() ?? [];
        if (roles.Length == 0)
        {
            return "A=1";
        }

        var each = Math.Max(1, (host.Session.Package?.PlayerCount ?? roles.Length) / roles.Length);
        return string.Join(", ", roles.Select(r => $"{r}={each}"));
    }

    // ===== 与软件同一条路径的调用 =====

    /// <summary>把一句话打进输入框再发送，走 HostViewModel.SendAsync 这一条路。</summary>
    private static async Task AskAsync(HostViewModel host, TranscriptWriter writer, string step, string text)
    {
        var before = host.Messages.Count;
        host.InputText = text;
        await host.SendAsync();
        await WaitIdleAsync(host);

        var reply = host.Messages.Skip(before).FirstOrDefault(m => m.IsModelReply)?.Text ?? "（没有回复）";
        writer.Add(step, text, reply, host.ContextUsageText);
        Console.WriteLine($"[{step}] 问：{text}");
        Console.WriteLine($"[{step}] 答：{FirstLine(reply)}");
    }

    /// <summary>点一下阶段面板上的按钮，并等播音收尾。</summary>
    private static async Task StepAsync(HostViewModel host, Func<Task> action)
    {
        await action();
        await WaitIdleAsync(host);
    }

    /// <summary>等主持台回到空闲：模型与朗读都结束。</summary>
    private static async Task WaitIdleAsync(HostViewModel host)
    {
        for (var i = 0; i < 1200 && (host.IsBusy || host.Session.Busy); i++)
        {
            await Task.Delay(100);
        }
    }

    private static HostViewModel NewHost(
        string settingsPath,
        string dataRoot,
        string databasePath,
        string? modelOverride,
        int? windowOverride = null)
    {
        var settingsStore = new SettingsStore(settingsPath, Path.Combine(dataRoot, "secrets"));
        var seed = settingsStore.Load();
        var magpie = new MagpieGateway
        {
            BaseUri = seed.MagpieBaseUri,
            CallerKey = settingsStore.LoadCallerKey(seed.CallerKeyRef),
        };
        var store = new SessionStore(databasePath);
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new HostViewModel(
            new HostDirector(magpie, new SilentTts()),
            // 每次都从磁盘读当前设置：和软件里一样，设置页刚改的值立刻生效。
            // 验收要换模型或改窗口时，只在内存里覆盖，不写回用户的设置文件。
            () => EffectiveSettings(settingsPath, dataRoot, modelOverride, windowOverride),
            new HostIntentRouter(),
            new CommandDispatcher(store),
            store,
            new SilentTts());
    }

    /// <summary>用户当前设置；验收需要的覆盖项只在内存里改（模型、上下文长度）。</summary>
    private static ToolSettings EffectiveSettings(
        string settingsPath,
        string dataRoot,
        string? modelOverride,
        int? windowOverride = null)
    {
        var settings = new SettingsStore(settingsPath, Path.Combine(dataRoot, "secrets")).Load();
        if (modelOverride is not null)
        {
            settings = settings with { ModelId = modelOverride };
        }

        return windowOverride is { } window ? settings with { ContextWindowTokens = window } : settings;
    }

    /// <summary>
    /// 有没有弹出推进 / 揭秘提案。
    ///
    /// HostViewModel 现在没有提案成员：路由结果只在 SendAsync 里用来识别「压缩上下文」，
    /// 状态意图不会在主持台上变成待确认提案。
    /// </summary>
    private static string DescribeProposal() => "HostViewModel 无提案成员，状态意图不会变成提案";

    private static string FirstLine(string text)
    {
        var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        return first.Length <= 80 ? first : first[..80] + "…";
    }

    // ===== 数据来源 =====

    private static string? LatestPackage(string dataRoot)
    {
        var dir = Path.Combine(dataRoot, "packages");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "pkg-*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    private static ScriptPackage LoadPackage(string path) =>
        JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(path))
        ?? throw new InvalidOperationException("剧本包无法解析。");

    private static string CopyDatabase(string dataRoot, string outDir)
    {
        var source = Path.Combine(dataRoot, "sessions.db");
        var target = Path.Combine(outDir, "sessions.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var from = source + suffix;
            var to = target + suffix;
            if (File.Exists(to))
            {
                File.Delete(to);
            }

            if (File.Exists(from))
            {
                File.Copy(from, to, overwrite: true);
            }
        }

        return target;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LocalDm.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? Directory.GetCurrentDirectory();
    }

    /// <summary>不出声的朗读替身：验收只关心 AI 说了什么。</summary>
    private sealed class SilentTts : ITtsService
    {
        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "验收静音", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop()
        {
        }
    }

    /// <summary>对话记录。这里会有真相，只写到 .local（已在 .gitignore 里）。</summary>
    private sealed class TranscriptWriter
    {
        private readonly StringBuilder _text = new();
        private readonly string _path;
        private readonly ScriptPackage _package;
        private readonly string? _modelId;

        public string Path => _path;

        public TranscriptWriter(string path, ScriptPackage package, string? modelId)
        {
            _path = path;
            _package = package;
            _modelId = modelId;
            _text.AppendLine("# DM 人工验收对话记录");
            _text.AppendLine();
            _text.AppendLine($"- 剧本：{package.Title}（{package.PlayerCount} 人）");
            _text.AppendLine($"- 模型：{modelId}");
            _text.AppendLine($"- 时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            _text.AppendLine();
            _text.AppendLine("> 这里有真相与剧本原文，只放在 .local 下，不进仓库。");
            _text.AppendLine();
        }

        public void WriteHeader(string phaseTitle, string usage)
        {
            _text.AppendLine("## 第一局：揭秘前与揭秘后（第 1–8 条）");
            _text.AppendLine();
            _text.AppendLine($"开局环节：{phaseTitle}；占用：{usage}");
            _text.AppendLine();
        }

        public void WriteChatHeader(string phaseTitle, string usage, int? window)
        {
            _text.AppendLine();
            _text.AppendLine("## 第二局：未揭秘状态（第 9–12 条）");
            _text.AppendLine();
            _text.AppendLine($"开局环节：{phaseTitle}；占用：{usage}；上下文长度：{(window is { } value ? value.ToString() : "用户设置值")}");
            _text.AppendLine();
        }

        public void Add(string step, string question, string reply, string usage)
        {
            _text.AppendLine($"## 第 {step} 条");
            _text.AppendLine();
            _text.AppendLine($"**玩家：** {question}");
            _text.AppendLine();
            _text.AppendLine($"**主持：** {reply}");
            _text.AppendLine();
            _text.AppendLine($"占用：{usage}");
            _text.AppendLine();
        }

        public void AddChat(
            int index,
            string question,
            string reply,
            string usageBefore,
            string usageAfter,
            string? compactionEvent)
        {
            _text.AppendLine($"## 第 9 条 · 桌边第 {index} 句");
            _text.AppendLine();
            _text.AppendLine($"**玩家：** {question}");
            _text.AppendLine();
            _text.AppendLine($"**主持：** {reply}");
            _text.AppendLine();
            _text.AppendLine($"占用：{usageBefore} → {usageAfter}");
            if (compactionEvent is not null)
            {
                _text.AppendLine();
                _text.AppendLine($"**聊天区事件：** {compactionEvent}");
            }

            _text.AppendLine();
        }

        public void AddChatLine(string speaker, string text)
        {
            _text.AppendLine($"**{speaker}：** {text}");
            _text.AppendLine();
        }

        public void Note(string step, string note)
        {
            _text.AppendLine($"### 第 {step} 条的程序检查");
            _text.AppendLine();
            _text.AppendLine(note);
            _text.AppendLine();
        }

        public void Flush() => File.WriteAllText(_path, _text.ToString(), new UTF8Encoding(false));
    }
}








