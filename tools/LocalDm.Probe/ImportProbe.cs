using System.Diagnostics;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Documents;
using LocalDm.Infrastructure.Import;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 用真实 MinerU（flash 本地档）走应用的导入管线，产出可核对的来源覆盖摘要。
/// 支持单目录取样与整本剧本（多个角色目录 + 线索卡）批量。
/// 输出只含文件名、页数、字符数、耗时，不含正文。
/// </summary>
public static class ImportProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var storyRoot = ArgumentValue(args, "--story")
            ?? throw new ArgumentException("需要 --story <剧本根目录或角色目录>");
        var outDir = ArgumentValue(args, "--out") ?? "docs/evidence/us2";
        var home = ArgumentValue(args, "--home")
            ?? Path.Combine(FindRepoRoot(), ".appdata", "mineru-home");
        var executable = ArgumentValue(args, "--mineru")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "mineru.exe");
        var maxPagesPerFolder = int.Parse(ArgumentValue(args, "--pages") ?? "0");
        var tier = Enum.Parse<ParseTier>(ArgumentValue(args, "--tier") ?? "Flash", ignoreCase: true);
        var recursive = !args.Contains("--single");

        Directory.CreateDirectory(outDir);
        using var runner = new MineruRunner(executable, home);
        var preflight = new MineruPrivacyPreflight(home);
        var fallback = new RapidOcrFallback(
            ArgumentValue(args, "--ocr-python") ?? DefaultPython(),
            ArgumentValue(args, "--ocr-script") ?? RapidOcrFallback.DefaultScriptPath());
        var pipeline = new ImportPipeline(runner, preflight, fallback);

        var identity = await runner.GetIdentityAsync(CancellationToken.None);
        var serverStatus = await runner.RunStatusAsync("server", CancellationToken.None);
        var telemetryStatus = await runner.RunStatusAsync("telemetry", CancellationToken.None);
        var (reportedHome, telemetry) = MineruPrivacyPreflight.ReadStatusJson(serverStatus, telemetryStatus);
        var check = preflight.Evaluate(identity, reportedHome, telemetry);

        var groups = CollectGroups(storyRoot, recursive, maxPagesPerFolder);
        var allFiles = groups.SelectMany(g => g.Files).ToArray();

        var result = new ProbeReport
        {
            ObservedAt = DateTimeOffset.Now.ToString("O"),
            ToolVersion = identity.Version,
            Tier = tier.ToString(),
            PrivacyAllowed = check.Allowed,
            PrivacyCode = check.ErrorCode,
            Telemetry = telemetry,
            TotalPages = allFiles.Sum(PageCountOf),
            Groups = [.. groups.Select(g => g.Name)],
        };

        if (!check.Allowed)
        {
            await WriteAsync(outDir, result);
            return 2;
        }

        // 用户已显式授权：真实剧本用本机 flash 解析。授权绑定整批清单 hash。
        var manifestHash = ManifestHashFor(allFiles);
        var consent = new DataConsent
        {
            ToolIdentity = identity.Tool,
            Destination = "local",
            DataKind = DataKind.RealScript,
            SourceManifestHash = manifestHash,
            Scope = ConsentScope.LocalProcessingOnly,
        };
        result.ConsentScope = consent.Scope.ToString();
        result.ManifestHash = manifestHash;

        var rollup = Stopwatch.StartNew();
        foreach (var group in groups)
        {
            foreach (var file in group.Files)
            {
                var fileId = $"{group.Name}/{Path.GetFileNameWithoutExtension(file)}";
                var isPdf = ScriptFileTypes.IsPdf(file);
                var sw = Stopwatch.StartNew();
                ImportFileResult imported;
                try
                {
                    imported = await pipeline.ImportFileAsync(
                        fileId,
                        Path.GetFullPath(file),
                        isPdf ? DocumentKind.ScanPdf : DocumentKind.Image,
                        pdfPageCount: isPdf ? ScriptImportService.CountPdfPages(file) : null,
                        consent: consent,
                        CancellationToken.None,
                        manifestHash: manifestHash,
                        tier: tier);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    imported = ImportFileResult.Failed(fileId, "PROBE_EXCEPTION", ex.GetType().Name);
                }

                sw.Stop();
                result.Files.Add(new ProbeFile
                {
                    Group = group.Name,
                    FileId = fileId,
                    State = imported.State.ToString(),
                    ToolComplete = imported.ToolComplete,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    SpanCount = imported.Spans.Count,
                    ExpectedPages = PageCountOf(file),
                    Pages = imported.Spans.Select(s => s.PageNumber).Distinct().OrderBy(p => p).ToArray(),
                    HasLocator = imported.Spans.All(s => !string.IsNullOrWhiteSpace(s.ToolLocator)),
                    SampleChars = imported.Spans.Sum(s => s.OriginalText.Length),
                    ErrorCode = imported.ErrorCode,
                    PublicReason = imported.PublicReason,
                    FallbackOcr = imported.FallbackOcr,
                    UsedFallback = imported.FallbackOcr is not null,
                });

                Console.WriteLine(
                    $"[{result.Files.Count}/{allFiles.Length}] {fileId}: {imported.State} " +
                    $"spans={imported.Spans.Count} chars={imported.Spans.Sum(s => s.OriginalText.Length)} {sw.ElapsedMilliseconds}ms");
            }
        }

        rollup.Stop();
        result.TotalElapsedMs = rollup.ElapsedMilliseconds;
        result.FilesProcessed = result.Files.Count;
        result.AllComplete = result.Files.All(f => f.ToolComplete && f.Pages.Length >= f.ExpectedPages);
        var expectedPages = result.Files.Sum(f => f.ExpectedPages);
        result.SourceCoveragePercent = expectedPages == 0
            ? 0
            : Math.Round(100.0 * result.Files.Sum(f => Math.Min(f.Pages.Length, f.ExpectedPages)) / expectedPages, 1);
        result.GroupSummaries = [.. groups.Select(g => new ProbeGroup
        {
            Name = g.Name,
            Pages = g.Files.Sum(PageCountOf),
            Complete = result.Files.Count(f => f.Group == g.Name && f.ToolComplete),
            Chars = result.Files.Where(f => f.Group == g.Name).Sum(f => f.SampleChars),
            ElapsedMs = result.Files.Where(f => f.Group == g.Name).Sum(f => f.ElapsedMs),
        })];

        await WriteAsync(outDir, result);

        Console.WriteLine();
        Console.WriteLine($"tier={tier} pages={result.FilesProcessed} complete={result.AllComplete} " +
                          $"coverage={result.SourceCoveragePercent}% elapsed={result.TotalElapsedMs}ms");
        foreach (var g in result.GroupSummaries)
        {
            Console.WriteLine($"  {g.Name}: {g.Complete}/{g.Pages} complete, {g.Chars} chars, {g.ElapsedMs}ms");
        }

        return result.AllComplete ? 0 : 1;
    }

    private static List<(string Name, string[] Files)> CollectGroups(string root, bool recursive, int maxPerFolder)
    {
        // 图片与 PDF 都认，和导入向导用的是同一份判断（ScriptFileTypes）。
        IEnumerable<string> Enumerate(string dir) =>
            ScriptFileTypes.MaterialFiles(dir)
                .OrderBy(p => ScriptFileTypes.IsPdf(p) ? 0 : 1)
                .ThenBy(p => PaddedName(Path.GetFileNameWithoutExtension(p)), StringComparer.Ordinal);

        var groups = new List<(string Name, string[] Files)>();
        var direct = Enumerate(root).ToArray();
        if (direct.Length > 0)
        {
            var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            groups.Add((name, maxPerFolder > 0 ? direct.Take(maxPerFolder).ToArray() : direct));
        }

        if (!recursive)
        {
            return groups;
        }

        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var files = Enumerate(dir).ToArray();
            if (files.Length == 0)
            {
                continue;
            }

            groups.Add((Path.GetFileName(dir), maxPerFolder > 0 ? files.Take(maxPerFolder).ToArray() : files));
        }

        return groups;
    }

    private static string DefaultPython()
    {
        // 回退 OCR 需要一个能 import rapidocr 的解释器；环境变量优先，便于换机器时覆盖。
        var configured = Environment.GetEnvironmentVariable("LOCALDM_OCR_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return "python";
    }

    private static string PaddedName(string name) =>
        int.TryParse(name, out var n) ? n.ToString("D4") : name;

    /// <summary>一个文件的页数：PDF 按文件内页数，图片一张一页。</summary>
    private static int PageCountOf(string path) =>
        ScriptFileTypes.IsPdf(path) ? ScriptImportService.CountPdfPages(path) : 1;

    /// <summary>整批材料的清单 hash：由每个文件的相对标识与内容 hash 决定，绑定一次授权。</summary>
    private static string ManifestHashFor(IEnumerable<string> files)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            builder.Append(Path.GetFileName(file)).Append('|')
                .Append(ManifestFingerprint.ComputeSha256(file))
                .Append('\n');
        }

        return ManifestFingerprint.ComputeSha256Text(builder.ToString());
    }

    private static async Task WriteAsync(string outDir, ProbeReport report)
    {
        var path = Path.Combine(outDir, "import-probe.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.WriteLine("wrote " + path);
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LocalDm.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? Directory.GetCurrentDirectory();
    }

    private sealed class ProbeReport
    {
        public string ObservedAt { get; set; } = string.Empty;
        public string ToolVersion { get; set; } = string.Empty;
        public string Tier { get; set; } = string.Empty;
        public bool PrivacyAllowed { get; set; }
        public string? PrivacyCode { get; set; }
        public string? Telemetry { get; set; }
        public string? ConsentScope { get; set; }
        public string? ManifestHash { get; set; }
        public int TotalPages { get; set; }
        public List<string> Groups { get; set; } = [];
        public int FilesProcessed { get; set; }
        public bool AllComplete { get; set; }
        public double SourceCoveragePercent { get; set; }
        public long TotalElapsedMs { get; set; }
        public List<ProbeFile> Files { get; set; } = [];
        public List<ProbeGroup> GroupSummaries { get; set; } = [];
    }

    private sealed class ProbeGroup
    {
        public string Name { get; set; } = string.Empty;
        public int Pages { get; set; }
        public int Complete { get; set; }
        public int Chars { get; set; }
        public long ElapsedMs { get; set; }
    }

    private sealed class ProbeFile
    {
        public string Group { get; set; } = string.Empty;
        public string FileId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public bool ToolComplete { get; set; }
        public long ElapsedMs { get; set; }
        public int SpanCount { get; set; }
        public int ExpectedPages { get; set; }
        public int[] Pages { get; set; } = [];
        public bool HasLocator { get; set; }
        public int SampleChars { get; set; }
        public string? ErrorCode { get; set; }
        public string? PublicReason { get; set; }
        public string? FallbackOcr { get; set; }
        public bool UsedFallback { get; set; }
    }
}
