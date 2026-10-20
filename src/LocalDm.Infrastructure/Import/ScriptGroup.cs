namespace LocalDm.Infrastructure.Import;

/// <summary>一个导入分组的类别。扫描先猜，导入的人可以在界面上改。</summary>
public enum ScriptGroupKind
{
    /// <summary>组织者手册：含真相与主持人专用页。</summary>
    Organizer = 0,

    /// <summary>角色本：每位玩家一本，任何角色都不上公共屏。</summary>
    Role = 1,

    /// <summary>线索卡：正面与卡背都算页。</summary>
    Material = 2,

    /// <summary>导入的人明确不要进包的分组。</summary>
    Ignore = 3,
}

/// <summary>
/// 一个材料分组：文件夹（或单个 PDF）里的文件按页序排好。
/// 分组名会进段 ID 与材料编号，必须稳定，所以同名时改用相对路径。
/// </summary>
public sealed record ScriptGroup
{
    /// <summary>分组名：叶子文件夹名；同名时用相对路径（<c>/</c> 连接）。</summary>
    public required string Name { get; init; }

    /// <summary>相对剧本根目录的路径，界面显示用。</summary>
    public required string RelativePath { get; init; }

    /// <summary>已按页序排好的文件绝对路径。</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>页数（含卡背页）。PDF 的真实页数要到识别阶段才知道，这里按文件数算。</summary>
    public int PageCount => Files.Count;

    /// <summary>类别：扫描时猜的，导入的人可改。</summary>
    public ScriptGroupKind Kind { get; init; } = ScriptGroupKind.Role;

    /// <summary>猜测依据，例如「名称含“线索”」。只说名字与路径，不含任何正文。</summary>
    public string GuessReason { get; init; } = string.Empty;

    /// <summary>文件类型摘要，例如 <c>jpg</c> 或 <c>pdf</c>。</summary>
    public string FileTypeLabel
    {
        get
        {
            var kinds = Files
                .Select(path => Path.GetExtension(path).TrimStart('.').ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(kind => kind, StringComparer.Ordinal)
                .ToArray();
            return kinds.Length == 0 ? "—" : string.Join("/", kinds);
        }
    }
}

/// <summary>
/// 扫描结果：分组列表 + 需要导入的人处理的问题。
///
/// <see cref="Issues"/> 里只放问题类别与分组名，不放任何识别文字：
/// 导入的人可能就是这局的玩家。
/// </summary>
public sealed record ScriptScanResult
{
    public required IReadOnlyList<ScriptGroup> Groups { get; init; }

    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>一个组织者手册都没猜到时，必须由导入的人指定，不能跳过。</summary>
    public bool NeedsOrganizerChoice =>
        !Groups.Any(group => group.Kind == ScriptGroupKind.Organizer);

    /// <summary>猜到一个以上组织者手册：现有分层规则只支持一个，要求只保留一个。</summary>
    public bool HasMultipleOrganizerCandidates =>
        Groups.Count(group => group.Kind == ScriptGroupKind.Organizer) > 1;

    /// <summary>组织者手册分组名；还没指定时为 null。</summary>
    public string? OrganizerGroupName =>
        Groups.FirstOrDefault(group => group.Kind == ScriptGroupKind.Organizer)?.Name;

    /// <summary>导入的分组里没有这个目录名时用：挡住开局并点名。</summary>
    public const string OrganizerGroupMissing = "ORGANIZER_GROUP_MISSING";
}
