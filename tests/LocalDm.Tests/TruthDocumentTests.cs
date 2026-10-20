using LocalDm.App.ViewModels;

namespace LocalDm.Tests;

/// <summary>
/// 真相原文的解析与清理。
///
/// 真相是扫描件转出来的 Markdown：混着标题、代码棁、管道表格和页码。
/// 解析错了就会把 Markdown 语法糊到桌面大屏上，或者把等宽时间线的列位弄乱。
/// </summary>
public sealed class TruthDocumentTests
{
    private const string Sample = """
        ## 13:30~18:00 的时间线

        ```
        姓名    13:30        14:20
        玉书    塔顶值守    收到电报
        海      整理灯油    与玉书争执
        ```

        ## 结论

        钟声不是有人敲的，而是雾号。
        """;

    [Fact]
    public void Parses_heading_code_and_paragraph_blocks()
    {
        var blocks = TruthDocument.Parse(Sample);

        // 标题 / 等宽块 / 标题 / 段落
        Assert.Equal(4, blocks.Count);
        Assert.Equal(TruthBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("13:30~18:00 的时间线", blocks[0].Text);
        Assert.Equal(TruthBlockKind.Code, blocks[1].Kind);
        Assert.Equal(TruthBlockKind.Heading, blocks[2].Kind);
        Assert.Equal(TruthBlockKind.Paragraph, blocks[3].Kind);
    }

    [Fact]
    public void Code_block_keeps_every_row_and_drops_the_fence()
    {
        var code = TruthDocument.Parse(Sample).First(b => b.Kind == TruthBlockKind.Code);

        Assert.Equal(3, code.Lines.Count);
        Assert.DoesNotContain(code.Lines, l => l.Contains("```", StringComparison.Ordinal));
        Assert.Contains("姓名    13:30        14:20", code.Lines);
    }

    [Fact]
    public void Code_block_does_not_pretend_to_know_column_indexes()
    {
        var code = TruthDocument.Parse(Sample).First(b => b.Kind == TruthBlockKind.Code);

        // 列位置必须在视觉空间里算（汉字全角、西文半角），
        // 字符下标推不准，所以模型里不存它，由渲染器自己从内容推导。
        Assert.Equal(3, code.Lines.Count);
    }

    [Fact]
    public void Pipe_rows_become_a_table()
    {
        var blocks = TruthDocument.Parse("| 姓名 | 位置 |\n|---|---|\n| 玉书 | 塔顶 |\n| 海 | 塔底 |");

        var table = Assert.Single(blocks);
        Assert.Equal(TruthBlockKind.Table, table.Kind);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal("姓名", table.Rows[0][0]);
        Assert.DoesNotContain(table.Rows, r => r.Any(c => c.Contains("---", StringComparison.Ordinal)));
    }

    [Fact]
    public void Clean_removes_markdown_syntax_and_scan_page_numbers()
    {
        var cleaned = TruthDocument.Clean("## 标题\n```\n正文一行\n```\n07\n正文二");

        Assert.Contains("标题", cleaned, StringComparison.Ordinal);
        Assert.Contains("正文一行", cleaned, StringComparison.Ordinal);
        Assert.Contains("正文二", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("##", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("```", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("07", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_input_yields_nothing()
    {
        Assert.Empty(TruthDocument.Parse(null));
        Assert.Empty(TruthDocument.Parse("   "));
        Assert.Equal(string.Empty, TruthDocument.Clean(null));
    }

    [Fact]
    public void Indented_heading_is_parsed_and_never_loops()
    {
        // 扫描件转写常见「行首带空格的标题」。
        // 若标题判定不用 TrimStart，段落收集会认领不了它，外层循环就原地打转。
        var blocks = TruthDocument.Parse("  ## 带缩进的标题\n\n正文");

        Assert.Equal(2, blocks.Count);
        Assert.Equal(TruthBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("带缩进的标题", blocks[0].Text);
        Assert.Equal(TruthBlockKind.Paragraph, blocks[1].Kind);
    }

    [Fact]
    public void Numbers_inside_a_code_block_are_kept()
    {
        // 等宽块里的「07」可能是时刻或数量，不是扫描页码：不能删。
        var code = TruthDocument.Parse("```\n07  塔顶\n12  塔底\n```")
            .First(b => b.Kind == TruthBlockKind.Code);

        Assert.Equal(2, code.Lines.Count);
        Assert.Contains(code.Lines, l => l.Contains("07", StringComparison.Ordinal));
        Assert.Contains(code.Lines, l => l.Contains("12", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_page_numbers_outside_code_blocks_are_removed()
    {
        var cleaned = TruthDocument.Clean("正文一\n07\n07/12\n正文二");

        Assert.Contains("正文一", cleaned, StringComparison.Ordinal);
        Assert.Contains("正文二", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("\n07\n", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("07/12", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Unclosed_fence_does_not_lose_the_content()
    {
        var blocks = TruthDocument.Parse("```\n没有收尾的围栏\n");

        var code = Assert.Single(blocks);
        Assert.Equal(TruthBlockKind.Code, code.Kind);
        Assert.Equal("没有收尾的围栏", code.Lines[0]);
    }
}
