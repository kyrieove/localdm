using System.Text;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 把一局的操作时间线、DM 对话和平板公开消息导出成 UTF-8 文本，供对照试玩问题。
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

        var (text, events, turns, messages) = await SessionExport.BuildAsync(store, sessionId, CancellationToken.None);
        outPath ??= Path.Combine(".local", "exports", $"{sessionId}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, text, new UTF8Encoding(false));

        Console.WriteLine($"已导出：{Path.GetFullPath(outPath)}（操作 {events} 条，DM 对话 {turns} 轮，公开消息 {messages} 条）");
        return 0;
    }
}
