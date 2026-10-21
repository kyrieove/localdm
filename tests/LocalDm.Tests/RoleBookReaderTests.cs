using LocalDm.Core.Tablet;

namespace LocalDm.Tests;

/// <summary>
/// 阅读器领域模型的行为测试：逐字一致、章节顺序、阅读位置与设置边界。
///
/// 分页本身在浏览器里按 DOM 测量完成（reader-pagination.js），
/// 这些用例锁定它依赖的服务端契约：块顺序、字符流、位置映射、设置钳制。
/// </summary>
public sealed class RoleBookReaderTests
{
    [Fact]
    public void Plain_text_keeps_every_character_in_order()
    {
        var book = new RoleBookText
        {
            RoleId = "role-a",
            Chapters = [new RoleBookChapter
            {
                ChapterId = "chapter-a", Title = "第一章",
                Blocks = [
                    new RoleBookBlock { BlockId = "title", Kind = RoleBookBlockKind.ChapterTitle, Text = "第一章" },
                    new RoleBookBlock { BlockId = "p1", Kind = RoleBookBlockKind.Paragraph, Text = "不改写𠮷字。" },
                    new RoleBookBlock { BlockId = "s1", Kind = RoleBookBlockKind.SectionTitle, Text = "下一节" },
                    new RoleBookBlock { BlockId = "p2", Kind = RoleBookBlockKind.Paragraph, Text = "原文继续。" },
                ],
            }],
        };
        var text = RoleBookPlainText.Build(book);

        // 逐字一致：按章节与块顺序拼回来，必须与正文块依次相接相等。
        const string expected = "第一章不改写𠮷字。下一节原文继续。";
        Assert.Equal(expected, text);

        // 不丢字、不乱序：每块的开头都要按出现顺序被找到。
        var cursor = 0;
        foreach (var block in book.Chapters.SelectMany(c => c.Blocks)
                     .Where(b => b.Kind == RoleBookBlockKind.Paragraph))
        {
            var found = text.IndexOf(block.Text, cursor, StringComparison.Ordinal);
            Assert.True(found >= cursor, $"块 {block.BlockId} 的顺序或内容对不上。");
            cursor = found + block.Text.Length;
        }

        Assert.Equal(text.Length, cursor);
    }

    [Fact]
    public void Chapter_titles_are_not_mixed_into_paragraph_flow()
    {
        var book = SyntheticRoleBook.Create();

        // 章题是独立块；正文段落里不能夹着另一章的章题文字。
        // 「抵达」「旧书与午后」这类章题本身可能作为普通词出现在正文里，
        // 所以只断言章题块没有被塞进段落块，不断言词不出现。
        var chapterTitleBlocks = book.Chapters
            .SelectMany(c => c.Blocks)
            .Where(b => b.Kind == RoleBookBlockKind.ChapterTitle)
            .Select(b => b.Text)
            .ToArray();
        var paragraphBlocks = book.Chapters
            .SelectMany(c => c.Blocks)
            .Where(b => b.Kind == RoleBookBlockKind.Paragraph)
            .Select(b => b.Text)
            .ToArray();

        // 每章都以自己的章题开头。
        foreach (var chapter in book.Chapters)
        {
            Assert.Equal(RoleBookBlockKind.ChapterTitle, chapter.Blocks[0].Kind);
            Assert.Equal(chapter.Title, chapter.Blocks[0].Text);
        }

        // 章题块与段落块是两套，没有互相串。
        Assert.All(chapterTitleBlocks, title =>
            Assert.DoesNotContain(title, paragraphBlocks, StringComparer.Ordinal));
        Assert.Equal(book.Chapters.Count, chapterTitleBlocks.Length);
    }

    [Fact]
    public void Position_zero_is_a_valid_first_page()
    {
        var starts = new long[] { 0, 320, 700, 1100 };

        // 位置 0 必须映射到第 0 页：不能用 0 表示「没有进度」。
        Assert.Equal(0, ReadingPositionMap.PageIndexFor(starts, ReadingPosition.Start));
    }

    [Fact]
    public void Position_maps_to_the_last_page_that_starts_at_or_before_it()
    {
        var starts = new long[] { 0, 320, 700, 1100 };

        Assert.Equal(0, ReadingPositionMap.PageIndexFor(starts, new ReadingPosition(319)));
        Assert.Equal(1, ReadingPositionMap.PageIndexFor(starts, new ReadingPosition(320)));
        Assert.Equal(2, ReadingPositionMap.PageIndexFor(starts, new ReadingPosition(701)));
        Assert.Equal(3, ReadingPositionMap.PageIndexFor(starts, new ReadingPosition(99999)));
    }

    [Fact]
    public void Position_after_repagination_keeps_the_reading_place()
    {
        // 字号变大后每页容纳的字数变少，页起点列表变长；
        // 但按字符偏移量仍应落在同一处文字上。
        var before = new long[] { 0, 400, 800, 1200 };
        var after = new long[] { 0, 200, 400, 600, 800, 1000, 1200 };

        var position = new ReadingPosition(810);
        var beforePage = ReadingPositionMap.PageIndexFor(before, position);
        var afterPage = ReadingPositionMap.PageIndexFor(after, position);

        Assert.Equal(2, beforePage);
        Assert.Equal(4, afterPage);
        Assert.True(after[afterPage] <= position.CharacterOffset);
    }

    [Fact]
    public void Negative_position_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadingPosition(-1));
    }

    [Theory]
    [InlineData(10, "1.75", false)]
    [InlineData(30, "9.9", true)]
    public void Settings_are_clamped_to_the_supported_range(int fontSize, string spacing, bool focus)
    {
        var clamped = ReadingSettings.Clamp(new ReadingSettings
        {
            FontSize = fontSize,
            LineSpacing = spacing,
            FocusMode = focus,
        });

        Assert.InRange(clamped.FontSize, ReadingSettings.MinFontSteps, ReadingSettings.MaxFontSteps);
        Assert.Contains(clamped.LineSpacing, new[]
        {
            ReadingSettings.SpacingCompact,
            ReadingSettings.SpacingRelaxed,
            ReadingSettings.SpacingLoose,
        });
        Assert.Equal(focus, clamped.FocusMode);
    }

    [Fact]
    public void Figures_keep_their_original_text_and_breaks_keep_their_place()
    {
        var book = new RoleBookText
        {
            RoleId = "r",
            Chapters =
            [
                new RoleBookChapter
                {
                    ChapterId = "c",
                    Title = "章",
                    Blocks =
                    [
                        new RoleBookBlock { BlockId = "t", Kind = RoleBookBlockKind.ChapterTitle, Text = "章" },
                        new RoleBookBlock { BlockId = "p1", Kind = RoleBookBlockKind.Paragraph, Text = "第一段。" },
                        new RoleBookBlock
                        {
                            BlockId = "f1",
                            Kind = RoleBookBlockKind.Figure,
                            Text = "地图",
                            ImageAssetId = "asset-map",
                            SourcePageNumber = 3,
                        },
                        new RoleBookBlock { BlockId = "b1", Kind = RoleBookBlockKind.Break, Text = string.Empty },
                        new RoleBookBlock { BlockId = "p2", Kind = RoleBookBlockKind.Paragraph, Text = "第二段。" },
                    ],
                },
            ],
        };

        var text = RoleBookPlainText.Build(book);

        // 章题和插图原有文字保留，空分隔不增加字符，也不重复章题。
        Assert.Equal("章第一段。地图第二段。", text);
        Assert.Equal(11, text.Length);

        // 但块顺序保留了插图位置与来源页号，原稿核对能对上。
        var figure = book.Chapters[0].Blocks.Single(b => b.Kind == RoleBookBlockKind.Figure);
        Assert.Equal("asset-map", figure.ImageAssetId);
        Assert.Equal(3, figure.SourcePageNumber);
    }

    [Fact]
    public void Confidence_records_what_recognition_did_not_prove()
    {
        var book = SyntheticRoleBook.Create();

        // 识别完成只说明跑完了：块数字段如实给出，界面据此提示可与原稿核对。
        Assert.True(book.Confidence.TotalBlocks > 0);
        Assert.Equal(0, book.Confidence.EmptyOrSuspectBlocks);
        Assert.Equal(RoleBookPlainText.CharacterCount(book), book.Confidence.TotalCharacters);
    }
}
