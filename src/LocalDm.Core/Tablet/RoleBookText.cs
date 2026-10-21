namespace LocalDm.Core.Tablet;

/// <summary>
/// 角色本的一个内容块。正文只排版，不润色、不概括、不补写。
/// <see cref="Kind"/> 决定它怎样呈现；<see cref="SourceSpanId"/> 保留来源映射，
/// 让「这句话出自哪一页哪一块」始终可核对。
/// </summary>
public sealed record RoleBookBlock
{
    /// <summary>稳定块 ID：同一份材料每次生成都必须一样，否则阅读位置无法跨会话恢复。</summary>
    public required string BlockId { get; init; }

    public required RoleBookBlockKind Kind { get; init; }

    /// <summary>正文或标题文字。原样来自识别结果，不做任何改写。</summary>
    public required string Text { get; init; }

    /// <summary>这一块在来源里的位置；图片块用它指向原稿页。</summary>
    public string? SourceSpanId { get; init; }

    /// <summary>
    /// 插图或特殊图案：指向原稿页资产。
    /// 地图、手写信件等无法可靠转成文字的图案走这一项，保留在原位置附近。
    /// </summary>
    public string? ImageAssetId { get; init; }

    /// <summary>插图说明（原文中的图注）；没有则为 null。</summary>
    public string? ImageCaption { get; init; }

    /// <summary>原稿页号（1 起）。仅用于「查看原稿」入口，不参与阅读器分页。</summary>
    public int? SourcePageNumber { get; init; }
}

public enum RoleBookBlockKind
{
    /// <summary>章题。</summary>
    ChapterTitle = 0,

    /// <summary>节题。</summary>
    SectionTitle = 1,

    /// <summary>正文段落。</summary>
    Paragraph = 2,

    /// <summary>插图、地图、手写信件等需保留原图的图案。</summary>
    Figure = 3,

    /// <summary>分隔（原文里的空行或分隔线）。</summary>
    Break = 4,
}

/// <summary>
/// 角色本的一章。章题不混入正文分页：翻页不会跨章悄悄显示下一章。
/// </summary>
public sealed record RoleBookChapter
{
    public required string ChapterId { get; init; }

    public required string Title { get; init; }

    /// <summary>这一章包含的内容块，按阅读顺序排列。</summary>
    public required IReadOnlyList<RoleBookBlock> Blocks { get; init; }
}

/// <summary>
/// 一个角色的整本阅读材料：文本块 + 原稿页核对入口。
/// 角色本正文不进入主持模型上下文；这里只服务于本人平板的只读阅读。
/// </summary>
public sealed record RoleBookText
{
    public required string RoleId { get; init; }

    public required IReadOnlyList<RoleBookChapter> Chapters { get; init; }

    /// <summary>原稿页资产 ID 列表，按页序排列；「查看原稿」入口用它核对。</summary>
    public IReadOnlyList<string> SourcePageAssetIds { get; init; } = [];

    /// <summary>识别完成与页覆盖完整不等于文字完全正确：这里如实记录文字置信情况。</summary>
    public RoleBookTextConfidence Confidence { get; init; } = new();
}

/// <summary>
/// 文字质量的如实记录。识别完成只说明跑完了，页覆盖完整只说明没缺页，
/// 都不说明每个字都对。界面据此提示「可与原稿核对」。
/// </summary>
public sealed record RoleBookTextConfidence
{
    /// <summary>原文块总数。</summary>
    public int TotalBlocks { get; init; }

    /// <summary>识别为空或明显缺字的块数。</summary>
    public int EmptyOrSuspectBlocks { get; init; }

    /// <summary>总字符数；用于准备区显示规模，不显示内容。</summary>
    public int TotalCharacters { get; init; }

    /// <summary>是否有原稿页可核对。</summary>
    public bool HasSourcePages { get; init; }
}
