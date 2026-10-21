namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 认哪些文件算剧本材料：图片（jpg/jpeg/png/webp）与 PDF，别的都不认。
///
/// 扫描、识别、命令行的取样探针都用这一份判断，不在各处各写一套：
/// 两处判断不一致时，同一份剧本在界面里能导入、在探针里却"看不见"。
/// </summary>
public static class ScriptFileTypes
{
    /// <summary>图片页的扩展名（小写，含点）。</summary>
    public static readonly IReadOnlyList<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>不是材料的杂项文件。</summary>
    public static readonly IReadOnlyList<string> JunkFiles = ["thumbs.db", ".ds_store", "desktop.ini"];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsPdf(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsJunk(string path) =>
        JunkFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>是图片或 PDF，且不是杂项文件。</summary>
    public static bool IsMaterial(string path) =>
        (IsImage(path) || IsPdf(path)) && !IsJunk(path);

    /// <summary>
    /// 一个文件夹里直接可用的材料文件（跳过隐藏文件与系统杂项）。
    /// 顺序不在这里定：页序由调用方按各自的规则排。
    /// </summary>
    public static IReadOnlyList<string> MaterialFiles(string directory) =>
        Directory.GetFiles(directory)
            .Where(path => !IsHidden(path))
            .Where(IsMaterial)
            .ToArray();

    private static bool IsHidden(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.'))
        {
            return true;
        }

        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.Hidden) != 0;
    }
}
