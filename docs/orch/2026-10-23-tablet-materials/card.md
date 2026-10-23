# tablet-materials — 平板材料旁路清单、导入时备好、剧本库不显示版本号

## Goal
- 没有平板清单的剧本包，调一次 `TabletMaterials.Ensure(package, packagePath)` 就得到带清单的包：包标识、版本、内容指纹不变，清单存进 `assets/<包标识>.tablet.json`，第二次调用直接读文件。
- 导入向导建包后（`BuildAsync`）自动调用一次，失败不拦导入。
- 剧本库每行摘要不含版本号。

## Risk
medium

## Parameters
- 旁路文件路径：`<包文件所在目录>/../assets/<PackageId>.tablet.json`（与 `TabletAssetRoot.Resolve` 同一个 assets 目录规则）。
- 整组确认：`package.Materials` 里全部 `MaterialId` 都当「一张卡一个文件」，传给 `TabletPackagePreparer.Prepare(package, assetsDir, confirmed)`；配不唯一或文件对不上的组由准备器自己暂停，不另处理。
- 旁路文件的资产根目录 `assets/<manifest.AssetRootFingerprint>` 不存在时视为没存过，重新准备。

## Files
may change: `src/LocalDm.Infrastructure/Import/TabletMaterials.cs`, `src/LocalDm.Infrastructure/Import/ScriptImportService.Extract.cs`, `src/LocalDm.App/ViewModels/LibraryViewModel.cs`, `tests/LocalDm.Tests/TabletPackageModeTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 4)
1. 新建 `src/LocalDm.Infrastructure/Import/TabletMaterials.cs`，原样写入：
```csharp
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
```
2. `src/LocalDm.Infrastructure/Import/ScriptImportService.Extract.cs` 的 `BuildAsync` 末尾，紧挨在 `return new ScriptImportResult { Build = built, PackagePath = packagePath, FlowPath = flowPath };` 这一行之前，插入：
```csharp
        // 第一次导入就把平板材料备好；失败不拦导入，开局时还会再补一次。
        try
        {
            await Task.Run(() => TabletMaterials.Ensure(built.Package, packagePath), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine("[Tablet] 导入时备平板材料失败：" + ex.GetType().Name);
        }

```
3. `src/LocalDm.App/ViewModels/LibraryViewModel.cs`：`LibraryPackageRow` 里的 `Summary` 属性（`grep -n "public string Summary"` 找到那一行），把表达式改成 `$"{PlayerCount} 人 · {PageCount} 页来源 · {Readiness}"`（去掉开头的 `v{Version} · `）。其余不动。
4. `tests/LocalDm.Tests/TabletPackageModeTests.cs`：在 `PrepareSyntheticPackage` 方法之前加一个测试，复用同类里已有的 `PrepareSyntheticPackage()`：
```csharp
    [Fact]
    public void Tablet_materials_ensure_writes_a_sidecar_without_changing_package_identity()
    {
        var (packagePath, _) = PrepareSyntheticPackage();
        var original = JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(packagePath))!;
        var bare = original with { Tablet = null };

        var first = TabletMaterials.Ensure(bare, packagePath);
        var sidecar = Path.Combine(_root, "assets", bare.PackageId + ".tablet.json");

        Assert.NotNull(first.Tablet);
        Assert.Equal(bare.PackageId, first.PackageId);
        Assert.Equal(bare.Version, first.Version);
        Assert.Equal(bare.ContentHash, first.ContentHash);
        Assert.True(File.Exists(sidecar));

        var second = TabletMaterials.Ensure(bare, packagePath);
        Assert.Equal(first.Tablet!.AssetRootFingerprint, second.Tablet!.AssetRootFingerprint);
        Assert.Same(original, TabletMaterials.Ensure(original, packagePath));
    }
```

## Check
`dotnet test tests/LocalDm.Tests --filter-method "*Tablet_materials_ensure*"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.
