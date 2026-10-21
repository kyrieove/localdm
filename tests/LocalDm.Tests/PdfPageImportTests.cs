using System.IO;
using System.Text;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Tests;

/// <summary>
/// 多页 PDF 的导入：PDF 的一页就是一页，不整份压成一条。
///
/// 全部材料都在临时目录里现造（PDF 只写对象头，图片是一像素 PNG），
/// 识别实现是假的：每页回两段合成文字，用来验证「同页拼接、跨页不混」。
/// 不读 stories/、.appdata/、.local/，也不启动 MinerU。
/// </summary>
public sealed class PdfPageImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-pdf-" + Guid.NewGuid().ToString("N"));

    public PdfPageImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
    }

    [Fact]
    public async Task Pdf_pages_become_one_page_each_with_their_spans_joined_in_order()
    {
        var story = NewStory();
        WritePdf(Path.Combine(story, "组织", "手册.pdf"), pages: 3);
        var service = NewService(new FakeExtractor());

        var extraction = await service.ExtractAsync(
            story,
            service.Scan(story).Groups,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, extraction.Pages.Count);
        Assert.Equal(new[] { 1, 2, 3 }, extraction.Pages.Select(page => page.PageNumber));
        Assert.Equal(new[] { "p1", "p2", "p3" }, extraction.Pages.Select(page => page.PageLabel));
        Assert.Equal(
            new[]
            {
                "第 1 页第 1 段。\n第 1 页第 2 段。",
                "第 2 页第 1 段。\n第 2 页第 2 段。",
                "第 3 页第 1 段。\n第 3 页第 2 段。",
            },
            extraction.Pages.Select(page => page.Text));
        Assert.Equal(3, extraction.Pages.Select(page => page.SpanId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Built_package_covers_every_pdf_page()
    {
        var story = NewStory();
        WritePdf(Path.Combine(story, "组织", "手册.pdf"), pages: 3);
        var service = NewService(new FakeExtractor());

        var extraction = await service.ExtractAsync(story, service.Scan(story).Groups, null, TestContext.Current.CancellationToken);
        var result = await service.BuildAsync(
            extraction,
            "合成测试剧本",
            4,
            MinimalFlow(organizerGroup: "手册"),
            TestContext.Current.CancellationToken);

        var readiness = result.Build.Readiness;
        Assert.True(readiness.CanStart, string.Join("、", readiness.IssueCategories));
        Assert.DoesNotContain("PAGE_COVERAGE_INCOMPLETE", readiness.IssueCategories);
        var coverage = Assert.Single(readiness.FilePageCoverage);
        Assert.Equal(3, coverage.ExpectedPages);
        Assert.Equal(3, coverage.ProcessedPages);
    }

    [Fact]
    public void Group_page_count_sums_pdf_pages_and_image_pages()
    {
        var folder = Path.Combine(_root, "混合");
        Directory.CreateDirectory(folder);
        var pdf = WritePdf(Path.Combine(folder, "手册.pdf"), pages: 3);
        var one = WritePng(Path.Combine(folder, "1.jpg"));
        var two = WritePng(Path.Combine(folder, "2.png"));

        var group = new ScriptGroup
        {
            Name = "混合",
            RelativePath = "混合",
            Files = [pdf, one, two],
            Kind = ScriptGroupKind.Organizer,
        };

        Assert.Equal(5, group.PageCount);
    }

    [Fact]
    public async Task Progress_counts_pages_and_ends_at_the_total()
    {
        var story = NewStory();
        WritePdf(Path.Combine(story, "组织", "手册.pdf"), pages: 3);
        WritePng(Path.Combine(story, "组织", "补充1.jpg"));
        WritePng(Path.Combine(story, "组织", "补充2.jpg"));
        var service = NewService(new FakeExtractor());
        var reports = new List<ImportProgress>();

        await service.ExtractAsync(
            story,
            service.Scan(story).Groups,
            new RecordingProgress(reports),
            TestContext.Current.CancellationToken);

        // 3 页 PDF + 2 张图片 = 5 页；PDF 先算、图片后算。
        Assert.All(reports, report => Assert.Equal(5, report.Total));
        Assert.Equal(5, reports[^1].Completed);
        Assert.Equal(3, reports[0].Completed);
        Assert.Equal(0, reports[^1].FailedCount);
    }

    [Fact]
    public async Task Mixed_script_with_pdf_manual_pdf_roles_and_image_clues_builds_a_startable_package()
    {
        var story = NewStory();
        WritePdf(Path.Combine(story, "手册", "主持手册.pdf"), pages: 3);
        WritePdf(Path.Combine(story, "角色本", "甲.pdf"), pages: 2);
        WritePdf(Path.Combine(story, "角色本", "乙.pdf"), pages: 2);
        WritePng(Path.Combine(story, "线索", "1.jpg"));
        WritePng(Path.Combine(story, "线索", "2.png"));
        WritePng(Path.Combine(story, "线索", "3.jpg"));
        WritePng(Path.Combine(story, "线索", "4.png"));

        var service = NewService(new FakeExtractor());
        var groups = service.Scan(story).Groups;

        Assert.Equal(ScriptGroupKind.Organizer, groups.Single(g => g.Name == "主持手册").Kind);
        Assert.Equal(ScriptGroupKind.Role, groups.Single(g => g.Name == "甲").Kind);
        Assert.Equal(ScriptGroupKind.Role, groups.Single(g => g.Name == "乙").Kind);
        Assert.Equal(ScriptGroupKind.Material, groups.Single(g => g.Name == "线索").Kind);
        Assert.Equal(3, groups.Single(g => g.Name == "主持手册").PageCount);
        Assert.Equal(2, groups.Single(g => g.Name == "甲").PageCount);
        Assert.Equal(2, groups.Single(g => g.Name == "乙").PageCount);
        Assert.Equal(4, groups.Single(g => g.Name == "线索").PageCount);

        var extraction = await service.ExtractAsync(story, groups, null, TestContext.Current.CancellationToken);
        Assert.Equal(11, extraction.Pages.Count);

        var result = await service.BuildAsync(
            extraction,
            "合成混合剧本",
            4,
            MinimalFlow(organizerGroup: "主持手册"),
            TestContext.Current.CancellationToken);

        var package = result.Build.Package;
        Assert.Equal(2, package.Roles.Count);
        Assert.Single(package.Materials);
        Assert.True(result.Build.Readiness.CanStart, string.Join("、", result.Build.Readiness.IssueCategories));
        Assert.DoesNotContain("PAGE_COVERAGE_INCOMPLETE", result.Build.Readiness.IssueCategories);
        Assert.All(result.Build.Readiness.FilePageCoverage, coverage => Assert.Equal(coverage.ExpectedPages, coverage.ProcessedPages));
    }

    [Fact]
    public async Task Folder_with_both_pdf_and_images_lists_the_pdf_pages_first_and_keeps_ids_unique()
    {
        var story = NewStory();
        WritePdf(Path.Combine(story, "混合", "组织手册.pdf"), pages: 2);
        WritePng(Path.Combine(story, "混合", "1.jpg"));
        WritePng(Path.Combine(story, "混合", "2.png"));

        var service = NewService(new FakeExtractor());
        var groups = service.Scan(story).Groups;

        Assert.Equal(new[] { "组织手册", "混合" }, groups.Select(group => group.Name));
        Assert.Equal(2, groups[0].PageCount);
        Assert.Equal(2, groups[1].PageCount);

        var extraction = await service.ExtractAsync(story, groups, null, TestContext.Current.CancellationToken);
        var result = await service.BuildAsync(
            extraction,
            "合成混放剧本",
            4,
            MinimalFlow(organizerGroup: "组织手册"),
            TestContext.Current.CancellationToken);

        var ids = result.Build.Package.Segments.Select(segment => segment.SegmentId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        // PDF 的两页排在图片前面，页号是 PDF 内的 1、2。
        Assert.Equal(new[] { "seg-组织手册-p1", "seg-组织手册-p2" }, ids[..2]);
        Assert.All(ids[2..], id => Assert.StartsWith("seg-混合-", id, StringComparison.Ordinal));
    }

    [Fact]
    public void Wizard_and_import_probe_recognize_the_same_file_types()
    {
        foreach (var name in new[] { "1.jpg", "1.jpeg", "1.png", "1.webp", "1.pdf", "1.JPG", "1.PDF" })
        {
            Assert.True(ScriptFileTypes.IsMaterial(name), name);
        }

        foreach (var name in new[] { "1.txt", "Thumbs.db", ".DS_Store", "1.docx", "封面" })
        {
            Assert.False(ScriptFileTypes.IsMaterial(name), name);
        }

        // 扫描用的就是这份判断：同一个文件夹放满这些扩展名，只挑出 5 个材料文件。
        var folder = Path.Combine(_root, "类型");
        Directory.CreateDirectory(folder);
        WritePng(Path.Combine(folder, "1.jpg"));
        WritePng(Path.Combine(folder, "2.jpeg"));
        WritePng(Path.Combine(folder, "3.png"));
        WritePng(Path.Combine(folder, "4.webp"));
        WritePdf(Path.Combine(folder, "5.pdf"), pages: 2);
        File.WriteAllText(Path.Combine(folder, "不打印.txt"), "不是材料");
        File.WriteAllText(Path.Combine(folder, "Thumbs.db"), "不是材料");

        var files = ScriptFileTypes.MaterialFiles(folder);
        Assert.Equal(5, files.Count);
        Assert.DoesNotContain(files, path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, path => path.EndsWith("Thumbs.db", StringComparison.OrdinalIgnoreCase));

        var service = NewService(new FakeExtractor());
        var scanned = service.Scan(folder).Groups.SelectMany(group => group.Files).ToArray();
        Assert.Equal(
            files.OrderBy(path => path, StringComparer.Ordinal),
            scanned.OrderBy(path => path, StringComparer.Ordinal));
    }

    // ===== 辅助 =====

    private ScriptImportService NewService(IPageExtractor extractor) =>
        new(new ScriptImportOptions { DataRoot = Path.Combine(_root, "data"), Extractor = extractor });

    /// <summary>每本剧本一个自己的根目录，免得互相看见对方的材料。</summary>
    private string NewStory()
    {
        var story = Path.Combine(_root, "剧本-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(story);
        return story;
    }

    private static string WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. OnePixelPng, .. Encoding.UTF8.GetBytes(Path.GetFileName(path))]);
        return path;
    }

    /// <summary>
    /// 造一份 <paramref name="pages"/> 页的 PDF：页数由 <c>/Type /Page</c> 对象头决定，
    /// 导入服务也是这么数的。内容不重要，识别实现是假的。
    /// </summary>
    private static string WritePdf(string path, int pages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder("%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        text.Append("2 0 obj << /Type /Pages /Count ").Append(pages).Append(" >> endobj\n");
        for (var page = 1; page <= pages; page++)
        {
            text.Append(page + 2).Append(" 0 obj << /Type /Page /Parent 2 0 R >> endobj\n");
        }

        text.Append("%%EOF\n");
        File.WriteAllText(path, text.ToString(), Encoding.Latin1);
        return path;
    }

    private ScriptFlow MinimalFlow(string organizerGroup) =>
        new()
        {
            Layering = new ScriptLayering
            {
                OrganizerGroup = organizerGroup,
                TruthPages = [1],
                IgnoredPages = [],
                PublicPages = [],
            },
            Steps =
            [
                new ScriptFlowStep
                {
                    StepId = "read",
                    Title = "阅本",
                    Kind = ScriptStepKind.Read,
                    Hint = "读自己的角色本。",
                    Conditions = ["全员读完"],
                    SourceRef = "组织者手册第 1 页",
                    OffersOpeningSpeech = true,
                },
                new ScriptFlowStep
                {
                    StepId = "reveal",
                    Title = "揭晓",
                    Kind = ScriptStepKind.Reveal,
                    Hint = "公开真相。",
                    Conditions = [],
                    SourceRef = "组织者手册第 1 页",
                },
            ],
        };

    private sealed class RecordingProgress : IProgress<ImportProgress>
    {
        private readonly IList<ImportProgress> _sink;

        public RecordingProgress(IList<ImportProgress> sink) => _sink = sink;

        public void Report(ImportProgress value) => _sink.Add(value);
    }

    /// <summary>
    /// 假识别：PDF 每页回两段文字（块 0、块 1），图片一页回一段。
    /// 页号从 1 开始，与真实识别一致；文字里带页号，便于断言「跨页没有混在一起」。
    /// </summary>
    private sealed class FakeExtractor : IPageExtractor
    {
        public Task<ToolIdentity> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ToolIdentity { Tool = "fake-mineru", Version = "1.0", AppOwnedHome = "fake-home" });

        public Task<ImportFileResult> ExtractAsync(
            string fileId,
            string absolutePath,
            DataConsent consent,
            CancellationToken cancellationToken)
        {
            var isPdf = ScriptFileTypes.IsPdf(absolutePath);
            var pageCount = isPdf ? ScriptImportService.CountPdfPages(absolutePath) : 1;
            var source = SourceIndex.BuildSourceFile(
                fileId,
                absolutePath,
                isPdf ? DocumentKind.ScanPdf : DocumentKind.Image,
                pdfPageCount: isPdf ? pageCount : null,
                isPdf ? SourceOrder.PageOrder : SourceOrder.ManifestOrder,
                "fake-mineru 1.0");

            var spans = new List<SourceSpan>();
            for (var page = 1; page <= pageCount; page++)
            {
                var blocks = isPdf ? 2 : 1;
                for (var block = 0; block < blocks; block++)
                {
                    var text = isPdf
                        ? $"第 {page} 页第 {block + 1} 段。"
                        : $"{Path.GetFileNameWithoutExtension(absolutePath)} 的识别文字。";
                    spans.Add(new SourceSpan
                    {
                        SpanId = $"{fileId}-p{page}-b{block}",
                        FileId = fileId,
                        PageNumber = page,
                        BlockNumber = block,
                        ToolLocator = $"doc:{fileId}/tier:flash/page:{page}/block:{block}",
                        TextHash = SourceIndex.ComputeTextHash(text),
                        OriginalText = text,
                    });
                }
            }

            return Task.FromResult(new ImportFileResult(
                fileId,
                ImportFileState.Complete,
                ToolComplete: true,
                source,
                spans,
                [],
                ErrorCode: null,
                PublicReason: null));
        }
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
