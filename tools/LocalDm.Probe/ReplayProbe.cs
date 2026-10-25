using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Magpie;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 用一局真实存档重放某几句玩家原话：历史取存档里这句之前的原话，局面设成当时的环节，
/// 系统提示词用当前代码。用来检验改过的提示词在真实对话里会不会照做。
/// 输出含剧情，只在本机看。
/// 用法：--replay --session 会话ID --ask "环节ID|玩家原话" [--ask ...] [--then 追问 ...] [--runs 次数] [--model 模型]
/// --then 在模型这次的回复后面接着追问（例如连续说「下一段」），每个 --ask 后都跑一遍。
/// </summary>
internal static class ReplayProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var dataRoot = Value(args, "--data") ?? SessionContext.ResolveDataRoot();
        var sessionId = Value(args, "--session");
        var asks = Values(args, "--ask");
        var thens = Values(args, "--then");
        var runs = int.TryParse(Value(args, "--runs"), out var n) && n > 0 ? n : 1;
        if (sessionId is null || asks.Length == 0)
        {
            Console.WriteLine("用法：--replay --session 会话ID --ask \"环节ID|玩家原话\" [--model 模型]");
            return 2;
        }

        var store = new SessionStore(Path.Combine(dataRoot, "sessions.db"));
        var saved = await store.LoadSessionAsync(sessionId, CancellationToken.None)
                    ?? throw new InvalidOperationException($"存档里没有 {sessionId}");
        var snapshot = await store.LoadDmConversationAsync(sessionId, CancellationToken.None)
                       ?? throw new InvalidOperationException("这局没有 DM 对话记录");
        var packagePath = Directory.GetFiles(Path.Combine(dataRoot, "packages"), $"{saved.PackageId}-{saved.PackageVersion}.json").Single();
        var package = DmAcceptanceProbe.LoadPackage(packagePath);

        var settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.json"), Path.Combine(dataRoot, "secrets"));
        var settings = settingsStore.Load();
        var modelId = Value(args, "--model") ?? settings.ModelId ?? throw new InvalidOperationException("设置里没有模型");
        using var gateway = new MagpieGateway { BaseUri = settings.MagpieBaseUri, CallerKey = settingsStore.LoadCallerKey(settings.CallerKeyRef) };
        Console.WriteLine($"剧本：{package.Title}；模型：{modelId}");

        foreach (var ask in asks)
        {
            var parts = ask.Split('|', 2);
            var (phaseId, text) = (parts[0], parts[1]);
            var index = snapshot.Turns.ToList().FindIndex(t => t.Role == "user" && t.Content == text);
            if (index < 0)
            {
                Console.WriteLine($"\n## 找不到这句原话：{text}");
                continue;
            }

            var conversation = new DmConversation();
            foreach (var turn in snapshot.Turns.Take(index))
            {
                if (turn.Role == "user") { conversation.AppendUser(turn.Content); }
                else { conversation.AppendAssistant(turn.Content, null); }
            }

            var session = saved with { State = SessionState.Playing, CurrentPhaseId = phaseId, ReleasedMaterialIds = [] };
            var original = index + 1 < snapshot.Turns.Count ? snapshot.Turns[index + 1].Content : "（无）";
            Console.WriteLine($"\n## 第 {index} 轮（环节 {phaseId}）玩家：{text}");
            Console.WriteLine($"\n### 当晚 DM\n{original}");

            var baseTurns = conversation.Turns.ToArray();
            for (var run = 1; run <= runs; run++)
            {
                // 每次从同一段历史出发；--then 的追问接在模型这次自己的回复后面。
                var chat = new DmConversation();
                foreach (var turn in baseTurns)
                {
                    if (turn.Role == "user") { chat.AppendUser(turn.Content); }
                    else { chat.AppendAssistant(turn.Content, null); }
                }

                foreach (var said in new[] { text }.Concat(thens))
                {
                    var messages = HostNarration.BuildAskMessages(package, session, said, chat, modelId);
                    var reply = await gateway.CompleteAsync(new MagpieCompletionRequest { ModelId = modelId, Messages = messages }, CancellationToken.None);
                    Console.WriteLine($"\n### 第 {run} 次 · 玩家：{said}\n{reply}");
                    chat.AppendUser(said);
                    chat.AppendAssistant(reply, null);
                }
            }
        }

        return 0;
    }

    private static string[] Values(string[] args, string name) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToArray();

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
