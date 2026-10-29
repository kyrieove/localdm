using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 命令行导入：把剧本文件夹扫成材料分组、识别、建包、写就绪报告。
///
/// 装配逻辑全在 <see cref="ScriptImportService"/> 里，界面走的是同一份实现。
/// 这里只负责解析参数、按参数确认分类、把结果打成一份能看的报告。
/// </summary>
public static class BuildPackageProbe
{
    public static async Task<int> RunAsync(string[] args) => await RunAsync(args, extractor: null);

    /// <summary>测试用重载：传入假的识别实现，其余流程与命令行完全相同。</summary>
    public static async Task<int> RunAsync(string[] args, IPageExtractor? extractor)
    {
        var storyRoot = DmAcceptanceProbe.ArgumentValue(args, "--story")
            ?? throw new ArgumentException("需要 --story <剧本根目录>");
        var outDir = DmAcceptanceProbe.ArgumentValue(args, "--out") ?? "docs/evidence/us3";
        var dataRoot = DmAcceptanceProbe.ArgumentValue(args, "--data") ?? Path.Combine(DmAcceptanceProbe.FindRepoRoot(), ".appdata", "localdm-data");
        var flow = LoadFlow(DmAcceptanceProbe.ArgumentValue(args, "--flow"));

        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(dataRoot);

        var service = new ScriptImportService(new ScriptImportOptions
        {
            DataRoot = dataRoot,
            MineruExecutable = DmAcceptanceProbe.ArgumentValue(args, "--mineru"),
            MineruHome = DmAcceptanceProbe.ArgumentValue(args, "--home"),
            OcrPythonPath = DmAcceptanceProbe.ArgumentValue(args, "--ocr-python"),
            Extractor = extractor,
        });

        var scan = service.Scan(storyRoot);

        // 标题不再写死某本剧本的名字：命令行没给就用文件夹名。
        var title = DmAcceptanceProbe.ArgumentValue(args, "--title") ?? Path.GetFileName(Path.GetFullPath(storyRoot).TrimEnd(Path.DirectorySeparatorChar));
        var playerCount = int.Parse(DmAcceptanceProbe.ArgumentValue(args, "--players") ?? "5");

        Console.WriteLine($"剧本文件夹：{storyRoot}");
        Console.WriteLine($"标题：{title}；玩家人数：{playerCount}");
        foreach (var group in scan.Groups)
        {
            Console.WriteLine($"  [{group.Kind}] {group.Name}（{group.PageCount} 页，{group.FileTypeLabel}）— {group.GuessReason}");
        }

        var chosen = DmAcceptanceProbe.ArgumentValue(args, "--organizer");
        if (chosen is null && scan.NeedsOrganizerChoice)
        {
            Console.WriteLine("! 没有分组像是组织者手册：请用 --organizer <分组名> 指定，或把手册放在名为「组织」「主持」「手册」的文件夹里。");
            return 2;
        }

        if (chosen is null && scan.HasMultipleOrganizerCandidates)
        {
            Console.WriteLine("! 有多个分组像是组织者手册：请用 --organizer <分组名> 指定一个。");
            return 2;
        }

        var organizer = chosen ?? scan.OrganizerGroupName!;
        // 手动指定时只认这一个：建包只接受一个手册，其余被猜成手册的分组不入包，并打印出来。
        var groups = scan.Groups
            .Select(group => string.Equals(group.Name, organizer, StringComparison.Ordinal)
                ? group with { Kind = ScriptGroupKind.Organizer }
                : group.Kind == ScriptGroupKind.Organizer ? group with { Kind = ScriptGroupKind.Ignore } : group)
            .ToArray();
        foreach (var dropped in scan.Groups.Where(g => g.Kind == ScriptGroupKind.Organizer && g.Name != organizer))
        {
            Console.WriteLine($"  不入包：{dropped.Name}（也像手册，但已指定 {organizer}）");
        }
        if (groups.All(group => group.Kind != ScriptGroupKind.Organizer))
        {
            Console.WriteLine($"! 指定的组织者手册分组不存在：{organizer}");
            return 2;
        }

        var pagesDone = 0;
        var lastReport = Stopwatch.StartNew();
        var total = Stopwatch.StartNew();
        var progress = new Progress<ImportProgress>(p =>
        {
            pagesDone = p.Completed;
            if (p.Completed == p.Total || lastReport.ElapsedMilliseconds > 2000)
            {
                lastReport.Restart();
                Console.WriteLine($"  识别 {p.Completed}/{p.Total} 页，失败 {p.FailedCount} 页");
            }
        });

        var extraction = await service.ExtractAsync(storyRoot, groups, progress, CancellationToken.None);
        if (extraction.IsBlocked)
        {
            Console.WriteLine($"preflight blocked: {extraction.BlockedCode}");
            Console.WriteLine($"  {extraction.BlockedReason}");
            return 2;
        }

        // 没有流程声明就不建包：宁可停在待复核，也不替剧本编一个默认流程。
        if (flow is null)
        {
            Console.WriteLine("! 没有流程声明（--flow）：这份包只会生成一个「流程待确认」阶段，不能开局。");
        }

        var flowToUse = flow ?? new ScriptFlow();
        // 没识别出文字的文件要人确认：命令行用 --blank-ok 表示看过了，是插图或空白页。
        var confirmed = extraction with { BlankFilesConfirmed = args.Contains("--blank-ok") };
        var result = await service.BuildAsync(confirmed, title, playerCount, flowToUse, CancellationToken.None);
        var built = result.Build;

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
            pagesExtracted = extraction.Pages.Count,
            totalPages = groups.Where(g => g.Kind != ScriptGroupKind.Ignore).Sum(g => g.PageCount),
            failedPages = extraction.FailedPages.Count,
            blankFiles = extraction.BlankFiles.Count,
            segmentCounts = built.SegmentCounts.ToDictionary(k => k.Key.ToString(), v => v.Value),
            truthCharacters = built.TruthCharacters,
            roleGroupCount = built.RoleGroupCount,
            materialGroupCount = built.MaterialGroupCount,
            phases = built.Package.Phases.Select(p => new { p.PhaseId, p.Title, p.IsVotePhase, p.IsRevealPhase }),
            flowDeclared = built.FlowDeclared,
            stepsNeedingReview = built.StepsNeedingReview,
            referenceIssues = built.ReferenceIssues,
            contentHash = built.Package.ContentHash,
            packagePath = Path.GetRelativePath(DmAcceptanceProbe.FindRepoRoot(), result.PackagePath),
            flowPath = Path.GetRelativePath(DmAcceptanceProbe.FindRepoRoot(), result.FlowPath),
            elapsedMs = total.ElapsedMilliseconds,
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
                          $"pages={extraction.Pages.Count}");
        Console.WriteLine($"  角色本分组={built.RoleGroupCount} 线索卡牌分组={built.MaterialGroupCount}");
        Console.WriteLine($"  内容指纹={built.Package.ContentHash}（换了流程就会变，旧存档不会接受新包）");

        if (extraction.FailedPages.Count > 0)
        {
            Console.WriteLine($"  ! {extraction.FailedPages.Count} 个文件识别出错，内容没进包：{string.Join("、", extraction.FailedPages)}");
        }

        if (extraction.BlankFiles.Count > 0)
        {
            Console.WriteLine($"  {extraction.BlankFiles.Count} 个文件没有识别出文字（插图或空白页）：{string.Join("、", extraction.BlankFiles)}");
        }

        if (built.MaterialGroupCount == 0)
        {
            Console.WriteLine("  ! 没有识别到任何线索卡牌分组：主持台将无可发放的编号，请人工确认剧本是否真的没有线索。");
        }

        if (built.RoleGroupCount != playerCount)
        {
            Console.WriteLine($"  ! 识别到 {built.RoleGroupCount} 个角色本分组，但本子是 {playerCount} 人：请人工核对是否有角色本被误判成线索卡。");
        }

        Console.WriteLine("wrote " + reportPath);
        return built.Readiness.CanStart ? 0 : 1;
    }

    /// <summary>
    /// 读取流程声明文件。流程必须由准备者按剧本原文写出，软件不提供默认流程。
    /// 缺省时返回 null，包会因此停在 NeedsReview。
    /// </summary>
    private static ScriptFlow? LoadFlow(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() },
        };
        return JsonSerializer.Deserialize<ScriptFlow>(File.ReadAllText(path), options)
               ?? throw new InvalidOperationException("流程声明文件无法解析。");
    }
}
