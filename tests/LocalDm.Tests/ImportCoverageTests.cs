using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Tests;

/// <summary>
/// T026：全页/事实来源覆盖、仅 Markdown 拒绝、秘密不出报告、关键异常禁止开局。
/// 合成 fixture 的已知真值来自 fixtures/synthetic/.private/expected.json（测试私有）。
/// </summary>
public sealed class ImportCoverageTests
{
    private static readonly string FixtureRoot = FixturePath.Locate();

    [Fact]
    public void Full_page_coverage_produces_ready_report()
    {
        var files = TwoFilesFixture();
        var spans = AllPages(files);
        var segments = new[]
        {
            ScriptValidator.CreateSegment("seg-1", "说明原文", SegmentVisibility.Public, [spans[0].SpanId]),
        };
        var facts = new[]
        {
            new KeyFact { FactId = "f1", Text = "关键事实", SourceSpanIds = [spans[0].SpanId], IsResolved = true },
        };

        var report = ScriptValidator.Validate("合成剧本", 5, files, spans, segments, facts, toolComplete: true, continuationPending: false);

        Assert.True(report.CanStart);
        Assert.Equal(ReadinessStatus.Ready, report.Status);
        Assert.Empty(report.IssueCategories);
    }

    [Fact]
    public void Missing_page_blocks_start()
    {
        var files = TwoFilesFixture();
        var spans = AllPages(files).Where(s => s.PageNumber != 3).ToArray();

        var report = ScriptValidator.Validate("合成剧本", 5, files, spans, [], [], toolComplete: true, continuationPending: false);

        Assert.False(report.CanStart);
        Assert.Contains("PAGE_COVERAGE_INCOMPLETE", report.IssueCategories);
    }

    [Fact]
    public void Pending_continuation_blocks_start_even_when_truncated_is_false()
    {
        var files = TwoFilesFixture();
        var report = ScriptValidator.Validate(
            "合成剧本",
            5,
            files,
            AllPages(files),
            [],
            [],
            toolComplete: true,
            continuationPending: true);

        Assert.False(report.CanStart);
        Assert.Contains("CONTINUATION_PENDING", report.IssueCategories);
    }

    [Fact]
    public void Tool_incomplete_blocks_start()
    {
        var files = TwoFilesFixture();
        var report = ScriptValidator.Validate(
            "合成剧本",
            5,
            files,
            AllPages(files),
            [],
            [],
            toolComplete: false,
            continuationPending: false);

        Assert.False(report.CanStart);
        Assert.Contains("TOOL_NOT_COMPLETE", report.IssueCategories);
    }

    [Fact]
    public void Unresolved_key_fact_stays_needs_review_without_inventing_a_murderer()
    {
        var files = TwoFilesFixture();
        var fact = new KeyFact
        {
            FactId = "f-unknown",
            Text = "尚未确认的关键事实",
            SourceSpanIds = [],
            IsResolved = false,
        };

        var report = ScriptValidator.Validate(
            "合成剧本",
            5,
            files,
            AllPages(files),
            [],
            [fact],
            toolComplete: true,
            continuationPending: false);

        Assert.False(report.CanStart);
        Assert.Contains("KEYFACT_NEEDS_REVIEW", report.IssueCategories);
        // 报告不含任何「默认凶手」字段或内容
        var view = PublicReadinessView.From(report);
        Assert.DoesNotContain("凶手", string.Join(',', view.IssueCodes), StringComparison.Ordinal);
    }

    [Fact]
    public void Segment_without_source_is_rejected()
    {
        var ex = Assert.Throws<DomainRuleException>(() =>
            ScriptValidator.CreateSegment("seg-empty", "无来源文本", SegmentVisibility.Public, []));
        Assert.Equal(DomainErrorCode.MISSING_SOURCE, ex.Code);
    }

    [Fact]
    public void Public_readiness_view_hides_secret_phrases_from_expected_file()
    {
        var expected = File.ReadAllText(Path.Combine(FixtureRoot, ".private", "expected.json"));
        var secrets = ExtractSecrets(expected);
        Assert.NotEmpty(secrets);

        var files = TwoFilesFixture();
        var report = ScriptValidator.Validate(
            "合成剧本",
            5,
            files,
            AllPages(files),
            [],
            [],
            toolComplete: true,
            continuationPending: false);
        var view = PublicReadinessView.From(report);

        var serialized = System.Text.Json.JsonSerializer.Serialize(view);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(FixtureRoot, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Markdown_cannot_impersonate_a_complete_package()
    {
        var dir = Path.Combine(Path.GetTempPath(), "localdm-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var md = Path.Combine(dir, "script.md");
            File.WriteAllText(md, "# 合成剧本\n\n| 页 | 文本 |\n|---|---|\n| 1 | 说明 |");

            var result = PreparedPackageReader.ReadFile(md);

            Assert.False(result.Accepted);
            Assert.Equal("PACKAGE_FORMAT_REJECTED", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Package_with_wrong_format_version_is_rejected()
    {
        var result = PreparedPackageReader.Read("""{"formatVersion":2,"completion":"Complete"}""");

        Assert.False(result.Accepted);
        Assert.Equal("PACKAGE_VERSION_UNSUPPORTED", result.ErrorCode);
    }

    [Fact]
    public void Package_marked_incomplete_is_rejected()
    {
        var result = PreparedPackageReader.Read("""{"formatVersion":1,"completion":"Incomplete","sourceFiles":[{"fileId":"a","pageCount":1}],"spans":[{"spanId":"s","fileId":"a","pageNumber":1}]}""");

        Assert.False(result.Accepted);
        Assert.Equal("PACKAGE_INCOMPLETE", result.ErrorCode);
    }

    [Fact]
    public void Package_with_missing_pages_is_rejected()
    {
        var json = """
            {
              "formatVersion": 1,
              "completion": "Complete",
              "sourceFiles": [{"fileId":"a","contentHash":"x","pageCount":3}],
              "spans": [{"spanId":"s1","fileId":"a","pageNumber":1,"toolLocator":"doc:D/tier:T/page:1","originalText":"文本"}]
            }
            """;

        var result = PreparedPackageReader.Read(json);

        Assert.False(result.Accepted);
        Assert.Equal("PACKAGE_COVERAGE_INCOMPLETE", result.ErrorCode);
    }

    [Fact]
    public void Locator_outside_whitelist_in_source_index_is_rejected()
    {
        var ex = Assert.Throws<DomainRuleException>(() => SourceIndex.ParseLocator("doc:D/tier:T/page:1; rm -rf /"));
        Assert.Equal(DomainErrorCode.SOURCE_MISMATCH, ex.Code);
    }

    private static SourceFile[] TwoFilesFixture()
    {
        var pdf = Path.Combine(FixtureRoot, "text-12pages.pdf");
        var image = Path.Combine(FixtureRoot, "images", "01-cover.png");
        return
        [
            source("text", pdf, DocumentKind.TextPdf, 12),
            source("image", image, DocumentKind.Image, 1),
        ];

        static SourceFile source(string id, string path, DocumentKind kind, int pages) => new()
        {
            FileId = id,
            PrivatePath = path,
            ContentHash = SourceIndex.ComputeFileHash(path),
            PageCount = kind == DocumentKind.Image ? 1 : pages,
            Origin = kind == DocumentKind.Image ? SourceOrder.ManifestOrder : SourceOrder.PageOrder,
            ToolIdentity = "mineru 4.0.1",
            ParseState = FileParseState.Complete,
        };
    }

    private static SourceSpan[] AllPages(IEnumerable<SourceFile> files)
    {
        var spans = new List<SourceSpan>();
        foreach (var file in files)
        {
            for (var page = 1; page <= file.PageCount; page++)
            {
                var text = $"合成页 {file.FileId} {page}";
                spans.Add(new SourceSpan
                {
                    SpanId = $"{file.FileId}-{page}",
                    FileId = file.FileId,
                    PageNumber = page,
                    BlockNumber = 1,
                    ToolLocator = $"doc:X/tier:text/page:{page}/block:1",
                    TextHash = SourceIndex.ComputeTextHash(text),
                    OriginalText = text,
                });
            }
        }

        return [.. spans];
    }

    private static string[] ExtractSecrets(string expectedJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(expectedJson);
        return doc.RootElement.GetProperty("secretPhrases")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
    }
}

/// <summary>定位仓库内 fixtures 目录（测试与构建输出目录不同）。</summary>
public static class FixturePath
{
    public static string Locate()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "fixtures", "synthetic");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("找不到 fixtures/synthetic。");
    }
}
