using System.Text;

namespace LocalDm.App.ViewModels;

/// <summary>真相文本里的块类型。</summary>
public enum TruthBlockKind
{
    /// <summary>小标题（原文里的 ## 标题）。</summary>
    Heading = 0,

    /// <summary>等宽块：原文用空格对齐的表格或时间线。</summary>
    Code = 1,

    /// <summary>管道表格：原文里 | 分隔的行。</summary>
    Table = 2,

    /// <summary>普通段落。</summary>
    Paragraph = 3,
}

/// <summary>真相文本里的一个块。</summary>
public sealed class TruthBlock
{
    public TruthBlockKind Kind { get; init; }

    /// <summary>标题与段落的文字。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>等宽块的每一行。</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>管道表格的每一行（已按单元格拆开）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];

    /// <summary>表格的列数。</summary>
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

/// <summary>
/// 真相文本的解析与清理。
///
/// 真相原文是剧本扫描件转出来的 Markdown：混着 <c>##</c> 标题、<c>```</c> 代码棁、
/// 管道表格、还有扫描页码。原样上屏会把 Markdown 语法直接糊到桌面上，
/// 也会让等宽表格的列位看起来在漂。这里把它拆成可渲染的块。
/// </summary>
public static class TruthDocument
{
    /// <summary>清理后的纯文本：去掉 Markdown 语法与扫描页码。给窄栏阅读用。</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var inFence = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                // 围栏里的内容原样保留：那里的「07」可能是真实数据，不是页码。
                builder.AppendLine(line);
                continue;
            }

            if (IsScanPageNumber(line))
            {
                continue;
            }

            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                line = line.TrimStart().TrimStart('#').Trim();
                if (line.Length == 0)
                {
                    continue;
                }
            }

            builder.AppendLine(line);
        }

        return builder.ToString().Trim('\n');
    }

    /// <summary>把真相原文拆成可渲染的块。</summary>
    public static IReadOnlyList<TruthBlock> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var blocks = new List<TruthBlock>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var raw = lines[index].TrimEnd();

            if (raw.Trim().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var code = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index].TrimEnd());
                    index++;
                }

                index++; // 跳过收尾的 ```
                if (code.Count > 0)
                {
                    blocks.Add(new TruthBlock
                    {
                        Kind = TruthBlockKind.Code,
                        Lines = code,
                    });
                }

                continue;
            }

            if (raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                var heading = raw.TrimStart().TrimStart('#').Trim();
                if (heading.Length > 0)
                {
                    blocks.Add(new TruthBlock { Kind = TruthBlockKind.Heading, Text = heading });
                }

                index++;
                continue;
            }

            if (raw.Trim().StartsWith('|'))
            {
                var rows = new List<IReadOnlyList<string>>();
                while (index < lines.Length && lines[index].Trim().StartsWith('|'))
                {
                    var cells = SplitRow(lines[index]);
                    // 跳过 |---|---| 这种分隔行
                    if (cells.Any(c => c.Length > 0 && c.All(ch => ch == '-' || ch == ':' || ch == ' ')))
                    {
                        index++;
                        continue;
                    }

                    rows.Add(cells);
                    index++;
                }

                if (rows.Count > 0)
                {
                    blocks.Add(new TruthBlock { Kind = TruthBlockKind.Table, Rows = rows });
                }

                continue;
            }

            if (IsScanPageNumber(raw))
            {
                index++;
                continue;
            }

            if (raw.Trim().Length == 0)
            {
                index++;
                continue;
            }

            var paragraph = new List<string>();
            while (index < lines.Length
                   && lines[index].Trim().Length > 0
                   && !lines[index].TrimStart().StartsWith('#')
                   && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)
                   && !lines[index].Trim().StartsWith('|')
                   && !IsScanPageNumber(lines[index]))
            {
                paragraph.Add(lines[index].Trim());
                index++;
            }

            if (paragraph.Count > 0)
            {
                blocks.Add(new TruthBlock { Kind = TruthBlockKind.Paragraph, Text = string.Join("\n", paragraph) });
            }
            else
            {
                // 防御：没被任何分支认领的行也必须前进，
                // 否则外层 while 会原地打转，界面直接卡死。
                index++;
            }
        }

        return blocks;
    }

    /// <summary>
    /// 扫描页码：整行只有数字（可以带斜杠），例如 07、12、07/12。
    /// 只在围栏之外判定 —— 围栏里的「07」可能是真实数据。
    /// </summary>
    private static bool IsScanPageNumber(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 6)
        {
            return false;
        }

        if (!trimmed.Any(char.IsDigit))
        {
            return false;
        }

        return trimmed.All(ch => char.IsDigit(ch) || ch == '/');
    }

    private static IReadOnlyList<string> SplitRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    /// <summary>
    /// 一行里所有非空白词的起始字符下标。
    ///
    /// 注意：字符下标推不出屏幕上的列 —— 汉字是全角、西文是半角，
    /// 同一「列」在不同行里的字符下标并不相同。列位置要在视觉空间里算，
    /// 见 <c>MonospaceGrid</c>。
    /// </summary>
    public static IEnumerable<int> TokenStarts(string line)
    {
        var inToken = false;
        for (var i = 0; i < line.Length; i++)
        {
            var isSpace = char.IsWhiteSpace(line[i]);
            if (!isSpace && !inToken)
            {
                yield return i;
            }

            inToken = !isSpace;
        }
    }
}
