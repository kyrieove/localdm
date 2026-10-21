using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 平板线索卡准备：把一处导入分组里的图片整理成可点名的冻结资产。
///
/// 两条准备路径，都用通用参数，不为任何一本剧本写专属尺寸或别名：
/// 1. 单卡文件：一张正面配一张卡背，按文件名里的编号与正背面标记提出候选；
/// 2. 整版：按 <see cref="GridLayout"/> 行列切出单张，正背面靠槽位映射配对。
///
/// 每个派生资产都记原文件 hash、页号、网格与槽位，任何一张都能回溯到原图上是哪一格。
/// 配不准、切不完整、名称还缺的组一律保持未准备好，不用整页代替单张。
/// </summary>
public static partial class TabletAssetBuilder
{
    /// <summary>
    /// 单卡配对候选：按文件名里的编号把「正面」与「卡背」配起来。
    /// 同一个编号出现两张正面、或只有卡背没有正面时，候选上会记下冲突，由调用方把整组标成未准备好。
    /// </summary>
    public static IReadOnlyList<SingleCardCandidate> PairSingleCards(IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var byOrder = new Dictionary<int, (List<string> Fronts, List<string> Backs)>();
        foreach (var path in filePaths)
        {
            var (order, isBack) = ParseCardFile(path);
            if (!byOrder.TryGetValue(order, out var entry))
            {
                entry = ([], []);
                byOrder[order] = entry;
            }

            (isBack ? entry.Backs : entry.Fronts).Add(path);
        }

        var candidates = new List<SingleCardCandidate>(byOrder.Count);
        foreach (var (order, entry) in byOrder.OrderBy(pair => pair.Key))
        {
            var conflicts = new List<TabletNotReadyReason>();
            if (entry.Fronts.Count == 0)
            {
                // 只有卡背
                conflicts.Add(TabletNotReadyReason.MissingFront);
            }
            else if (entry.Fronts.Count > 1 || entry.Backs.Count > 1)
            {
                // 同一编号对上多张：正背面配不唯一
                conflicts.Add(TabletNotReadyReason.PairingConflict);
            }

            candidates.Add(new SingleCardCandidate
            {
                SourceOrder = order,
                FrontPath = entry.Fronts.OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault(),
                BackPath = entry.Backs.OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault(),
                Conflicts = conflicts,
            });
        }

        return candidates;
    }

    /// <summary>
    /// 把整版切出来的每一格落盘成资产，返回资产清单。
    /// <paramref name="assetRoot"/> 是本次包的资产目录；<paramref name="sourceFileId"/> 是来源文件 ID。
    /// </summary>
    public static IReadOnlyList<TabletAsset> MaterializeGrid(
        string assetRoot,
        string sourceFileId,
        string imagePath,
        GridLayout layout,
        int hashesStartingFrom = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(layout);

        var sourceHash = ComputeFileHash(imagePath);
        var cuts = GridCardCropper.Crop(imagePath, layout);
        var assets = new List<TabletAsset>(cuts.Count);
        Directory.CreateDirectory(assetRoot);

        foreach (var cut in cuts)
        {
            var assetId = $"asset-{hashesStartingFrom}-card-r{cut.Slot.Row}c{cut.Slot.Column}";
            hashesStartingFrom++;
            // 派生文件用稳定编号命名，不改名、不覆盖原始资料。
            var relativePath = $"cards/{Sanitize(sourceFileId)}-r{cut.Slot.Row}c{cut.Slot.Column}.png";
            var file = Path.Combine(assetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, cut.Png);

            assets.Add(new TabletAsset
            {
                AssetId = assetId,
                RelativePath = relativePath,
                ContentHash = PngCodec.Sha256(cut.Png),
                Kind = TabletAssetKind.CardFront,
                SourceFileId = sourceFileId,
                SourceContentHash = sourceHash,
                SourcePageNumber = 1,
                GridRows = layout.Rows,
                GridColumns = layout.Columns,
                GridRegion = layout.Region,
                Slot = cut.Slot,
            });
        }

        return assets;
    }

    /// <summary>
    /// 把一张单卡文件落盘成资产（复制字节，不重新编码：不损失原图质量）。
    /// </summary>
    public static TabletAsset MaterializeSingle(
        string assetRoot,
        string assetId,
        string sourceFileId,
        string imagePath,
        TabletAssetKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var bytes = File.ReadAllBytes(imagePath);
        var extension = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
        var relativePath = $"cards/{Sanitize(assetId)}.{extension}";
        var file = Path.Combine(assetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);

        return new TabletAsset
        {
            AssetId = assetId,
            RelativePath = relativePath,
            ContentHash = PngCodec.Sha256(bytes),
            Kind = kind,
            SourceFileId = sourceFileId,
            SourceContentHash = PngCodec.Sha256(bytes),
            SourcePageNumber = 1,
        };
    }

    /// <summary>文件内容 hash（SHA-256 小写十六进制），与来源文件同一算法。</summary>
    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// 从文件名里读「编号 + 是不是卡背」。
    /// 只看文件名里的数字与「背 / back / b」这类通用字面，不认任何剧本专属别名。
    /// </summary>
    internal static (int Order, bool IsBack) ParseCardFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var isBack = BackMarkerRegex().IsMatch(name);
        var match = NumberRegex().Match(name);
        var order = match.Success && int.TryParse(match.Value, out var number) ? number : 0;
        return (order, isBack);
    }

    private static string Sanitize(string value) =>
        new([.. value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

    /// <summary>卡背标记：中文「背 / 背面」或英文 back / b，大小写不限。</summary>
    [GeneratedRegex(@"背|_?back\b|\bb$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackMarkerRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();
}

/// <summary>单卡配对的一个候选：一个编号 + 它的正背面文件与冲突。</summary>
public sealed record SingleCardCandidate
{
    /// <summary>来源序号：文件名里的编号，也是同名卡的显示编号。</summary>
    public required int SourceOrder { get; init; }

    public string? FrontPath { get; init; }

    public string? BackPath { get; init; }

    /// <summary>冲突原因；为空表示这一对是明确的。</summary>
    public required IReadOnlyList<TabletNotReadyReason> Conflicts { get; init; }

    public bool IsConsistent => Conflicts.Count == 0;
}
