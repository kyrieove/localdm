namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 剧本导入：扫描文件夹、猜分组类别、识别、建包。
///
/// 原来这套装配逻辑只在命令行工具里（<c>tools/LocalDm.Probe/BuildPackageProbe.cs</c>），
/// 而且认死「以装订结尾的目录」与「线索卡牌」两个目录名。搬到服务里之后，
/// 命令行与界面共用同一份实现，且不再依赖任何具体剧本的目录名。
/// </summary>
public sealed partial class ScriptImportService
{
    private readonly ScriptImportOptions _options;
    private readonly IPageExtractor _extractor;

    public ScriptImportService(ScriptImportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.DataRoot))
        {
            throw new ArgumentException("导入服务必须有运行数据根。", nameof(options));
        }

        _extractor = options.Extractor ?? new MineruPageExtractor(
            options.MineruExecutable,
            options.MineruHome ?? Path.Combine(options.DataRoot, "mineru-home"),
            options.OcrPythonPath);
    }
    /// <summary>组织者手册的关键词：命中任一即算候选。</summary>
    private static readonly string[] OrganizerKeywords = ["组织", "主持", "dm", "手册", "说明", "真相", "组织者"];

    /// <summary>线索卡的关键词。</summary>
    private static readonly string[] MaterialKeywords = ["线索", "卡", "搜证", "抉择", "回忆", "记忆", "地点", "证物"];

    /// <summary>
    /// 扫描文件夹，找出材料分组并猜类别。不读文件内容，不跑识别。
    /// </summary>
    public ScriptScanResult Scan(string storyRoot)
    {
        if (string.IsNullOrWhiteSpace(storyRoot))
        {
            throw new ArgumentException("必须给出剧本文件夹。", nameof(storyRoot));
        }

        var root = Path.GetFullPath(storyRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("找不到这个剧本文件夹。");
        }

        var groups = new List<ScriptGroup>();
        CollectGroups(root, root, groups);
        var unique = MakeNamesUnique(groups);
        var classified = unique.Select(Classify).ToArray();

        var issues = new List<string>();
        if (!classified.Any(group => group.Kind == ScriptGroupKind.Organizer))
        {
            issues.Add(ScriptScanResult.OrganizerGroupMissing);
        }

        if (classified.Count(group => group.Kind == ScriptGroupKind.Organizer) > 1)
        {
            issues.Add("MULTIPLE_ORGANIZER_GROUPS");
        }

        return new ScriptScanResult { Groups = classified, Issues = issues };
    }

    /// <summary>
    /// 递归找分组：直接包含图片的文件夹算一组，每个独立 PDF 各算一组。
    /// 只有子文件夹的中间层不算分组，只用来生成相对路径。
    ///
    /// 同一个文件夹里既有 PDF 又有图片时，PDF 的分组排在图片那组前面
    /// （多个 PDF 按文件名排），这样页序是「先 PDF 后图片」，与人对文件夹的读法一致。
    /// </summary>
    private static void CollectGroups(string root, string current, List<ScriptGroup> groups)
    {
        var files = ScriptFileTypes.MaterialFiles(current);

        // 独立 PDF 各成一组：一本手册、一个角色本各是一个文件时，不该被并成一组。
        foreach (var pdf in files.Where(ScriptFileTypes.IsPdf).OrderBy(f => f, StringComparer.Ordinal))
        {
            groups.Add(new ScriptGroup
            {
                Name = Path.GetFileNameWithoutExtension(pdf),
                RelativePath = RelativeTo(root, current) is { Length: > 0 } dir
                    ? $"{dir}/{Path.GetFileName(pdf)}"
                    : Path.GetFileName(pdf),
                Files = [pdf],
            });
        }

        var images = files.Where(ScriptFileTypes.IsImage)
            .OrderBy(f => PageOrderKey(Path.GetFileNameWithoutExtension(f)))
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToArray();
        if (images.Length > 0)
        {
            groups.Add(new ScriptGroup
            {
                Name = Path.GetFileName(current),
                RelativePath = RelativeTo(root, current),
                Files = images,
            });
        }

        foreach (var directory in Directory.GetDirectories(current).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (IsHidden(directory))
            {
                continue;
            }

            CollectGroups(root, directory, groups);
        }
    }

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

    private static string RelativeTo(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? string.Empty : relative.Replace('\\', '/');
    }

    /// <summary>
    /// 分组名去重：同名时改用相对路径。
    /// 分组名会进段 ID 与材料编号，重名会让两份材料在包里互相顶掉。
    /// </summary>
    private static IReadOnlyList<ScriptGroup> MakeNamesUnique(IReadOnlyList<ScriptGroup> groups)
    {
        var duplicated = groups
            .GroupBy(group => group.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        return groups
            .Select(group => duplicated.Contains(group.Name)
                ? group with { Name = group.RelativePath }
                : group)
            .ToArray();
    }

    /// <summary>
    /// 按分组名与相对路径里的关键词猜类别。
    /// 依据写进 <see cref="ScriptGroup.GuessReason"/>，导入的人能看到软件为什么这么猜。
    /// </summary>
    private static ScriptGroup Classify(ScriptGroup group)
    {
        var organizer = MatchKeyword(group, OrganizerKeywords);
        if (organizer is not null)
        {
            return group with { Kind = ScriptGroupKind.Organizer, GuessReason = organizer };
        }

        var material = MatchKeyword(group, MaterialKeywords);
        if (material is not null)
        {
            return group with { Kind = ScriptGroupKind.Material, GuessReason = material };
        }

        return group with { Kind = ScriptGroupKind.Role, GuessReason = "没有认出关键词，按角色本处理" };
    }

    /// <summary>先看名称，再看路径；命中就返回一句给界面的依据。</summary>
    private static string? MatchKeyword(ScriptGroup group, IReadOnlyList<string> keywords)
    {
        var name = group.Name.ToLowerInvariant();
        foreach (var keyword in keywords)
        {
            if (name.Contains(keyword, StringComparison.Ordinal))
            {
                return $"名称含“{keyword}”";
            }
        }

        var path = group.RelativePath.ToLowerInvariant();
        foreach (var keyword in keywords)
        {
            if (path.Contains(keyword, StringComparison.Ordinal))
            {
                return $"路径含“{keyword}”";
            }
        }

        return null;
    }

    /// <summary>
    /// 页键：正面页取数字本身，卡背取「同号 + 0.5」，保证卡背排在正面之后。
    /// 无法归类的排到最后，但仍保留（不静默丢弃）。
    /// </summary>
    internal static double PageOrderKey(string name)
    {
        var digits = name.TrimEnd('卡', '背');
        if (int.TryParse(digits, out var page))
        {
            return name.Length == digits.Length ? page : page + 0.5;
        }

        return double.MaxValue;
    }
}
