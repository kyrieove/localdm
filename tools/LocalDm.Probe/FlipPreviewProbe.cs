using System.Text;
using LocalDm.Infrastructure.Import;
using LocalDm.Infrastructure.Storage;
using LocalDm.Tablet;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 把一个角色的原稿页复制到 .local/flip-preview/，配上 docs/preview/role-flip-20261025 的翻页预览。
/// 含剧情，只写进不入库的 .local/；终端只打印路径、角色编号和页数。
/// </summary>
internal static class FlipPreviewProbe
{
    public static int Run(string[] args)
    {
        string? packageId = null, roleId = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--package": packageId = args[i + 1]; break;
                case "--role": roleId = args[i + 1]; break;
                default:
                    Console.WriteLine($"未知参数：{args[i]}");
                    return 2;
            }
        }

        if (packageId is null)
        {
            Console.WriteLine("要用 --package <pkg-id> 指定剧本包。");
            return 2;
        }

        var packagePath = Path.Combine(SessionContext.ResolveDataRoot(), "packages", packageId + ".json");
        var package = TabletMaterials.Ensure(DmAcceptanceProbe.LoadPackage(packagePath), packagePath);
        var content = new PackageTabletContent(package, TabletAssetRoot.Resolve(packagePath, package.Tablet!));
        Console.WriteLine($"角色：{string.Join("、", content.RoleBooks.Keys)}");

        roleId ??= content.RoleBooks.Keys.First();
        var count = content.RoleBooks[roleId].SourcePageAssetIds.Count;
        if (count == 0)
        {
            Console.WriteLine($"{roleId} 没有原稿页。");
            return 2;
        }

        var repoRoot = DmAcceptanceProbe.FindRepoRoot();
        var outDir = Path.Combine(repoRoot, ".local", "flip-preview", $"{packageId}-{roleId}");
        Directory.CreateDirectory(outDir);
        var entries = new List<string>();
        for (var page = 1; page <= count; page++)
        {
            if (!content.TryGetRoleSourcePage(roleId, page, out var bytes, out var contentType))
            {
                Console.WriteLine($"第 {page} 页取不到，停止。");
                return 1;
            }

            var name = $"p{page}{(contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg")}";
            File.WriteAllBytes(Path.Combine(outDir, name), bytes);
            entries.Add($"{{img:\"{name}\"}}");
        }

        File.WriteAllText(Path.Combine(outDir, "pages.js"), $"window.FLIP_PAGES=[{string.Join(",", entries)}];", new UTF8Encoding(false));
        File.Copy(Path.Combine(repoRoot, "docs", "preview", "role-flip-20261025", "index.html"), Path.Combine(outDir, "index.html"), overwrite: true);
        Console.WriteLine($"已写 {count} 页：{Path.Combine(outDir, "index.html")}");
        return 0;
    }
}
