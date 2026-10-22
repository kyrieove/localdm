using System.Security.Cryptography;
using System.Text;
using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 给已经冻结的剧本包准备平板清单与资产。
///
/// 只处理经人确认「一张卡一个文件」的材料：按文件名里的编号配正面与卡背。
/// 未确认版式、配不唯一、缺正面或文件对不上的组一律保持未准备好 ——
/// 不用整页图代替单张，也不把没配好的卡混在可用的组里发出去。
///
/// 资产按内容指纹单独存放：换一版包不会覆盖上一版的图，旧页面也取不到新局的同号卡。
/// </summary>
public static class TabletPackagePreparer
{
    /// <summary>
    /// 准备清单并把资产落盘。
    ///
    /// <paramref name="assetsBaseDirectory"/> 是资产总目录；本次的资产落在它下面的
    /// 「内容指纹」子目录里。返回的清单带着同一个指纹。
    /// 只有 <paramref name="confirmedSingleCardMaterialIds"/> 明确列出的材料组才按单卡文件处理；
    /// 省略时所有未知版式都暂停。
    /// </summary>
    public static TabletManifest Prepare(
        ScriptPackage package,
        string assetsBaseDirectory,
        IReadOnlySet<string>? confirmedSingleCardMaterialIds = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsBaseDirectory);

        var confirmedGroups = confirmedSingleCardMaterialIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(confirmedSingleCardMaterialIds, StringComparer.Ordinal);
        var packageGroups = package.Materials.Select(material => material.MaterialId).ToHashSet(StringComparer.Ordinal);
        if (confirmedGroups.Any(group => !packageGroups.Contains(group)))
        {
            throw new ArgumentException("单卡确认中包含剧本包没有的材料组。", nameof(confirmedSingleCardMaterialIds));
        }

        var files = package.SourceFiles.ToDictionary(f => f.FileId, StringComparer.Ordinal);
        var spans = package.Spans.ToDictionary(s => s.SpanId, StringComparer.Ordinal);

        var cards = new Dictionary<string, TabletCard>(StringComparer.Ordinal);
        var assets = new Dictionary<string, TabletAsset>(StringComparer.Ordinal);
        var groups = new Dictionary<string, TabletGroupPreparationState>(StringComparer.Ordinal);

        // 先把每组的配对结果与资产计划算出来：资产指纹要按全部资产的 hash 算。
        var plans = new List<CardPlan>();

        foreach (var material in package.Materials)
        {
            var groupFiles = material.SourceSpanIds
                .Select(id => spans.TryGetValue(id, out var span) ? files.GetValueOrDefault(span.FileId) : null)
                .Where(f => f is not null)
                .Select(f => f!)
                .ToArray();

            var candidates = TabletAssetBuilder.PairSingleCards(groupFiles.Select(f => f.PrivatePath).ToArray());
            var notReady = 0;
            TabletNotReadyReason? firstReason = null;

            if (!confirmedGroups.Contains(material.MaterialId))
            {
                foreach (var candidate in candidates)
                {
                    var cardId = CardId(material.MaterialId, candidate.SourceOrder);
                    cards[cardId] = UnavailableCard(
                        cardId,
                        material.MaterialId,
                        candidate,
                        TabletNotReadyReason.CropUnconfirmed);
                }

                groups[material.MaterialId] = new TabletGroupPreparationState
                {
                    GroupName = material.MaterialId,
                    Readiness = TabletGroupReadiness.NotReady,
                    Reason = TabletNotReadyReason.CropUnconfirmed,
                    NotReadyCardCount = candidates.Count,
                };
                continue;
            }

            foreach (var candidate in candidates)
            {
                var cardId = CardId(material.MaterialId, candidate.SourceOrder);
                if (!candidate.IsConsistent || candidate.FrontPath is null)
                {
                    notReady++;
                    var reason = candidate.Conflicts.Count > 0
                        ? candidate.Conflicts[0]
                        : TabletNotReadyReason.MissingFront;
                    firstReason ??= reason;

                    // 配不出来的卡也要进清单：目录里要让人看得到「这一组有问题」，
                    // 只是标成不可用、点不开。少了它，这一组在平板上就像根本不存在。
                    cards[cardId] = new TabletCard
                    {
                        CardId = cardId,
                        DisplayName = DisplayName(candidate.FrontPath ?? candidate.BackPath ?? cardId),
                        GroupName = material.MaterialId,
                        SourceOrder = candidate.SourceOrder,
                        FrontAssetId = cardId + "-front",
                        BackAssetId = candidate.BackPath is null ? null : cardId + "-back",
                        PreparationState = TabletCardPreparationState.NotReady,
                        NotReadyReason = reason,
                        SourceFileName = Path.GetFileNameWithoutExtension(
                            candidate.FrontPath ?? candidate.BackPath ?? cardId),
                    };
                    continue;
                }

                var frontFile = files.Values.FirstOrDefault(f =>
                    string.Equals(f.PrivatePath, candidate.FrontPath, StringComparison.Ordinal));
                if (frontFile is null || !File.Exists(frontFile.PrivatePath))
                {
                    notReady++;
                    firstReason ??= TabletNotReadyReason.AssetMissing;
                    cards[cardId] = UnavailableCard(cardId, material.MaterialId, candidate, TabletNotReadyReason.AssetMissing);
                    continue;
                }

                // 源文件被换过就不能分发：包里记的是旧 hash，对不上说明这份材料已经变了。
                var frontHash = TabletAssetBuilder.ComputeFileHash(frontFile.PrivatePath);
                if (!string.Equals(frontHash, frontFile.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    notReady++;
                    firstReason ??= TabletNotReadyReason.AssetMissing;
                    cards[cardId] = UnavailableCard(cardId, material.MaterialId, candidate, TabletNotReadyReason.AssetMissing);
                    continue;
                }

                var frontAssetId = cardId + "-front";
                var frontExtension = Path.GetExtension(frontFile.PrivatePath).TrimStart('.').ToLowerInvariant();
                var frontRelative = TabletAssetBuilder.SingleAssetRelativePath(frontAssetId, frontExtension);

                var plan = new CardPlan
                {
                    CardId = cardId,
                    // 分组名用 MaterialId：Label 是给人看的装饰（带「线索卡牌：」与页数），
                    // 拿它当分组名会在平板目录里套出一层括号。
                    GroupName = material.MaterialId,
                    SourceOrder = candidate.SourceOrder,
                    DisplayName = DisplayName(candidate.FrontPath),
                    SourceFileId = frontFile.FileId,
                    SourceFileName = Path.GetFileNameWithoutExtension(candidate.FrontPath),
                    FrontAssetId = frontAssetId,
                    FrontPath = candidate.FrontPath,
                    FrontHash = frontHash,
                    FrontRelativePath = frontRelative,
                    FrontExtension = frontExtension,
                };

                if (candidate.BackPath is not null)
                {
                    var backFile = files.Values.FirstOrDefault(f =>
                        string.Equals(f.PrivatePath, candidate.BackPath, StringComparison.Ordinal));
                    if (backFile is null || !File.Exists(backFile.PrivatePath))
                    {
                        notReady++;
                        firstReason ??= TabletNotReadyReason.AssetMissing;
                        cards[cardId] = UnavailableCard(
                            cardId,
                            material.MaterialId,
                            candidate,
                            TabletNotReadyReason.AssetMissing);
                        continue;
                    }

                    var backHash = TabletAssetBuilder.ComputeFileHash(backFile.PrivatePath);
                    if (!string.Equals(backHash, backFile.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        notReady++;
                        firstReason ??= TabletNotReadyReason.AssetMissing;
                        cards[cardId] = UnavailableCard(
                            cardId,
                            material.MaterialId,
                            candidate,
                            TabletNotReadyReason.AssetMissing);
                        continue;
                    }

                    var backAssetId = cardId + "-back";
                    var backExtension = Path.GetExtension(backFile.PrivatePath).TrimStart('.').ToLowerInvariant();
                    plan.BackAssetId = backAssetId;
                    plan.BackSourceFileId = backFile.FileId;
                    plan.BackPath = backFile.PrivatePath;
                    plan.BackHash = backHash;
                    plan.BackExtension = backExtension;
                    plan.BackRelativePath = TabletAssetBuilder.SingleAssetRelativePath(backAssetId, backExtension);
                }

                plans.Add(plan);
            }

            // 分组状态与卡用同一个分组名（MaterialId）：两边对不上，
            // 查组就会查不到，整组会被当成没准备好。
            groups[material.MaterialId] = new TabletGroupPreparationState
            {
                GroupName = material.MaterialId,
                Readiness = notReady == 0 && candidates.Count > 0
                    ? TabletGroupReadiness.Ready
                    : TabletGroupReadiness.NotReady,
                Reason = notReady == 0 && candidates.Count > 0
                    ? null
                    : firstReason ?? TabletNotReadyReason.AssetMissing,
                NotReadyCardCount = notReady,
            };
        }

        var roleSourceFingerprint = RoleSourceAssetBuilder.SourceFingerprint(package);
        var fingerprint = Fingerprint(plans, roleSourceFingerprint);
        var assetRoot = Path.Combine(assetsBaseDirectory, fingerprint);
        Directory.CreateDirectory(assetRoot);
        var roleAssets = RoleSourceAssetBuilder.Materialize(package, assetRoot);

        var plannedPaths = new List<string>();
        var plannedAssetIds = new List<string>();
        foreach (var plan in plans)
        {
            plannedPaths.Add(plan.FrontRelativePath);
            plannedAssetIds.Add(plan.FrontAssetId);
            if (plan.BackRelativePath is not null && plan.BackAssetId is not null)
            {
                plannedPaths.Add(plan.BackRelativePath);
                plannedAssetIds.Add(plan.BackAssetId);
            }
        }

        foreach (var roleAsset in roleAssets)
        {
            plannedPaths.Add(roleAsset.RelativePath);
            plannedAssetIds.Add(roleAsset.AssetId);
        }

        EnsureUniqueRelativePaths(plannedPaths);
        if (plannedAssetIds.Distinct(StringComparer.Ordinal).Count() != plannedAssetIds.Count)
        {
            throw new InvalidOperationException("多个平板资产使用了同一个资产 ID。");
        }

        foreach (var roleAsset in roleAssets)
        {
            assets.Add(roleAsset.AssetId, roleAsset);
        }

        foreach (var plan in plans)
        {
            var front = TabletAssetBuilder.MaterializeSingle(
                assetRoot,
                plan.FrontAssetId,
                plan.SourceFileId,
                plan.FrontPath,
                TabletAssetKind.CardFront);
            assets.Add(plan.FrontAssetId, front);

            if (plan.BackAssetId is not null
                && plan.BackSourceFileId is not null
                && plan.BackPath is not null)
            {
                var back = TabletAssetBuilder.MaterializeSingle(
                    assetRoot,
                    plan.BackAssetId,
                    plan.BackSourceFileId,
                    plan.BackPath,
                    TabletAssetKind.CardBack);
                assets.Add(plan.BackAssetId, back);
            }

            cards[plan.CardId] = new TabletCard
            {
                CardId = plan.CardId,
                DisplayName = plan.DisplayName,
                GroupName = plan.GroupName,
                SourceOrder = plan.SourceOrder,
                FrontAssetId = plan.FrontAssetId,
                BackAssetId = plan.BackAssetId,
                PreparationState = TabletCardPreparationState.Ready,
                SourceFileId = plan.SourceFileId,
                SourceFileName = plan.SourceFileName,
            };
        }

        return new TabletManifest
        {
            ManifestVersion = TabletManifest.CurrentManifestVersion,
            AssetRootFingerprint = fingerprint,
            Cards = cards,
            Groups = groups,
            Assets = assets,
            PreparedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 资产目录的内容指纹：按「相对路径 + 内容 hash」排序后取 SHA-256。
    /// 同样的资产内容永远得到同一个指纹，资产目录因此可以复用而不会串。
    /// </summary>
    private static string Fingerprint(IReadOnlyList<CardPlan> plans, string roleSourceFingerprint)
    {
        var lines = new List<string> { $"role-source={roleSourceFingerprint}" };
        foreach (var plan in plans)
        {
            lines.Add($"{plan.FrontRelativePath}={plan.FrontHash}");
            if (plan.BackRelativePath is not null && plan.BackHash is not null)
            {
                lines.Add($"{plan.BackRelativePath}={plan.BackHash}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static void EnsureUniqueRelativePaths(IReadOnlyList<string> paths)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!unique.Add(path.Replace('\\', '/')))
            {
                throw new InvalidOperationException("多个平板资产对应同一个相对路径。");
            }
        }
    }

    /// <summary>
    /// 卡片 ID：组 + 来源编号。同一张卡重新准备也保持不变，
    /// 玩家点选与取图都靠它，换名不会换错卡。
    /// </summary>
    private static string CardId(string materialId, int sourceOrder) => $"{materialId}-{sourceOrder}";

    /// <summary>
    /// 不可用的卡：进目录、点不开，但玩家看得到「这一组有问题」。
    /// </summary>
    private static TabletCard UnavailableCard(
        string cardId,
        string groupName,
        SingleCardCandidate candidate,
        TabletNotReadyReason reason) => new()
        {
            CardId = cardId,
            DisplayName = DisplayName(candidate.FrontPath ?? candidate.BackPath ?? cardId),
            GroupName = groupName,
            SourceOrder = candidate.SourceOrder,
            FrontAssetId = cardId + "-front",
            BackAssetId = candidate.BackPath is null ? null : cardId + "-back",
            PreparationState = TabletCardPreparationState.NotReady,
            NotReadyReason = reason,
            SourceFileName = Path.GetFileNameWithoutExtension(
                candidate.FrontPath ?? candidate.BackPath ?? cardId),
        };

    /// <summary>
    /// 公开名称：卡面自己印的编号优先（文件名里的数字部分），
    /// 没有编号就用原文件名。分组名另外给，目录里同名卡靠它区分。
    /// </summary>
    private static string DisplayName(string frontPath)
    {
        var name = Path.GetFileNameWithoutExtension(frontPath);
        var digits = new string([.. name.Where(char.IsAsciiDigit)]);
        return digits.Length > 0 ? digits : name;
    }

    private sealed class CardPlan
    {
        public required string CardId { get; init; }
        public required string GroupName { get; init; }
        public required int SourceOrder { get; init; }
        public required string DisplayName { get; init; }
        public required string SourceFileId { get; init; }
        public required string SourceFileName { get; init; }
        public required string FrontAssetId { get; init; }
        public required string FrontPath { get; init; }
        public required string FrontHash { get; init; }
        public required string FrontRelativePath { get; init; }
        public required string FrontExtension { get; init; }
        public string? BackAssetId { get; set; }
        public string? BackSourceFileId { get; set; }
        public string? BackPath { get; set; }
        public string? BackHash { get; set; }
        public string? BackRelativePath { get; set; }
        public string? BackExtension { get; set; }
    }
}
