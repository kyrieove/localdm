using System.Diagnostics;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Import;

/// <summary>剧本导入服务：识别与建包（扫描与猜类别在 <c>ScriptImportService.Scan.cs</c>）。</summary>
public sealed partial class ScriptImportService
{
    /// <summary>识别缓存的版本号：缓存内容一旦改结构就加一，旧缓存自然失效。</summary>
    private const int CacheVersion = 1;

    /// <summary>
    /// 逐页识别。progress 每完成一页报一次；可取消；已识别的页走缓存。
    ///
    /// 取消用抛 <see cref="OperationCanceledException"/> 表达：调用方据此知道没有写出任何包文件。
    /// </summary>
    public async Task<ScriptExtraction> ExtractAsync(
        string storyRoot,
        IReadOnlyList<ScriptGroup> confirmedGroups,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmedGroups);
        var usable = confirmedGroups.Where(group => group.Kind != ScriptGroupKind.Ignore).ToArray();
        if (usable.Length == 0)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "没有任何要识别的材料分组。");
        }

        if (!usable.Any(group => group.Kind == ScriptGroupKind.Organizer))
        {
            // 没有组织者手册就没有真相页与流程依据：这一条不能跳过。
            throw new DomainRuleException(
                DomainErrorCode.INVALID_INPUT,
                "必须先确定哪个分组是组织者手册。");
        }

        var files = usable
            .SelectMany(group => group.Files.Select(path => (Group: group, Path: path)))
            .ToArray();
        var manifestHash = ManifestHashFor(files.Select(f => f.Path));
        var identity = await _extractor.GetIdentityAsync(cancellationToken);
        var consent = new DataConsent
        {
            ToolIdentity = identity.Tool,
            Destination = "local",
            DataKind = DataKind.RealScript,
            SourceManifestHash = manifestHash,
            Scope = ConsentScope.LocalProcessingOnly,
        };

        // 页数先算清：一份 N 页的 PDF 算 N 页，一张图片算 1 页。进度分母与「共几页」都用它。
        var jobs = files
            .Select(file => new ImportJob(
                file.Group,
                file.Path,
                ScriptFileTypes.IsPdf(file.Path) ? DocumentKind.ScanPdf : DocumentKind.Image,
                ScriptFileTypes.IsPdf(file.Path) ? CountPdfPages(file.Path) : 1))
            .ToArray();
        var totalPages = jobs.Sum(job => job.PageCount);

        var pages = new List<RealScriptPackageBuilder.ExtractedPage>();
        var sources = new List<SourceFile>();
        var failed = new List<string>();
        var blank = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var completed = 0;

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var label = System.IO.Path.GetFileNameWithoutExtension(job.Path);
            var fileId = $"{job.Group.Name}-{label}";

            var extracted = await ExtractOneAsync(fileId, job.Path, job.Kind, job.PageCount, identity, consent, cancellationToken);
            if (extracted.Blocked is { } blocked)
            {
                // 预检或授权没过：整批停下，界面显示脱敏原因，不再继续上送别的页。
                return ScriptExtraction.Blocked(blocked.Code, blocked.Reason);
            }

            // 进度按页走：一个文件识别完，把它那几页一次算上。
            completed += job.PageCount;
            if (extracted.Source is null || extracted.ToolFailed)
            {
                failed.Add(fileId);
            }
            else if (extracted.Spans.Count == 0)
            {
                blank.Add(fileId); // 工具正常跑完但没有字：多半是插图或空白页
            }
            else
            {
                sources.Add(extracted.Source);
                AddPages(pages, job.Group, job.Kind, label, extracted.Spans);
            }

            progress?.Report(new ImportProgress
            {
                Completed = completed,
                Total = totalPages,
                PageLabel = label,
                FailedCount = failed.Count,
                EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, completed, totalPages),
            });
        }

        return new ScriptExtraction { Pages = pages, Sources = sources, FailedPages = failed, BlankFiles = blank };
    }

    /// <summary>
    /// 一份文件的识别结果 → 包里的页。
    ///
    /// 图片一页一条；PDF 按 span 的页号分组，同一页的 span 按原顺序拼成一条，
    /// 页号用 PDF 内的页号（从 1 开始），页标签写成 <c>p{页号}</c> 保证段 ID 唯一。
    /// </summary>
    private static void AddPages(
        List<RealScriptPackageBuilder.ExtractedPage> pages,
        ScriptGroup group,
        DocumentKind kind,
        string label,
        IReadOnlyList<SourceSpan> spans)
    {
        if (kind == DocumentKind.Image)
        {
            pages.Add(new RealScriptPackageBuilder.ExtractedPage
            {
                Group = group.Name,
                GroupKind = ToExtractedKind(group.Kind),
                PageNumber = int.TryParse(label, out var page) ? page : 0,
                PageLabel = label,
                SourcePageNumber = 1,
                Text = string.Join("\n", spans.Select(span => span.OriginalText)),
                FileId = spans[0].FileId,
                SpanId = spans[0].SpanId,
            });
            return;
        }

        foreach (var pageSpans in spans.GroupBy(span => span.PageNumber).OrderBy(group => group.Key))
        {
            pages.Add(new RealScriptPackageBuilder.ExtractedPage
            {
                Group = group.Name,
                GroupKind = ToExtractedKind(group.Kind),
                PageNumber = pageSpans.Key,
                PageLabel = $"p{pageSpans.Key}",
                SourcePageNumber = pageSpans.Key,
                Text = string.Join("\n", pageSpans.Select(span => span.OriginalText)),
                FileId = pageSpans.First().FileId,
                SpanId = pageSpans.First().SpanId,
            });
        }
    }

    /// <summary>
    /// 按确认过的分类与流程建包、登记、写包文件。
    ///
    /// 流程声明单独存一份 <c>&lt;包&gt;.flow.json</c>：以后可以「重新整理流程」，
    /// 不必重新识别。改流程会得到新的包标识，这是现有内容指纹规则。
    /// </summary>
    public async Task<ScriptImportResult> BuildAsync(
        ScriptExtraction extraction,
        string title,
        int playerCount,
        ScriptFlow flow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(flow);
        if (extraction.IsBlocked)
        {
            throw new DomainRuleException(DomainErrorCode.CONSENT_REQUIRED, extraction.BlockedReason!);
        }

        var built = RealScriptPackageBuilder.Build(
            title,
            playerCount,
            extraction.Pages,
            extraction.Sources,
            ScriptLayeringRule.FromFlow(flow),
            flow);

        using var store = new SessionStore(Path.Combine(_options.DataRoot, "sessions.db"));
        await store.InitializeAsync(cancellationToken);
        await store.RegisterPackageAsync(built.Package, cancellationToken);

        var packagesDir = Path.Combine(_options.DataRoot, "packages");
        Directory.CreateDirectory(packagesDir);
        var stem = $"{built.Package.PackageId}-{built.Package.Version}";
        var packagePath = Path.Combine(packagesDir, stem + ".json");
        var flowPath = Path.Combine(packagesDir, stem + ".flow.json");

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(
            packagePath,
            JsonSerializer.Serialize(built.Package, options),
            cancellationToken);
        await File.WriteAllTextAsync(
            flowPath,
            JsonSerializer.Serialize(flow, options),
            cancellationToken);

        // 第一次导入就把平板材料备好；失败不拦导入，开局时还会再补一次。
        try
        {
            await Task.Run(() => TabletMaterials.Ensure(built.Package, packagePath), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalDm.Core.Diagnostics.RunLog.Write("Tablet", "导入时备平板材料失败：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(Convert.ToString(ex.GetType().Name)));
        }

        return new ScriptImportResult { Build = built, PackagePath = packagePath, FlowPath = flowPath };
    }

    /// <summary>识别一页：先查缓存，未命中才调真正的识别实现，成功后写回缓存。</summary>
    private async Task<ExtractedOne> ExtractOneAsync(
        string fileId,
        string path,
        DocumentKind kind,
        int pageCount,
        ToolIdentity identity,
        DataConsent consent,
        CancellationToken cancellationToken)
    {
        var hash = SourceIndex.ComputeFileHash(path);
        var source = SourceIndex.BuildSourceFile(
            fileId,
            path,
            kind,
            pageCount,
            SourceOrder.ManifestOrder,
            $"{identity.Tool} {identity.Version}");

        var cached = ReadCache(hash);
        if (cached is { Count: > 0 })
        {
            var fromCache = BuildSpans(source, cached);
            if (fromCache.Count > 0)
            {
                return new ExtractedOne(source, fromCache, null);
            }
        }

        var result = await _extractor.ExtractAsync(fileId, path, consent, cancellationToken);
        if (result.State == ImportFileState.Blocked)
        {
            return new ExtractedOne(null, [], (result.ErrorCode ?? "BLOCKED", result.PublicReason ?? "识别未获授权。"));
        }

        if (result.Spans.Count == 0)
        {
            return new ExtractedOne(source, [], null, ToolFailed: result.State is not ImportFileState.Complete);
        }

        WriteCache(hash, result.Spans);
        return new ExtractedOne(source, result.Spans, null);
    }

    private static IReadOnlyList<SourceSpan> BuildSpans(SourceFile source, IReadOnlyList<CachedBatch> batches) =>
        batches
            .Select((batch, index) => new SourceSpan
            {
                SpanId = $"{source.FileId}-b{index}",
                FileId = source.FileId,
                PageNumber = Math.Max(1, batch.Page),
                BlockNumber = batch.Block,
                ToolLocator = batch.Locator,
                TextHash = SourceIndex.ComputeTextHash(batch.Text),
                OriginalText = batch.Text,
            })
            .ToArray();

    /// <summary>缓存按文件内容 hash 存：同一页不论放在哪本剧本里，识别一次就够了。</summary>
    private string CachePath(string hash) =>
        Path.Combine(_options.DataRoot, "ocr-cache", hash + ".json");

    private IReadOnlyList<CachedBatch>? ReadCache(string hash)
    {
        var path = CachePath(hash);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var cached = JsonSerializer.Deserialize<CachedPage>(File.ReadAllText(path));
            return cached is { Version: CacheVersion } ? cached.Batches : null;
        }
        catch (JsonException)
        {
            // 缓存坏了就当没有：宁可重识别一页，也不要让损坏的缓存卡住整次导入。
            return null;
        }
    }

    private void WriteCache(string hash, IReadOnlyList<SourceSpan> spans)
    {
        try
        {
            var directory = Path.Combine(_options.DataRoot, "ocr-cache");
            Directory.CreateDirectory(directory);
            var payload = new CachedPage(
                CacheVersion,
                spans.Select(span => new CachedBatch(span.PageNumber, span.BlockNumber, span.ToolLocator, span.OriginalText)).ToArray());
            File.WriteAllText(CachePath(hash), JsonSerializer.Serialize(payload));
        }
        catch (IOException)
        {
            // 缓存写不进去只是下次要重识别，不该让导入失败。
        }
    }

    private static RealScriptPackageBuilder.ExtractedGroupKind ToExtractedKind(ScriptGroupKind kind) => kind switch
    {
        ScriptGroupKind.Material => RealScriptPackageBuilder.ExtractedGroupKind.Material,
        ScriptGroupKind.Role => RealScriptPackageBuilder.ExtractedGroupKind.Role,
        // 组织者手册与「不要的分组」都不标注：手册由分层声明的分组名识别，不参与角色本/线索卡分流。
        _ => RealScriptPackageBuilder.ExtractedGroupKind.Unknown,
    };

    private static TimeSpan? EstimateRemaining(TimeSpan elapsed, int completed, int total)
    {
        if (completed <= 0 || total <= completed || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var perPage = elapsed.TotalSeconds / completed;
        return TimeSpan.FromSeconds(perPage * (total - completed));
    }

    /// <summary>
    /// 材料清单 hash：文件名 + 内容 hash，与命令行原有算法一致。
    /// 授权绑定的是这一批材料，而不是单个文件。
    /// </summary>
    private static string ManifestHashFor(IEnumerable<string> files)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            builder.Append(Path.GetFileName(file)).Append('|')
                .Append(SourceIndex.ComputeFileHash(file))
                .Append('\n');
        }

        return ManifestFingerprint.ComputeSha256Text(builder.ToString());
    }

    /// <summary>
    /// PDF 的页数：数页对象。<c>SourceSpan</c> 的页号要落在页数以内，
    /// 所以建来源时必须先知道这本 PDF 有几页。扫描（② 页显示页数）、识别与命令行探针共用这一个实现。
    ///
    /// 只认未压缩的对象头（<c>/Type /Page</c>）；交叉引用流压缩过的 PDF 可能数不准，
    /// 数少了的页在识别时会被判成源失配并记入失败页数，不会静默当成读完。
    /// </summary>
    public static int CountPdfPages(string path)
    {
        string text;
        try
        {
            // 用 Latin-1 读：PDF 的对象头是 ASCII，正文编码无关紧要。
            var bytes = File.ReadAllBytes(path);
            text = System.Text.Encoding.Latin1.GetString(bytes);
        }
        catch (IOException)
        {
            return 1;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf("/Type", index, StringComparison.Ordinal)) >= 0)
        {
            var cursor = index + "/Type".Length;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            if (text.AsSpan(cursor).StartsWith("/Page", StringComparison.Ordinal)
                && (cursor + 5 >= text.Length || !char.IsLetter(text[cursor + 5])))
            {
                count++;
                cursor += 5;
            }

            index = cursor > index ? cursor : index + 1;
        }

        return Math.Max(1, count);
    }

    /// <summary>一个待识别的文件：分组、路径、文档类型与页数。</summary>
    private sealed record ImportJob(ScriptGroup Group, string Path, DocumentKind Kind, int PageCount);

    private sealed record ExtractedOne(
        SourceFile? Source,
        IReadOnlyList<SourceSpan> Spans,
        (string Code, string Reason)? Blocked,
        bool ToolFailed = false);

    private sealed record CachedPage(int Version, IReadOnlyList<CachedBatch> Batches);

    private sealed record CachedBatch(int Page, int Block, string Locator, string Text);
}
