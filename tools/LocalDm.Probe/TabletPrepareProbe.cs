using System.Globalization;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 给已经冻结的剧本包准备平板清单与资产。
///
/// 重新准备会生成新版本（新包标识 + 新内容指纹）：旧包留在原处照常走纸质模式，
/// 旧存档也不会静默接受新资产、新名字。
///
/// 只报结构与数字：卡数、组数、准备好几组、资产数。不输出任何剧本内容。
/// </summary>
internal static class TabletPrepareProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? packagePath = null;
        string? assetsDir = null;
        string? outPath = null;
        var confirmedSingleCardMaterialIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--package" when i + 1 < args.Length:
                    packagePath = args[++i];
                    break;
                case "--assets" when i + 1 < args.Length:
                    assetsDir = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--confirm-single" when i + 1 < args.Length:
                    confirmedSingleCardMaterialIds.Add(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine(
                        "用法：--prepare-tablet --package <包.json> [--assets <目录>] [--out <包.json>] " +
                        "[--confirm-single <材料组 ID> ...]");
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            Console.Error.WriteLine("找不到剧本包文件。用 --package 指定。");
            return 2;
        }

        var packageDir = Path.GetDirectoryName(Path.GetFullPath(packagePath))!;
        assetsDir ??= Path.Combine(packageDir, "..", "assets");

        ScriptPackage package;
        try
        {
            package = JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(packagePath))
                ?? throw new InvalidOperationException("剧本包读不出来。");
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine("剧本包不是合法 JSON：" + ex.Message);
            return 2;
        }

        var manifest = TabletPackagePreparer.Prepare(
            package,
            Path.GetFullPath(assetsDir),
            confirmedSingleCardMaterialIds);
        var prepared = RealScriptPackageBuilder.WithTabletManifest(package, manifest);

        // 重新准备算新版本：旧版本文件留着，纸质模式照旧可用。
        var nextVersion = int.TryParse(package.Version, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? (v + 1).ToString(CultureInfo.InvariantCulture)
            : "2";
        prepared = prepared with { Version = nextVersion };

        var target = outPath ?? Path.Combine(
            packageDir,
            $"{prepared.PackageId}-{prepared.Version}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        await File.WriteAllTextAsync(
            target,
            JsonSerializer.Serialize(prepared, new JsonSerializerOptions { WriteIndented = true }));

        var readyGroups = manifest.Groups.Values.Count(g => g.IsReady);
        Console.WriteLine($"包：{prepared.Title}（{prepared.PlayerCount} 人）");
        Console.WriteLine($"新包标识：{prepared.PackageId} 版本：{prepared.Version}");
        Console.WriteLine($"内容指纹：{prepared.ContentHash}");
        Console.WriteLine($"清单：卡 {manifest.Cards.Count} 张，组 {manifest.Groups.Count} 个，准备好 {readyGroups} 组");
        Console.WriteLine($"资产：{manifest.Assets.Count} 个，目录 {manifest.AssetRootFingerprint}");

        // 逐组报账：每组多少张卡、多少张没准备好。只报数字与分组名。
        foreach (var group in manifest.Groups.Values.OrderBy(g => g.GroupName, StringComparer.Ordinal))
        {
            var cards = manifest.Cards.Values.Where(c => c.GroupName == group.GroupName).ToArray();
            var ready = cards.Count(c => c.PreparationState == TabletCardPreparationState.Ready);
            Console.WriteLine(
                $"  组 {group.GroupName}：卡 {cards.Length}，可用 {ready}，未就绪 {group.NotReadyCardCount}"
                + (group.IsReady ? string.Empty : $"（原因 {group.Reason}）"));
        }

        Console.WriteLine($"写入：{target}");
        return 0;
    }
}
