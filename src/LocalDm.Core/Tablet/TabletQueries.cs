using LocalDm.Core.Domain;

namespace LocalDm.Core.Tablet;

/// <summary>
/// 平板名称目录的只读查询。网页、导入准备区与测试都走这一份，
/// 不各写一套「按名字找图」的逻辑。
///
/// 三条硬规则：
/// 1. 取图只按 <see cref="TabletCard.CardId"/> 与资产索引解析，不按名称拼路径；
/// 2. 同名卡不会「搜到谁就给谁」，全部返回，由调用方自己选；
/// 3. 组没准备好就明确取不到，不退回整页。
///
/// 这里<b>没有按环节解锁的判断</b>：全部已准备好的卡从加入后就可访问。
/// </summary>
public static class TabletQueries
{
    /// <summary>有平板清单才算准备过；旧包没有清单时用纸质模式。</summary>
    public static bool HasManifest(ScriptPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.Tablet is not null;
    }

    /// <summary>取平板清单；旧包没有就返回 null（不是空清单，两者含义不同）。</summary>
    public static TabletManifest? RequireManifest(ScriptPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.Tablet;
    }

    /// <summary>
    /// 名称目录：全部卡按「分组首次出现的顺序 + 组内来源序号」排好。
    /// 顺序稳定，刷新页面不会跳位。未准备好的卡也在目录里（要让人知道缺哪组），但标成不可用。
    /// </summary>
    public static IReadOnlyList<TabletCatalogItem> BuildCatalog(TabletManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var duplicated = DuplicatedNames(manifest.Cards.Values);

        return Ordered(manifest)
            .Select(card => new TabletCatalogItem
            {
                CardId = card.CardId,
                DisplayName = card.DisplayName,
                GroupName = card.GroupName,
                SourceOrder = card.SourceOrder,
                // 同名时显示分组与编号：两个「现场照片」必须分得开
                Label = duplicated.Contains(card.DisplayName)
                    ? $"{card.DisplayName}（{card.GroupName} · {card.SourceOrder}）"
                    : card.DisplayName,
                IsAvailable = card.PreparationState == TabletCardPreparationState.Ready
                              && GroupIsReady(manifest, card.GroupName),
                NotReadyReason = card.PreparationState == TabletCardPreparationState.Ready
                    ? manifest.Groups.TryGetValue(card.GroupName, out var group) ? group.Reason : null
                    : card.NotReadyReason,
                HasBack = card.BackAssetId is not null,
            })
            .ToArray();
    }

    /// <summary>按名称找卡：包含匹配，全部返回。同名卡有多少张就返回多少张。</summary>
    public static IReadOnlyList<TabletCard> Search(TabletManifest manifest, string text)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return Ordered(manifest)
            .Where(card => card.DisplayName.Contains(text.Trim(), StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>名称完全相同的卡。找不到返回空，绝不退回「第一张差不多的」。</summary>
    public static IReadOnlyList<TabletCard> FindByName(TabletManifest manifest, string displayName)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Ordered(manifest)
            .Where(card => string.Equals(card.DisplayName, displayName, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// 改显示名。只改名称，<see cref="TabletCard.CardId"/> 与资产引用一律不动：
    /// 正因如此，改名不会让玩家点到别人的卡。
    /// </summary>
    public static TabletManifest Rename(TabletManifest manifest, string cardId, string displayName)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!manifest.Cards.TryGetValue(cardId, out var card))
        {
            throw new InvalidOperationException($"清单里没有卡片 {cardId}。");
        }

        var cards = new Dictionary<string, TabletCard>(manifest.Cards, StringComparer.Ordinal)
        {
            [cardId] = card with { DisplayName = displayName.Trim() },
        };

        return manifest with { Cards = cards };
    }

    /// <summary>换一份资产索引（准备完成时写入）。卡片与组状态不动。</summary>
    public static TabletManifest AttachAssets(
        TabletManifest manifest,
        IReadOnlyDictionary<string, TabletAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest with { Assets = assets };
    }

    /// <summary>
    /// 取图：按 <paramref name="cardId"/> 解析到一张资产。
    ///
    /// 失败原因分开报，网页据此给 404 / 409 并显示可理解的提示，不给异常堆栈，
    /// 也不给整页图当替代。
    /// </summary>
    public static TabletCardLookup TryResolve(
        TabletManifest manifest,
        string cardId,
        TabletCardSide side = TabletCardSide.Front)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.Cards.TryGetValue(cardId, out var card))
        {
            // 不按名称、不按大小写、不做模糊匹配
            return TabletCardLookup.Fail(TabletQueryFailure.NotFound, null);
        }

        if (side == TabletCardSide.Back && card.BackAssetId is null)
        {
            return TabletCardLookup.Fail(TabletQueryFailure.NoBackSide, card);
        }

        if (!GroupIsReady(manifest, card.GroupName))
        {
            var reason = manifest.Groups.TryGetValue(card.GroupName, out var group) ? group.Reason : null;
            return TabletCardLookup.Fail(TabletQueryFailure.GroupNotReady, card, reason);
        }

        if (card.PreparationState != TabletCardPreparationState.Ready)
        {
            return TabletCardLookup.Fail(TabletQueryFailure.GroupNotReady, card, card.NotReadyReason);
        }

        var assetId = side == TabletCardSide.Back ? card.BackAssetId! : card.FrontAssetId;
        if (!manifest.Assets.TryGetValue(assetId, out var asset))
        {
            return TabletCardLookup.Fail(TabletQueryFailure.AssetMissing, card, TabletNotReadyReason.AssetMissing);
        }

        return new TabletCardLookup
        {
            IsAvailable = true,
            Card = card,
            AssetId = asset.AssetId,
            RelativePath = asset.RelativePath,
            ContentHash = asset.ContentHash,
        };
    }

    private static bool GroupIsReady(TabletManifest manifest, string groupName) =>
        manifest.Groups.TryGetValue(groupName, out var group) && group.IsReady;

    /// <summary>
    /// 分组顺序按整组第一次出现的来源序号排，组内按来源序号排。
    /// 不能把「组内最小序号」当每个成员的前缀：那样两个组会交替着排，
    /// 目录里会出现 A1、B1、A2、B2，玩家按名称找卡时很难一眼看出分组。
    /// </summary>
    private static IReadOnlyList<TabletCard> Ordered(TabletManifest manifest)
    {
        var ranked = GroupOrder(manifest);
        return manifest.Cards.Values
            .OrderBy(card => ranked.TryGetValue(card.GroupName, out var rank) ? rank : int.MaxValue)
            .ThenBy(card => card.GroupName, StringComparer.Ordinal)
            .ThenBy(card => card.SourceOrder)
            .ThenBy(card => card.CardId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>每个分组在目录里的位置：按整组最小来源序号从小到大，序号相同再按组名。</summary>
    private static Dictionary<string, int> GroupOrder(TabletManifest manifest)
    {
        var ordered = manifest.Cards.Values
            .GroupBy(card => card.GroupName, StringComparer.Ordinal)
            .Select(group => (Group: group.Key, First: group.Min(card => card.SourceOrder)))
            .OrderBy(pair => pair.First)
            .ThenBy(pair => pair.Group, StringComparer.Ordinal)
            .Select(pair => pair.Group)
            .ToArray();

        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Length; i++)
        {
            ranks[ordered[i]] = i;
        }

        return ranks;
    }

    private static HashSet<string> DuplicatedNames(IEnumerable<TabletCard> cards) =>
        cards
            .GroupBy(card => card.DisplayName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
}

/// <summary>目录里的一行：网页按它列出名称，不拿名称去拼文件路径。</summary>
public sealed record TabletCatalogItem
{
    public required string CardId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>列表上显示的名字：同名卡带分组与编号，例如「现场照片（一楼 · 1）」。</summary>
    public required string Label { get; init; }

    public required string GroupName { get; init; }

    public required int SourceOrder { get; init; }

    public required bool IsAvailable { get; init; }

    public TabletNotReadyReason? NotReadyReason { get; init; }

    /// <summary>有没有卡背：平板 App 据此决定长按翻面，不必请求背面看是不是 404。</summary>
    public bool HasBack { get; init; }
}

public enum TabletCardSide
{
    Front = 0,

    Back = 1,
}

public enum TabletQueryFailure
{
    None = 0,

    /// <summary>清单里没有这个 cardId。</summary>
    NotFound = 1,

    /// <summary>这张卡所在组未准备好：这一组整组暂停分发。</summary>
    GroupNotReady = 2,

    /// <summary>组是好的，但资产索引指不到文件。</summary>
    AssetMissing = 3,

    /// <summary>这张卡没有卡背。</summary>
    NoBackSide = 4,
}

/// <summary>一次取图的结果：可用时给出资产 ID 与包内相对路径，不可用时给出脱敏原因。</summary>
public sealed record TabletCardLookup
{
    public required bool IsAvailable { get; init; }

    public TabletCard? Card { get; init; }

    public string? AssetId { get; init; }

    /// <summary>包内相对路径（<c>/</c> 分隔），不是物理路径。</summary>
    public string? RelativePath { get; init; }

    public string? ContentHash { get; init; }

    public TabletQueryFailure Failure { get; init; }

    public TabletNotReadyReason? Reason { get; init; }

    public static TabletCardLookup Fail(TabletQueryFailure failure, TabletCard? card, TabletNotReadyReason? reason = null) =>
        new() { IsAvailable = false, Card = card, Failure = failure, Reason = reason };
}
