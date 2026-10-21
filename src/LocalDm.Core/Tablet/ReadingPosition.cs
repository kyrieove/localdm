using System.Text;

namespace LocalDm.Core.Tablet;

/// <summary>
/// 阅读位置。按文本位置而不是只按页码保存：
/// 字号、行距、窗口大小、横竖屏变化后分页结果会变，页码对不上，
/// 但「读到哪里」这个文本偏移量不变。位置 0 是有效值（第一页开头），
/// 不能用 0 表示「没有进度」。
/// </summary>
public readonly record struct ReadingPosition
{
    public ReadingPosition(long characterOffset)
    {
        if (characterOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterOffset), "文本位置不能为负。");
        }

        CharacterOffset = characterOffset;
    }

    /// <summary>从整本开头算起的字符偏移量。</summary>
    public long CharacterOffset { get; }

    /// <summary>没有进度时的起点：第一页开头，而不是「无效」。</summary>
    public static ReadingPosition Start => new(0);
}

/// <summary>
/// 阅读设置。字号与行距变化后重新分页，但保持当前文本位置。
/// </summary>
public sealed record ReadingSettings
{
    public const int MinFontSteps = 16;

    public const int MaxFontSteps = 26;

    public const string SpacingCompact = "1.75";

    public const string SpacingRelaxed = "1.95";

    public const string SpacingLoose = "2.2";

    /// <summary>正文字号（像素）。</summary>
    public int FontSize { get; init; } = 19;

    /// <summary>行距倍数，取值见三个常量。</summary>
    public string LineSpacing { get; init; } = SpacingRelaxed;

    /// <summary>专注模式：隐藏目录与页脚，只留正文。</summary>
    public bool FocusMode { get; init; }

    public static ReadingSettings Clamp(ReadingSettings settings) => settings with
    {
        FontSize = Math.Clamp(settings.FontSize, MinFontSteps, MaxFontSteps),
        LineSpacing = settings.LineSpacing switch
        {
            SpacingCompact => SpacingCompact,
            SpacingLoose => SpacingLoose,
            _ => SpacingRelaxed,
        },
    };
}

/// <summary>
/// 阅读进度的持久化形状。按角色与浏览器会话保存，
/// 不同玩家、不同设备的进度互不影响。
/// </summary>
public sealed record ReadingProgress
{
    public required string SessionKey { get; init; }

    public required string RoleId { get; init; }

    public required ReadingPosition Position { get; init; }

    public ReadingSettings Settings { get; init; } = new();
}

/// <summary>
/// 把阅读进度映射回分页结果，供「恢复到读到的位置」使用。
/// 分页由前端按 DOM 测量产生，这里只负责在给定页起点列表里找最近的一页。
/// </summary>
public static class ReadingPositionMap
{
    /// <summary>
    /// 在按文本偏移量排序的页起点列表里，找到不大于 <paramref name="position"/> 的最后一页。
    /// 位置 0 必须返回第 0 页，不能返回 -1。
    /// </summary>
    public static int PageIndexFor(IReadOnlyList<long> pageStarts, ReadingPosition position)
    {
        ArgumentNullException.ThrowIfNull(pageStarts);
        if (pageStarts.Count == 0)
        {
            return -1;
        }

        var index = 0;
        for (var i = 0; i < pageStarts.Count; i++)
        {
            if (pageStarts[i] <= position.CharacterOffset)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        return index;
    }
}

/// <summary>
/// 阅读器一次进度上报：文本位置 + 当时的阅读设置。
/// 用记录类型而不是元组，Blazor 的 EventCallback 才能直接绑定方法组。
/// </summary>
public sealed record ReadingProgressUpdate(long CharacterOffset, ReadingSettings Settings);

/// <summary>
/// 角色本纯文本展开：把章节与块按阅读顺序拼成一条字符流。
/// 阅读位置、分页、逐字一致性校验都以它为准，避免各按各的规则拼串。
/// 章题只计一次，正文、节题与插图原有文字按阅读顺序计入；空分隔不计字符。
/// 使用 .NET 与浏览器共有的 UTF-16 长度，不把补充字符拆成不完整的代理项。
/// </summary>
public static class RoleBookPlainText
{
    public static string Build(RoleBookText book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var builder = new StringBuilder();
        foreach (var chapter in book.Chapters)
        {
            builder.Append(chapter.Title);
            foreach (var block in chapter.Blocks)
            {
                if (block.Kind is RoleBookBlockKind.ChapterTitle or RoleBookBlockKind.Break)
                {
                    continue;
                }

                builder.Append(block.Text);
                if (block.Kind == RoleBookBlockKind.Figure)
                {
                    builder.Append(block.ImageCaption);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>整本总字符数，用于准备区显示规模。</summary>
    public static int CharacterCount(RoleBookText book) => Build(book).Length;
}
