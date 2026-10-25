using System.Text;
using LocalDm.Core.Tablet;

namespace LocalDm.Infrastructure.Storage;

/// <summary>
/// 把一局导出成 UTF-8 文本：操作时间线、DM 对话、平板公开消息。含剧情，只写在本机。
/// 「重新开始」会删掉这一局的记录，删之前先归档一份到数据目录的 playlogs/，供试玩后查问题。
/// </summary>
public static class SessionExport
{
    public static async Task<(string Text, int Events, int Turns, int Messages)> BuildAsync(
        SessionStore store, string sessionId, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine($"# 本局导出 {sessionId}").AppendLine();

        var events = await store.ReadEventsAsync(sessionId, cancellationToken);
        text.AppendLine($"## 操作时间线（{events.Count} 条）").AppendLine();
        foreach (var e in events)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(e.CommittedAtUnixMs).ToLocalTime();
            text.AppendLine($"- {at:MM-dd HH:mm:ss} {e.Type} {e.SafePayload}");
        }

        text.AppendLine();
        var conversation = await store.LoadDmConversationAsync(sessionId, cancellationToken);
        var turns = conversation?.Turns ?? [];
        text.AppendLine($"## DM 对话（{turns.Count} 轮，整理过 {conversation?.CompactionCount ?? 0} 次）").AppendLine();
        if (!string.IsNullOrWhiteSpace(conversation?.Memo))
        {
            text.AppendLine("### 备忘").AppendLine(conversation.Memo).AppendLine();
        }

        for (var i = 0; i < turns.Count; i++)
        {
            text.AppendLine($"### {i} {turns[i].Role}").AppendLine(turns[i].Content).AppendLine();
        }

        // 公开消息按序号分页读完。
        var messages = new List<PublicMessage>();
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

        return (text.ToString(), events.Count, turns.Count, messages.Count);
    }

    /// <summary>
    /// 归档到 playlogs/&lt;剧本名&gt;-&lt;时间&gt;.md。这一局什么都没发生时不写，返回 null。
    /// </summary>
    public static async Task<string?> ArchiveAsync(
        SessionStore store, string sessionId, string title, CancellationToken cancellationToken)
    {
        var (text, events, turns, messages) = await BuildAsync(store, sessionId, cancellationToken);
        if (events + turns + messages == 0)
        {
            return null;
        }

        var safeTitle = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var folder = Path.Combine(store.DataFolder, "playlogs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), cancellationToken);
        return path;
    }
}
