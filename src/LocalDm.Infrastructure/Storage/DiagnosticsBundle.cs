using System.IO.Compression;
using System.Text;

namespace LocalDm.Infrastructure.Storage;

/// <summary>
/// 设置页「导出日志」：把运行日志、崩溃日志、试玩归档、存档里每一局的导出和设置文件打成一个 zip，
/// 方便交给开发者查问题。含对话原文（有剧情）；不含密钥（密钥在 secrets/，不打包）。
/// </summary>
public static class DiagnosticsBundle
{
    public static async Task CreateAsync(SessionStore store, string zipPath, CancellationToken cancellationToken)
    {
        var root = store.DataFolder;
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var folder in new[] { "logs", "playlogs" })
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(dir))
            {
                zip.CreateEntryFromFile(file, $"{folder}/{Path.GetFileName(file)}");
            }
        }

        var settings = Path.Combine(root, "settings.json");
        if (File.Exists(settings))
        {
            zip.CreateEntryFromFile(settings, "settings.json");
        }

        // 还没重新开始的局不在 playlogs 里：现场导出一份。
        foreach (var session in await store.ListSessionsAsync(cancellationToken))
        {
            var (text, _, _, _) = await SessionExport.BuildAsync(store, session.SessionId, cancellationToken);
            await using var entry = zip.CreateEntry($"sessions/{session.SessionId}.md").Open();
            await entry.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellationToken);
        }
    }
}
