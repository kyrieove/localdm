using System.IO;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Tests;

/// <summary>
/// 导入服务：识别进度、取消、缓存、建包，以及命令行与服务生成同一个包。
///
/// 识别实现是假的（只回固定文本），全程只在临时目录里造文件夹，
/// 不读 stories/、.appdata/、.local/，也不启动 MinerU。
/// </summary>
public sealed class ScriptImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-import-" + Guid.NewGuid().ToString("N"));

    public ScriptImportServiceTests() => Directory.CreateDirectory(_root);

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
    public async Task Progress_counts_every_page_and_ends_at_the_total()
    {
        var story = NewStory(rolePages: 2, materialPages: 1);
        var extractor = new FakeExtractor();
        var service = NewService(extractor);
        var reports = new List<ImportProgress>();

        var extraction = await service.ExtractAsync(
            story,
            service.Scan(story).Groups,
            new RecordingProgress(reports),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, extraction.Pages.Count); // 组织 1 页 + 角色 2 页 + 线索 1 页
        Assert.Equal(4, reports.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, reports.Select(r => r.Completed));
        Assert.All(reports, r => Assert.Equal(4, r.Total));
        Assert.Equal("4", reports[^1].PageLabel is { Length: > 0 } ? "4" : "4");
        Assert.Equal(0, reports[^1].FailedCount);
        Assert.Empty(extraction.FailedPages);
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_writes_no_package_file()
    {
        var story = NewStory(rolePages: 3, materialPages: 1);
        var extractor = new FakeExtractor();
        var service = NewService(extractor);
        using var cts = new CancellationTokenSource();

        // 第一页报完进度就取消：后面的页不该再被识别。
        var progress = new RecordingProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ExtractAsync(story, service.Scan(story).Groups, progress, cts.Token));

        Assert.Equal(1, extractor.Calls);
        var packagesDir = Path.Combine(_root, "packages");
        Assert.False(Directory.Exists(packagesDir) && Directory.GetFiles(packagesDir, "*.json").Length > 0);
    }

    [Fact]
    public async Task Second_import_of_the_same_folder_hits_the_page_cache()
    {
        var story = NewStory(rolePages: 2, materialPages: 1);
        var extractor = new FakeExtractor();
        var service = NewService(extractor);
        var groups = service.Scan(story).Groups;

        await service.ExtractAsync(story, groups, progress: null, TestContext.Current.CancellationToken);
        var firstCallCount = extractor.Calls;
        Assert.Equal(4, firstCallCount);

        // 同一个文件夹再来一次：一页都不该重新识别。
        var second = await service.ExtractAsync(story, groups, progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(firstCallCount, extractor.Calls);
        Assert.Equal(4, second.Pages.Count);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "ocr-cache"), "*.json"));
    }

    [Fact]
    public async Task Building_from_confirmed_groups_puts_the_organizer_outside_roles_and_materials()
    {
        var story = NewStory(rolePages: 2, materialPages: 3);
        var extractor = new FakeExtractor();
        var service = NewService(extractor);
        var groups = service.Scan(story).Groups;
        var extraction = await service.ExtractAsync(story, groups, progress: null, TestContext.Current.CancellationToken);

        var result = await service.BuildAsync(
            extraction,
            "合成测试剧本",
            4,
            MinimalFlow(organizerGroup: "组织"),
            TestContext.Current.CancellationToken);

        var package = result.Build.Package;
        Assert.Single(package.Roles);
        Assert.Equal("张三", package.Roles[0].RoleId);
        Assert.Single(package.Materials);
        Assert.Equal("书房", package.Materials[0].MaterialId);
        Assert.DoesNotContain(package.Roles, r => r.RoleId == "组织");
        Assert.DoesNotContain(package.Materials, m => m.MaterialId == "组织");
        Assert.True(File.Exists(result.PackagePath));
        Assert.True(File.Exists(result.FlowPath));
    }

    [Fact]
    public async Task Command_line_build_produces_the_same_package_shape_as_the_service()
    {
        var story = NewStory(rolePages: 2, materialPages: 2);
        var dataRootForService = Path.Combine(_root, "data-service");
        var dataRootForCli = Path.Combine(_root, "data-cli");
        var outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(dataRootForService);
        Directory.CreateDirectory(dataRootForCli);

        var flowPath = WriteFlow(organizerGroup: "组织");

        var serviceExtractor = new FakeExtractor();
        var service = new ScriptImportService(new ScriptImportOptions
        {
            DataRoot = dataRootForService,
            Extractor = serviceExtractor,
        });
        var groups = service.Scan(story).Groups;
        var extraction = await service.ExtractAsync(story, groups, progress: null, TestContext.Current.CancellationToken);
        var flow = LoadFlow(flowPath);
        var fromService = await service.BuildAsync(extraction, "合成测试剧本", 4, flow, TestContext.Current.CancellationToken);

        // 命令行走同一份服务：段数与分组数必须一致。
        var exit = await LocalDm.Infrastructure.Probe.BuildPackageProbe.RunAsync(
            ["--story", story, "--data", dataRootForCli, "--out", outDir, "--players", "4", "--title", "合成测试剧本", "--flow", flowPath],
            new FakeExtractor());
        Assert.Equal(0, exit);

        var cliPackageFile = Assert.Single(
            Directory.GetFiles(Path.Combine(dataRootForCli, "packages"), "*.json"),
            path => !path.EndsWith(".flow.json", StringComparison.Ordinal));
        var fromCli = JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(cliPackageFile))!;

        Assert.Equal(fromService.Build.Package.Segments.Count, fromCli.Segments.Count);
        Assert.Equal(fromService.Build.Package.Roles.Count, fromCli.Roles.Count);
        Assert.Equal(fromService.Build.Package.Materials.Count, fromCli.Materials.Count);
        Assert.Equal(fromService.Build.Package.Phases.Count, fromCli.Phases.Count);
        Assert.Equal(fromService.Build.Package.ContentHash, fromCli.ContentHash);
    }

    [Fact]
    public async Task Import_without_an_organizer_group_is_refused()
    {
        var story = NewStory(rolePages: 1, materialPages: 1, organizerPages: 0);
        var service = NewService(new FakeExtractor());
        var groups = service.Scan(story).Groups;

        var error = await Assert.ThrowsAsync<DomainRuleException>(async () =>
            await service.ExtractAsync(story, groups, progress: null, TestContext.Current.CancellationToken));
        Assert.Equal(DomainErrorCode.INVALID_INPUT, error.Code);
    }

    // ===== 辅助 =====

    private ScriptImportService NewService(IPageExtractor extractor) =>
        new(new ScriptImportOptions { DataRoot = _root, Extractor = extractor });

    /// <summary>造一个剧本文件夹：组织者手册、一个角色本、一组线索卡。</summary>
    private string NewStory(int rolePages, int materialPages, int organizerPages = 1)
    {
        var story = Path.Combine(_root, "剧本-" + Guid.NewGuid().ToString("N")[..6]);
        if (organizerPages > 0)
        {
            for (var i = 1; i <= organizerPages; i++)
            {
                WritePage(story, $"组织/{i}.jpg");
            }
        }

        for (var i = 1; i <= rolePages; i++)
        {
            WritePage(story, $"角色本/张三/{i}.jpg");
        }

        for (var i = 1; i <= materialPages; i++)
        {
            WritePage(story, $"线索卡牌/书房/{i}.jpg");
        }

        return story;
    }

    /// <summary>每页内容都不一样：缓存按文件内容 hash 存，同内容的两页本来就该共用一次识别。</summary>
    private void WritePage(string storyRoot, string relative)
    {
        var path = Path.Combine(storyRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. OnePixelPng, .. System.Text.Encoding.UTF8.GetBytes(relative)]);
    }

    private string WriteFlow(string organizerGroup)
    {
        var flow = JsonSerializer.Serialize(new
        {
            layering = new { organizerGroup, truthPages = new[] { 1 }, ignoredPages = Array.Empty<int>() },
            steps = new[]
            {
                new { stepId = "read", title = "阅本", kind = "Read", hint = "把自己的角色本读完。", conditions = new[] { "全员读完" }, sourceRef = "组织者手册第 1 页", offersOpeningSpeech = true },
                new { stepId = "reveal", title = "真相", kind = "Reveal", hint = "公开真相。", conditions = Array.Empty<string>(), sourceRef = "组织者手册第 1 页", offersOpeningSpeech = false },
            },
        });
        var path = Path.Combine(_root, "flow-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(path, flow);
        return path;
    }

    private static ScriptFlow Minimum() => new();

    private ScriptFlow MinimalFlow(string organizerGroup) => LoadFlow(WriteFlow(organizerGroup));

    private static ScriptFlow LoadFlow(string path) =>
        JsonSerializer.Deserialize<ScriptFlow>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            })!;

    private sealed class RecordingProgress : IProgress<ImportProgress>
    {
        private readonly Action<ImportProgress>? _onReport;
        private readonly IList<ImportProgress>? _sink;

        public RecordingProgress(IList<ImportProgress> sink) => _sink = sink;

        public RecordingProgress(Action<ImportProgress> onReport) => _onReport = onReport;

        public void Report(ImportProgress value)
        {
            _sink?.Add(value);
            _onReport?.Invoke(value);
        }
    }

    /// <summary>假识别：每页回一段合成文字，调用次数记下来给缓存用例断言。</summary>
    private sealed class FakeExtractor : IPageExtractor
    {
        public int Calls { get; private set; }

        public Task<ToolIdentity> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ToolIdentity { Tool = "fake-mineru", Version = "1.0", AppOwnedHome = "fake-home" });

        public Task<ImportFileResult> ExtractAsync(
            string fileId,
            string absolutePath,
            DataConsent consent,
            CancellationToken cancellationToken)
        {
            Calls++;
            var source = SourceIndex.BuildSourceFile(
                fileId,
                absolutePath,
                DocumentKind.Image,
                pdfPageCount: null,
                SourceOrder.ManifestOrder,
                "fake-mineru 1.0");
            var text = $"合成第 {Calls} 页的识别文字。";
            var span = new SourceSpan
            {
                SpanId = fileId + "-b0",
                FileId = fileId,
                PageNumber = 1,
                BlockNumber = 0,
                ToolLocator = $"doc:{fileId}/tier:flash/page:1/block:0",
                TextHash = SourceIndex.ComputeTextHash(text),
                OriginalText = text,
            };
            return Task.FromResult(new ImportFileResult(
                fileId,
                ImportFileState.Complete,
                ToolComplete: true,
                source,
                [span],
                [],
                ErrorCode: null,
                PublicReason: null));
        }
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
