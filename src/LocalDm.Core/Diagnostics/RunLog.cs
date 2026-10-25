namespace LocalDm.Core.Diagnostics;

/// <summary>
/// 运行日志：一天一个文件（logs/run-yyyyMMdd.log），记事件和原因，供试玩后查问题。
/// 不写剧情原文和凭据：调用方只传事件名、异常类型、已脱敏的原因。
/// 软件启动时设 <see cref="Folder"/>；没设（测试、探针）就只写调试输出。
/// </summary>
public static class RunLog
{
    private static readonly object Gate = new();

    public static string? Folder { get; set; }

    public static void Write(string tag, string detail)
    {
        System.Diagnostics.Debug.WriteLine($"[{tag}] {detail}");
        if (Folder is not { } folder)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(
                    Path.Combine(folder, $"run-{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {tag}: {detail}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不进去也不能影响主持。
        }
    }
}
