using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Infrastructure.Import;
using LocalDm.Infrastructure.Magpie;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 方案第 5 节的人工验收：用真实的导入向导 ViewModel 跑一遍真实剧本文件夹。
///
/// 驱动的都是软件自己的代码路径：扫描、识别（真 MinerU）、起草（真模型）、建包。
/// 只打印分组名、页数、流程结构与就绪结论 —— 不打印任何识别文字、卡面或真相，
/// 这样输出可以进仓库。
/// </summary>
public static class ImportWizardProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var storyRoot = ArgumentValue(args, "--story")
            ?? throw new ArgumentException("需要 --story <剧本文件夹>");
        var dataRootSetting = ArgumentValue(args, "--settings-data")
            ?? Path.Combine(repoRoot, ".appdata", "localdm-data");
        var workRoot = ArgumentValue(args, "--data")
            ?? Path.Combine(repoRoot, ".local", "import-acceptance", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var modelOverride = ArgumentValue(args, "--model");
        var mineruHome = ArgumentValue(args, "--home");
        var players = int.Parse(ArgumentValue(args, "--players") ?? "5");
        var titleOverride = ArgumentValue(args, "--title");
        var outFile = ArgumentValue(args, "--out");

        var dataRoot = Path.Combine(workRoot, "data");
        var packagesDir = Path.Combine(dataRoot, "packages");
        Directory.CreateDirectory(packagesDir);

        var settingsStore = new SettingsStore(
            Path.Combine(dataRootSetting, "settings.json"),
            Path.Combine(dataRootSetting, "secrets"));
        var settings = settingsStore.Load();
        var modelId = modelOverride ?? settings.ModelId;

        Console.WriteLine($"剧本文件夹：{storyRoot}");
        Console.WriteLine($"记录目录：{workRoot}");
        Console.WriteLine($"模型：{modelId}（用户设置：{settings.ModelId}）");
        Console.WriteLine();

        var report = new StringBuilder();
        report.AppendLine($"# 导入向导人工验收 {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine();
        report.AppendLine($"- 剧本文件夹：`{Path.GetFileName(storyRoot.TrimEnd(Path.DirectorySeparatorChar))}`");
        report.AppendLine($"- 模型：`{modelId}`");
        report.AppendLine($"- 运行数据：`.local/import-acceptance/{Path.GetFileName(workRoot)}/data`（不进仓库）");
        report.AppendLine();

        var gateway = new MagpieGateway
        {
            BaseUri = settings.MagpieBaseUri,
            CallerKey = settingsStore.LoadCallerKey(settings.CallerKeyRef),
        };

        var wizard = new ImportWizardViewModel(
            dataRoot,
            packagesDir,
            () => new ScriptImportService(new ScriptImportOptions
            {
                DataRoot = dataRoot,
                MineruExecutable = settings.MineruExecutable,
                MineruHome = mineruHome,
                OcrPythonPath = settings.OcrPythonPath,
            }),
            new FlowDrafter(gateway),
            () => modelId);

        // 回归核对：用已有的流程文件跟新代码重建一次，不重新起草（模型每次文字不同，包标识没法比）。
        if (ArgumentValue(args, "--reuse") is { } reusePath)
        {
            return await RunReuseAsync(reusePath, wizard, packagesDir, report, outFile);
        }

        // ===== ① + ② 扫描 =====
        wizard.SetFolder(storyRoot);
        wizard.Title = titleOverride ?? Path.GetFileName(storyRoot.TrimEnd(Path.DirectorySeparatorChar));
        wizard.PlayerCount = players;
        await wizard.ScanAsync();

        Console.WriteLine("【② 分类】");
        report.AppendLine("## ② 分类确认");
        report.AppendLine();
        report.AppendLine("| 分组 | 类别 | 页数 | 类型 | 猜测依据 |");
        report.AppendLine("|---|---|---|---|---|");
        foreach (var group in wizard.Groups)
        {
            Console.WriteLine($"  {group.Name,-16} {KindLabel(group.Kind),-6} {group.PageCount,3} 页  {group.FileTypeLabel,-8} {group.GuessReason}");
            report.AppendLine($"| {group.Name} | {KindLabel(group.Kind)} | {group.PageCount} | {group.FileTypeLabel} | {group.GuessReason} |");
        }

        await DiagnoseMineruAsync(settings.MineruExecutable, mineruHome);
        var organizer = SingleSafe(wizard.Groups.Where(group => group.Kind == ScriptGroupKind.Organizer));
        var roleCount = wizard.Groups.Count(group => group.Kind == ScriptGroupKind.Role);
        var materialCount = wizard.Groups.Count(group => group.Kind == ScriptGroupKind.Material);
        Console.WriteLine($"  组织者手册={organizer?.Name} 角色本={roleCount} 线索卡={materialCount}");
        report.AppendLine();
        report.AppendLine($"- 组织者手册：{organizer?.Name}");
        report.AppendLine($"- 角色本分组：{roleCount}（玩家人数 {players}）");
        report.AppendLine($"- 线索卡分组：{materialCount}");
        report.AppendLine($"- 页数合计：{wizard.Groups.Where(g => g.Kind != ScriptGroupKind.Ignore).Sum(g => g.PageCount)}");
        report.AppendLine();

        if (organizer is null)
        {
            Console.WriteLine("! 没有认出组织者手册，停止。");
            report.AppendLine("**结论**：② 页没能认出组织者手册，验收中止。");
            WriteReport(report, outFile);
            return 2;
        }

        // ===== ③ + ④ 识别与起草 =====
        Console.WriteLine();
        Console.WriteLine("【③ 识别 + ④ 起草】（每 20 页报一次进度）");
        var stopwatch = Stopwatch.StartNew();
        var watching = true;
        var reporter = Task.Run(async () =>
        {
            while (watching)
            {
                await Task.Delay(15000);
                if (!string.IsNullOrEmpty(wizard.ProgressText))
                {
                    Console.WriteLine($"  {wizard.ProgressText} {wizard.EstimatedRemainingText} {wizard.FailedPagesText}");
                }
            }
        });

        await wizard.ExtractAndDraftAsync();
        watching = false;
        await reporter;
        stopwatch.Stop();

        Console.WriteLine($"  用时 {stopwatch.Elapsed.TotalMinutes:0.0} 分钟；状态：{wizard.Status}");
        report.AppendLine("## ③ 识别");
        report.AppendLine();
        report.AppendLine($"- 用时：{stopwatch.Elapsed.TotalMinutes:0.0} 分钟");
        report.AppendLine($"- 结束状态：{wizard.Status}");
        report.AppendLine($"- 失败页：{(string.IsNullOrEmpty(wizard.FailedPagesText) ? "无" : wizard.FailedPagesText)}");
        report.AppendLine();

        if (!wizard.IsFlowStep)
        {
            Console.WriteLine("! 没走到 ④，停止。");
            report.AppendLine("**结论**：没有走到流程确认这一步，验收中止（见上面的状态）。");
            WriteReport(report, outFile);
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine($"【④ 流程】{wizard.LayeringSummary}");
        Console.WriteLine($"  起草尝试：{wizard.DraftAttempts} 次");
        report.AppendLine("## ④ 流程确认");
        report.AppendLine();
        report.AppendLine($"- 分层摘要：{wizard.LayeringSummary}");
        report.AppendLine($"- 起草尝试：{wizard.DraftAttempts} 次");
        Console.WriteLine("  序号 类别     依据页      隐藏  标题");
        report.AppendLine();
        report.AppendLine("| # | 类别 | 依据页 | 隐藏 | 标题 |");
        report.AppendLine("|---|---|---|---|---|");
        for (var i = 0; i < wizard.FlowSteps.Count; i++)
        {
            var step = wizard.FlowSteps[i];
            Console.WriteLine($"  {i + 1,3} {step.KindLabel,-8} {step.SourceRefDisplay,-11} {(step.IsHidden ? "是" : "否"),-4} {step.Title}");
            report.AppendLine($"| {i + 1} | {step.KindLabel} | {step.SourceRefDisplay} | {(step.IsHidden ? "是" : "否")} | {step.Title} |");
        }

        if (wizard.DraftFailureReason is { } reason)
        {
            Console.WriteLine($"  ! 起草失败：{reason}");
            report.AppendLine($"- **起草失败**：{reason}");
        }

        var hidden = wizard.FlowSteps.Count(step => step.IsHidden);
        Console.WriteLine($"  被剧透检查隐藏：{hidden} 条");
        report.AppendLine();
        report.AppendLine($"- 被剧透检查隐藏的条目：{hidden} 条");

        // 命中的条目按 ④ 页的要求重写（这里写成不含剧情的中性提示），再继续建包。
        foreach (var step in wizard.FlowSteps.Where(step => step.IsHidden))
        {
            step.Hint = "按流程做下一步。";
        }

        Console.WriteLine($"  能建包：{wizard.CanBuild}；揭示环节 {wizard.FlowSteps.Count(s => s.Kind == ScriptStepKind.Reveal)} 个");
        report.AppendLine($"- 建包前 CanBuild={wizard.CanBuild}；揭示环节 {wizard.FlowSteps.Count(s => s.Kind == ScriptStepKind.Reveal)} 个");
        report.AppendLine();

        // ===== ⑤ 建包 =====
        await wizard.BuildAsync();
        Console.WriteLine();
        Console.WriteLine($"【⑤ 完成】{wizard.ReadinessText}");
        report.AppendLine("## ⑤ 完成");
        report.AppendLine();
        report.AppendLine($"- 就绪：{wizard.ReadinessText}");

        var built = Directory.GetFiles(packagesDir, "*.json")
            .Where(path => !path.EndsWith(".flow.json", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (built is null)
        {
            Console.WriteLine("! 没有写出包文件。");
            report.AppendLine("- **没有写出包文件**");
            WriteReport(report, outFile);
            return 4;
        }

        var package = JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(built))!;
        var flowPath = Path.ChangeExtension(built, ".flow.json");
        Console.WriteLine($"  包={package.PackageId} 角色本={package.Roles.Count} 线索卡={package.Materials.Count} 段={package.Segments.Count}");
        report.AppendLine($"- 包：`{package.PackageId}`，角色本 {package.Roles.Count} 组，线索卡 {package.Materials.Count} 组，段 {package.Segments.Count}");
        report.AppendLine($"- 流程文件：`{Path.GetFileName(flowPath)}`（存在={File.Exists(flowPath)}）");

        // 剧本库能不能看到这一本
        var library = new LibraryViewModel(packagesDir);
        await library.LoadAsync(CancellationToken.None);
        var listed = library.Packages.Any(row => row.PackageId == package.PackageId);
        Console.WriteLine($"  剧本库列出这一本：{listed}");
        report.AppendLine($"- 剧本库列出这一本：{listed}");
        report.AppendLine();

        // ===== 缓存复核：同一文件夹再导一次 =====
        Console.WriteLine();
        Console.WriteLine("【缓存复核】同一文件夹再识别一次（应全部命中缓存，不再调 MinerU）");
        var again = Stopwatch.StartNew();
        var secondService = new ScriptImportService(new ScriptImportOptions
        {
            DataRoot = dataRoot,
            MineruExecutable = settings.MineruExecutable,
            MineruHome = mineruHome,
            OcrPythonPath = settings.OcrPythonPath,
        });
        var groups = secondService.Scan(storyRoot).Groups;
        var second = await secondService.ExtractAsync(storyRoot, groups, progress: null, CancellationToken.None);
        again.Stop();
        var cacheFiles = Directory.Exists(Path.Combine(dataRoot, "ocr-cache"))
            ? Directory.GetFiles(Path.Combine(dataRoot, "ocr-cache"), "*.json").Length
            : 0;
        Console.WriteLine($"  用时 {again.Elapsed.TotalSeconds:0.0} 秒；页 {second.Pages.Count}；缓存文件 {cacheFiles}");
        report.AppendLine("## 缓存复核");
        report.AppendLine();
        report.AppendLine($"- 同一文件夹第二次识别：{again.Elapsed.TotalSeconds:0.0} 秒，页 {second.Pages.Count}，缓存条目 {cacheFiles}");
        report.AppendLine($"- 第一次识别用时：{stopwatch.Elapsed.TotalMinutes:0.0} 分钟");
        report.AppendLine();

        // ===== 与命令行生成的包对比 =====
        var cliPackage = NewestCliPackage(dataRootSetting);
        if (cliPackage is not null)
        {
            var cli = JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(cliPackage))!;
            var cliTruth = TruthLabels(cli, "组织");
            var newTruth = wizard.LayeringPages.Where(page => page.Layer == "真相").Select(page => page.PageNumber).OrderBy(n => n).ToArray();
            Console.WriteLine();
            Console.WriteLine($"【与命令行包对比】命令行 {cli.PackageId}：角色本={cli.Roles.Count} 线索卡={cli.Materials.Count} 真相页={string.Join(",", cliTruth)}");
            Console.WriteLine($"                        向导 {package.PackageId}：角色本={package.Roles.Count} 线索卡={package.Materials.Count} 真相页={string.Join(",", newTruth)}");
            report.AppendLine("## 与命令行生成的包对比");
            report.AppendLine();
            report.AppendLine("| 项目 | 命令行包 | 向导包 |");
            report.AppendLine("|---|---|---|");
            report.AppendLine($"| 包标识 | `{cli.PackageId}` | `{package.PackageId}` |");
            report.AppendLine($"| 角色本分组 | {cli.Roles.Count} | {package.Roles.Count} |");
            report.AppendLine($"| 线索卡分组 | {cli.Materials.Count} | {package.Materials.Count} |");
            report.AppendLine($"| 真相页 | {string.Join("、", cliTruth)} | {string.Join("、", newTruth)} |");
            report.AppendLine();
        }

        WriteReport(report, outFile);
        Console.WriteLine();
        Console.WriteLine("记录：" + (outFile ?? "（未指定 --out）"));
        return wizard.IsDoneStep ? 0 : 5;
    }

    /// <summary>
    /// 用已有的包与流程文件重建一次，不重新起草：改代码后核对段数、段 ID 与包标识有没有变。
    /// 重新起草会拿到不同的文字，包标识跟着变，那样的对比说明不了回归。
    /// </summary>
    private static async Task<int> RunReuseAsync(
        string packagePath,
        ImportWizardViewModel wizard,
        string packagesDir,
        StringBuilder report,
        string? outFile)
    {
        var flowPath = Path.ChangeExtension(packagePath, ".flow.json");
        if (!File.Exists(flowPath))
        {
            Console.WriteLine("! 找不到流程文件：" + flowPath);
            return 6;
        }

        var package = JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(packagePath))!;
        var flow = JsonSerializer.Deserialize<ScriptFlow>(
            File.ReadAllText(flowPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            })!;

        Console.WriteLine($"复用包：{package.PackageId}（{Path.GetFileName(packagePath)}）");
        report.AppendLine("## 复用已有流程重建（回归核对）");
        report.AppendLine();
        report.AppendLine($"- 原包：`{package.PackageId}`，段 {package.Segments.Count}");
        report.AppendLine();

        await wizard.OpenExistingFlowAsync(package, flow);
        Console.WriteLine($"  状态：{wizard.Status}");
        if (!wizard.IsFlowStep)
        {
            Console.WriteLine("! 没走到 ④，停止。");
            WriteReport(report, outFile);
            return 7;
        }

        await wizard.BuildAsync();
        Console.WriteLine($"  就绪：{wizard.ReadinessText}");

        var built = Directory.GetFiles(packagesDir, "*.json")
            .Where(path => !path.EndsWith(".flow.json", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (built is null)
        {
            Console.WriteLine("! 没有写出包文件。");
            WriteReport(report, outFile);
            return 8;
        }

        var rebuilt = JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(built))!;
        var beforeIds = package.Segments.Select(segment => segment.SegmentId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var afterIds = rebuilt.Segments.Select(segment => segment.SegmentId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var sameIds = beforeIds.SequenceEqual(afterIds, StringComparer.Ordinal);
        var samePackageId = string.Equals(package.PackageId, rebuilt.PackageId, StringComparison.Ordinal);

        Console.WriteLine($"  新包：{rebuilt.PackageId} 段={rebuilt.Segments.Count}；段 ID 一致={sameIds}；包标识一致={samePackageId}");
        report.AppendLine($"- 重建后的包：`{rebuilt.PackageId}`，段 {rebuilt.Segments.Count}");
        report.AppendLine($"- 段 ID 逐条一致：{(sameIds ? "是" : "否")}");
        report.AppendLine($"- 包标识一致：{(samePackageId ? "是" : "否")}");
        report.AppendLine();

        if (!sameIds || !samePackageId)
        {
            var missing = beforeIds.Except(afterIds, StringComparer.Ordinal).Take(10).ToArray();
            var added = afterIds.Except(beforeIds, StringComparer.Ordinal).Take(10).ToArray();
            Console.WriteLine($"  只在这一版里有的段 ID：{string.Join("、", missing)}");
            Console.WriteLine($"  只在新包里有的段 ID：{string.Join("、", added)}");
        }

        WriteReport(report, outFile);
        return sameIds && samePackageId ? 0 : 9;
    }


    /// <summary>诊断：按 MineruRunner 的方式起子进程取两份状态 JSON，只打印关键字与错误码。</summary>
    private static async Task DiagnoseMineruAsync(string? executable, string? home)
    {
        var exe = executable ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "mineru.exe");
        var appHome = home ?? Path.Combine(Path.GetTempPath(), "no-home");
        foreach (var area in new[] { "server", "telemetry" })
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in area == "server" ? new[] { "server", "status", "--json" } : new[] { "telemetry", "status", "--json" })
            {
                startInfo.ArgumentList.Add(arg);
            }

            foreach (var key in startInfo.Environment.Keys.Where(k => k.StartsWith("MINERU_", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                startInfo.Environment.Remove(key);
            }

            startInfo.Environment["MINERU_HOME"] = appHome;
            using var process = Process.Start(startInfo)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var trimmed = stdout.Replace("\r", string.Empty).Replace("\n", " ");
            Console.WriteLine($"  [诊断] {area}: {(trimmed.Length > 220 ? trimmed[..220] : trimmed)}");
        }
    }

    private static string KindLabel(ScriptGroupKind kind) => kind switch
    {
        ScriptGroupKind.Organizer => "组织者手册",
        ScriptGroupKind.Role => "角色本",
        ScriptGroupKind.Material => "线索卡",
        _ => "不进包",
    };

    /// <summary>命令行包里的真相页：按组织者手册分组的段标签（文件名）推断页码。</summary>
    private static IReadOnlyList<int> TruthLabels(ScriptPackage package, string organizerGroup)
    {
        var truthIds = package.Truth.RevealedSegmentIds.ToHashSet(StringComparer.Ordinal);
        return package.Segments
            .Where(segment => truthIds.Contains(segment.SegmentId))
            .Select(segment => segment.SegmentId.Replace($"seg-{organizerGroup}-", string.Empty, StringComparison.Ordinal))
            .Select(label => int.TryParse(label, out var page) ? page : 0)
            .Where(page => page > 0)
            .OrderBy(page => page)
            .ToArray();
    }

    private static string? NewestCliPackage(string dataRootSetting)
    {
        var packages = Path.Combine(dataRootSetting, "packages");
        return Directory.Exists(packages)
            ? Directory.GetFiles(packages, "pkg-*.json")
                .Where(path => !path.EndsWith(".flow.json", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    private static void WriteReport(StringBuilder report, string? outFile)
    {
        if (string.IsNullOrWhiteSpace(outFile))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, report.ToString(), new UTF8Encoding(false));
    }

    private static T? SingleSafe<T>(IEnumerable<T> items)
        where T : class
    {
        var list = items.ToArray();
        return list.Length == 1 ? list[0] : list.FirstOrDefault();
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
}
