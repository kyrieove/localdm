using System.Diagnostics;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Documents;
using LocalDm.Infrastructure.Import;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 把真实剧本的两批提取结果装配成可开局的 ScriptPackage，并写出就绪报告。
/// 分层依据组织者手册的作者原话（第 7 页「后面是案件的全部真相」），不由模型判断。
/// </summary>
public static class BuildPackageProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var storyRoot = ArgumentValue(args, "--story")
            ?? throw new ArgumentException("需要 --story <剧本根目录>");
        var outDir = ArgumentValue(args, "--out") ?? "docs/evidence/us3";
        var title = ArgumentValue(args, "--title") ?? "孽岛疑云";
        var playerCount = int.Parse(ArgumentValue(args, "--players") ?? "5");
        var home = ArgumentValue(args, "--home") ?? Path.Combine(FindRepoRoot(), ".appdata", "mineru-home");
        var dataRoot = ArgumentValue(args, "--data") ?? Path.Combine(FindRepoRoot(), ".appdata", "localdm-data");

        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(dataRoot);

        var roleDir = Directory.GetDirectories(storyRoot).FirstOrDefault(d => d.EndsWith("装订", StringComparison.Ordinal))
                      ?? throw new DirectoryNotFoundException("没找到角色本目录");
        var clueDir = Path.Combine(storyRoot, "线索卡牌");

        using var runner = new MineruRunner(DefaultMineru(), home);
        var fallback = new RapidOcrFallback(DefaultPython(), RapidOcrFallback.DefaultScriptPath());
        var pipeline = new ImportPipeline(runner, new MineruPrivacyPreflight(home), fallback);

        var identity = await runner.GetIdentityAsync(CancellationToken.None);
        var (reportedHome, telemetry) = MineruPrivacyPreflight.ReadStatusJson(
            await runner.RunStatusAsync("server", CancellationToken.None),
            await runner.RunStatusAsync("telemetry", CancellationToken.None));
        var preflight = new MineruPrivacyPreflight(home).Evaluate(identity, reportedHome, telemetry);
        if (!preflight.Allowed)
        {
            Console.WriteLine($"preflight blocked: {preflight.ErrorCode}");
            return 2;
        }

        var groups = CollectGroups(roleDir).Concat(CollectGroups(clueDir)).ToArray();
        var allFiles = groups.SelectMany(g => g.Files).ToArray();
        var manifestHash = ManifestHashFor(allFiles);
        var consent = new DataConsent
        {
            ToolIdentity = identity.Tool,
            Destination = "local",
            DataKind = DataKind.RealScript,
            SourceManifestHash = manifestHash,
            Scope = ConsentScope.LocalProcessingOnly,
        };

        var pages = new List<RealScriptPackageBuilder.ExtractedPage>();
        var sources = new List<SourceFile>();
        var sw = Stopwatch.StartNew();

        foreach (var group in groups)
        {
            foreach (var file in group.Files)
            {
                var label = Path.GetFileNameWithoutExtension(file);
                var pageNumber = int.TryParse(label, out var parsed) ? parsed : 0;
                var fileId = $"{group.Name}-{label}";
                var imported = await pipeline.ImportFileAsync(
                    fileId,
                    Path.GetFullPath(file),
                    DocumentKind.Image,
                    pdfPageCount: null,
                    consent: consent,
                    CancellationToken.None,
                    manifestHash: manifestHash,
                    tier: ParseTier.Flash);

                if (imported.Source is not null)
                {
                    sources.Add(imported.Source);
                }

                if (imported.Spans.Count == 0)
                {
                    Console.WriteLine($"  ! {fileId}: 无来源（{imported.ErrorCode ?? imported.State.ToString()}）");
                    continue;
                }

                pages.Add(new RealScriptPackageBuilder.ExtractedPage
                {
                    Group = group.Name,
                    PageNumber = pageNumber,
                    PageLabel = label,
                    Text = string.Join("\n", imported.Spans.Select(s => s.OriginalText)),
                    FileId = imported.Source?.FileId ?? fileId,
                    SpanId = imported.Spans[0].SpanId,
                });

                Console.WriteLine($"  {fileId}: {imported.Spans.Count} span(s), {imported.Spans.Sum(s => s.OriginalText.Length)} chars");
            }
        }

        sw.Stop();

        var built = RealScriptPackageBuilder.Build(title, playerCount, pages, sources);

        // 注册到本地存档并写包文件，供应用直接读取
        using var store = new SessionStore(Path.Combine(dataRoot, "sessions.db"));
        await store.InitializeAsync(CancellationToken.None);
        await store.RegisterPackageAsync(built.Package, CancellationToken.None);

        var packagesDir = Path.Combine(dataRoot, "packages");
        Directory.CreateDirectory(packagesDir);
        var packagePath = Path.Combine(packagesDir, $"{built.Package.PackageId}-{built.Package.Version}.json");
        await File.WriteAllTextAsync(
            packagePath,
            JsonSerializer.Serialize(built.Package, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        var report = new
        {
            observedAt = DateTimeOffset.Now.ToString("O"),
            title,
            playerCount,
            packageId = built.Package.PackageId,
            manifestHash = built.Package.ManifestHash,
            validation = built.Package.Validation.ToString(),
            canStart = built.Readiness.CanStart,
            status = built.Readiness.Status.ToString(),
            issueCategories = built.Readiness.IssueCategories,
            pagesExtracted = pages.Count,
            totalPages = allFiles.Length,
            coverage = allFiles.Length == 0 ? 0 : Math.Round(100.0 * pages.Count / allFiles.Length, 1),
            segmentCounts = built.SegmentCounts.ToDictionary(k => k.Key.ToString(), v => v.Value),
            truthCharacters = built.TruthCharacters,
            phases = built.Package.Phases.Select(p => new { p.PhaseId, p.Title, p.IsVotePhase, p.IsRevealPhase }),
            packagePath = Path.GetRelativePath(FindRepoRoot(), packagePath),
            elapsedMs = sw.ElapsedMilliseconds,
        };

        var reportPath = Path.Combine(outDir, "real-package.json");
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"package={built.Package.PackageId} canStart={built.Readiness.CanStart} " +
                          $"pages={pages.Count}/{allFiles.Length} elapsed={sw.ElapsedMilliseconds}ms");
        foreach (var kv in built.SegmentCounts)
        {
            Console.WriteLine($"  {kv.Key}: {kv.Value} segments");
        }

        Console.WriteLine("wrote " + reportPath);
        return built.Readiness.CanStart ? 0 : 1;
    }

    private static IEnumerable<(string Name, string[] Files)> CollectGroups(string root)
    {
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            // 图片页序只能由 manifest 明确指定，不能按文件名字典序猜剧情。
            // 这里按「先数字页，再同页的卡背」排：数字升序，卡背紧跟其正面。
            var files = Directory.GetFiles(dir, "*.jpg")
                .OrderBy(f => PageOrderKey(Path.GetFileNameWithoutExtension(f)))
                .ThenBy(f => f, StringComparer.Ordinal)
                .ToArray();
            if (files.Length == 0)
            {
                continue;
            }

            yield return (Path.GetFileName(dir), files);
        }
    }

    /// <summary>页键：正面页取数字本身，卡背取「同号 + 0.5」，保证卡背排在正面之后。</summary>
    private static double PageOrderKey(string name)
    {
        var digits = name.TrimEnd('卡', '背');
        if (int.TryParse(digits, out var page))
        {
            return name.Length == digits.Length ? page : page + 0.5;
        }

        // 无法归类的图排到最后，但仍保留（不静默丢弃）
        return double.MaxValue;
    }

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

    private static string DefaultMineru() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "mineru.exe");

    private static string DefaultPython() =>
        Environment.GetEnvironmentVariable("LOCALDM_OCR_PYTHON") is { Length: > 0 } configured && File.Exists(configured)
            ? configured
            : "python";

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
}
