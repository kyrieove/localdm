using System.Text;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 把一局的 DM 对话和平板公开消息导出成 UTF-8 文本，供对照试玩问题。
/// 导出含剧情，默认写到不入库的 .local/exports/；终端只打印路径和条数。
/// </summary>
internal static class ExportSessionProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? sessionId = null, dataRoot = null, outPath = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--session": sessionId = args[i + 1]; break;
                case "--data": dataRoot = args[i + 1]; break;
                case "--out": outPath = args[i + 1]; break;
                default:
                    Console.WriteLine($"未知参数：{args[i]}");
                    return 2;
            }
        }

        using var context = await SessionContext.OpenAsync(dataRoot);
        var store = (SessionStore)context.Store;

        // 没指定就导出最近更新的一局。
        sessionId ??= (await store.ListSessionsAsync(CancellationToken.None)).FirstOrDefault()?.SessionId;
        if (sessionId is null)
        {
            Console.WriteLine("数据目录里没有任何一局。");
            return 2;
        }

        var text = new StringBuilder();
        text.AppendLine($"# 本局导出 {sessionId}").AppendLine();

        var conversation = await store.LoadDmConversationAsync(sessionId, CancellationToken.None);
        var turns = conversation?.Turns ?? [];
        text.AppendLine($"## DM 对话（{turns.Count} 轮，整理过 {conversation?.CompactionCount ?? 0} 次）").AppendLine();
        if (!string.IsNullOrWhiteSpace(conversation?.Memo))
        {
            text.AppendLine("### 备忘").AppendLine(conversation.Memo).AppendLine();
        }

        for (int i = 0; i < turns.Count; i++)
        {
            text.AppendLine($"### {i} {turns[i].Role}").AppendLine(turns[i].Content).AppendLine();
        }

        // 公开消息按序号分页读完。
        var messages = new List<LocalDm.Core.Tablet.PublicMessage>();
        long after = 0;
        while (true)
        {
            var page = await store.ReadPublicMessagesAsync(sessionId, after);
            if (page.Count == 0)
            {
                break;
            }

            messages.AddRange(page);
            after = page[^1].Sequence;
        }

        text.AppendLine($"## 平板公开消息（{messages.Count} 条）").AppendLine();
        foreach (var m in messages)
        {
            text.AppendLine($"### {m.Sequence} {m.Author}/{m.Kind} {m.CreatedAt.ToLocalTime():MM-dd HH:mm:ss}")
                .AppendLine(m.Text).AppendLine();
        }

        outPath ??= Path.Combine(".local", "exports", $"{sessionId}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, text.ToString(), new UTF8Encoding(false));

        Console.WriteLine($"已导出：{Path.GetFullPath(outPath)}（DM 对话 {turns.Count} 轮，公开消息 {messages.Count} 条）");
        return 0;
    }
}
