using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 保证一本剧本有可分发的平板清单，且不生成新版本、不改包标识。
/// 清单已写在包里就直接用；否则读 assets/&lt;包标识&gt;.tablet.json；都没有才现场准备并存成这个旁路文件。
/// 导入时调一次，旧包在软件启动后台补一次，以后每次开局只是读文件。
/// ponytail: 按「一张卡一个文件」整组确认；整版多卡的剧本要人逐组确认版式，那一步还没有界面。
/// </summary>
public static class TabletMaterials
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ScriptPackage Ensure(ScriptPackage package, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Tablet is not null)
        {
            return package;
        }

        var assetsDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(packagePath))!, "..", "assets"));
        var sidecar = Path.Combine(assetsDir, package.PackageId + ".tablet.json");

        lock (Gate)
        {
            if (TryRead(sidecar, assetsDir) is { } saved)
            {
                return package with { Tablet = saved };
            }

            var confirmed = package.Materials.Select(m => m.MaterialId).ToHashSet(StringComparer.Ordinal);
            var manifest = TabletPackagePreparer.Prepare(package, assetsDir, confirmed);
            Directory.CreateDirectory(assetsDir);
            File.WriteAllText(sidecar, JsonSerializer.Serialize(manifest, Options));
            return package with { Tablet = manifest };
        }
    }

    private static TabletManifest? TryRead(string sidecar, string assetsDir)
    {
        if (!File.Exists(sidecar))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<TabletManifest>(File.ReadAllText(sidecar));
            return manifest is not null && Directory.Exists(Path.Combine(assetsDir, manifest.AssetRootFingerprint))
                ? manifest
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
