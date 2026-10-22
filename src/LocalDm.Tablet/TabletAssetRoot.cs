using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 资产目录的位置规则：包的同级目录下的 <c>assets/&lt;内容指纹&gt;</c>。
///
/// 准备清单的那边（<c>tools/LocalDm.Probe --prepare-tablet</c>）与服务这一边
/// 必须用同一条规则，所以规则只写在这里一份。
/// </summary>
public static class TabletAssetRoot
{
    /// <summary>按包的路径与清单指纹算出资产目录。</summary>
    public static string Resolve(string packagePath, TabletManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(manifest);

        var packageDir = Path.GetDirectoryName(Path.GetFullPath(packagePath))!;
        return Path.GetFullPath(Path.Combine(packageDir, "..", "assets", manifest.AssetRootFingerprint));
    }
}
