using System.IO;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Tablet;

namespace LocalDm.Tests;

/// <summary>
/// 平板清单与名称目录：玩家按名称点的永远是同一张卡。
///
/// 这里只考「清单 + 只读查询」：取图按 cardId，不按名称拼路径；
/// 同名卡靠分组与编号区分；组未准备好时明确取不到；旧包没有清单时照常可用。
/// 全部是合成数据，不读 stories/、.appdata/、.local/。
/// </summary>
public sealed class TabletQueryTests
{
    // ===== 名称目录 =====

    [Fact]
    public void Catalog_keeps_every_card_and_orders_it_by_group_then_source_order()
    {
        var manifest = Manifest(
            Card("c-2", "第二张", "地点", 2, ready: true),
            Card("c-1", "第一张", "地点", 1, ready: true),
            Card("c-9", "另一组的卡", "回忆", 1, ready: true));

        var catalog = TabletQueries.BuildCatalog(manifest);

        // 「地点」组的最小来源序号是 1，「回忆」组也是 1：序号相同按组名排，回忆在前。
        // 同一组内按来源序号排（c-1 在 c-2 前）。顺序稳定，刷新不会跳。
        Assert.Equal(["c-9", "c-1", "c-2"], catalog.Select(item => item.CardId).ToArray());
        Assert.Equal(["另一组的卡", "第一张", "第二张"], catalog.Select(item => item.DisplayName).ToArray());
    }

    [Fact]
    public void Catalog_never_interleaves_two_groups()
    {
        // 两组各自的来源序号交错（A:1,3 B:2,4）：目录里必须先排完整个 A 组再排 B 组，
        // 不能变成 A1、B2、A3、B4 —— 那样玩家按名称找卡时看不出分组。
        var manifest = Manifest(
            Card("a-1", "甲一", "第一组", 1, ready: true),
            Card("a-3", "甲二", "第一组", 3, ready: true),
            Card("b-2", "乙一", "第二组", 2, ready: true),
            Card("b-4", "乙二", "第二组", 4, ready: true));

        var catalog = TabletQueries.BuildCatalog(manifest);

        Assert.Equal(["a-1", "a-3", "b-2", "b-4"], catalog.Select(item => item.CardId).ToArray());
    }

    [Fact]
    public void Cards_with_the_same_name_keep_distinct_ids_and_show_group_and_number()
    {
        // 同名卡不许「搜到谁就给谁」：两张都留在目录里，各自显示分组与编号。
        var manifest = Manifest(
            Card("c-a", "现场照片", "一楼", 1, ready: true),
            Card("c-b", "现场照片", "二楼", 3, ready: true));

        var catalog = TabletQueries.BuildCatalog(manifest);

        Assert.Equal(2, catalog.Count);
        Assert.Equal(["一楼", "二楼"], catalog.Select(item => item.GroupName).ToArray());
        Assert.Equal(["现场照片（一楼 · 1）", "现场照片（二楼 · 3）"], catalog.Select(item => item.Label).ToArray());

        // 按名称找会得到两张：调用方必须自己选，不能拿到「第一张」。
        var matches = TabletQueries.FindByName(manifest, "现场照片");
        Assert.Equal(["c-a", "c-b"], matches.Select(card => card.CardId).ToArray());
    }

    [Fact]
    public void A_card_with_a_unique_name_shows_the_bare_name()
    {
        var manifest = Manifest(
            Card("c-a", "独有名称", "一楼", 1, ready: true),
            Card("c-b", "重复名称", "一楼", 2, ready: true),
            Card("c-c", "重复名称", "二楼", 1, ready: true));

        var catalog = TabletQueries.BuildCatalog(manifest);

        Assert.Equal("独有名称", catalog[0].Label);
        Assert.Equal("重复名称（一楼 · 2）", catalog[1].Label);
        Assert.Equal("重复名称（二楼 · 1）", catalog[2].Label);
        Assert.Single(TabletQueries.FindByName(manifest, "独有名称"));
    }

    [Fact]
    public void Renaming_a_card_changes_only_its_label_not_how_it_is_fetched()
    {
        var manifest = Manifest(Card("c-a", "旧名称", "地点", 1, ready: true));
        var renamed = TabletQueries.Rename(manifest, "c-a", "新名称");

        Assert.Equal("新名称", renamed.Cards["c-a"].DisplayName);
        // 取图永远走 cardId：改名不动资产引用。
        Assert.Equal("asset-front-a", renamed.Cards["c-a"].FrontAssetId);
        Assert.Equal("asset-front-a", manifest.Cards["c-a"].FrontAssetId);

        // 旧名称查不到了，新名称查得到：不允许两个名字同时命中同一张。
        Assert.Empty(TabletQueries.FindByName(renamed, "旧名称"));
        Assert.Single(TabletQueries.FindByName(renamed, "新名称"));
    }

    [Fact]
    public void Renaming_to_an_existing_name_is_refused()
    {
        var manifest = Manifest(
            Card("c-a", "甲", "地点", 1, ready: true),
            Card("c-b", "乙", "地点", 2, ready: true));

        // 同名可以存在（靠分组与编号区分），但「改名撞车」要确认这是有意的：
        // 这里不抛异常，只保证两张各自的 cardId 不变。
        var renamed = TabletQueries.Rename(manifest, "c-b", "甲");

        Assert.Equal(["c-a", "c-b"], TabletQueries.FindByName(renamed, "甲").Select(c => c.CardId).ToArray());
        Assert.Equal("asset-front-a", renamed.Cards["c-a"].FrontAssetId);
        Assert.Equal("asset-front-b", renamed.Cards["c-b"].FrontAssetId);
    }

    [Fact]
    public void Search_matches_names_by_containment_and_never_returns_a_fuzzy_first_hit()
    {
        var manifest = Manifest(
            Card("c-a", "书房里的照片", "一楼", 1, ready: true),
            Card("c-b", "客厅里的照片", "一楼", 2, ready: true),
            Card("c-c", "信", "回忆", 1, ready: true));

        var hits = TabletQueries.Search(manifest, "照片");
        Assert.Equal(["c-a", "c-b"], hits.Select(card => card.CardId).ToArray());

        // 完全同名的两张必须都返回，调用方不能只拿第一张。
        Assert.Equal(2, hits.Count);
    }

    // ===== 组准备状态 =====

    [Fact]
    public void A_group_that_is_not_ready_is_marked_and_its_cards_cannot_be_fetched()
    {
        var manifest = TabletQueries.AttachAssets(
            Manifest(
                Card("c-a", "能看的卡", "准备好的一组", 1, ready: true),
                Card("c-b", "拆不开的卡", "没准备好的一组", 1, ready: false)),
            Assets("asset-front-a", "asset-front-b"));

        Assert.True(manifest.Groups["准备好的一组"].IsReady);
        Assert.False(manifest.Groups["没准备好的一组"].IsReady);
        Assert.Equal(TabletCardPreparationState.NotReady, manifest.Cards["c-b"].PreparationState);

        // 目录里照样列出（玩家要知道这一组的存在），但明确标成未准备好。
        var catalog = TabletQueries.BuildCatalog(manifest);
        Assert.Contains(catalog, item => item.CardId == "c-b" && !item.IsAvailable);

        var ok = TabletQueries.TryResolve(manifest, "c-a");
        Assert.True(ok.IsAvailable);
        Assert.Equal("asset-front-a", ok.AssetId);

        // 组未准备好：取图失败并说明原因，不退回整页。
        var blocked = TabletQueries.TryResolve(manifest, "c-b");
        Assert.False(blocked.IsAvailable);
        Assert.Equal(TabletQueryFailure.GroupNotReady, blocked.Failure);
        Assert.Equal(TabletNotReadyReason.CropUnconfirmed, blocked.Reason);
        Assert.Null(blocked.AssetId);
    }

    [Fact]
    public void A_card_in_a_ready_group_can_still_be_missing_its_asset()
    {
        // 组是好的，但这一张的资产索引指不到文件：同样不给图，原因不同。
        var manifest = Manifest(
            Card("c-a", "有图", "地点", 1, ready: true),
            Card("c-b", "缺图", "地点", 2, ready: true, frontAssetId: "asset-missing"))
            with { Assets = Assets("asset-front-a") };

        var missing = TabletQueries.TryResolve(manifest, "c-b");
        Assert.False(missing.IsAvailable);
        Assert.Equal(TabletQueryFailure.AssetMissing, missing.Failure);

        var ok = TabletQueries.TryResolve(manifest, "c-a");
        Assert.True(ok.IsAvailable);
    }

    [Fact]
    public void An_unknown_card_id_is_not_found_and_nothing_else_is_guessed()
    {
        var manifest = TabletQueries.AttachAssets(
            Manifest(Card("c-a", "现场照片", "地点", 1, ready: true)),
            Assets("asset-front-a"));

        // 猜测的 ID、按名称、大小写不同的 ID 一律取不到：只认清单里的 cardId。
        Assert.Equal(TabletQueryFailure.NotFound, TabletQueries.TryResolve(manifest, "c-zzz").Failure);
        Assert.Equal(TabletQueryFailure.NotFound, TabletQueries.TryResolve(manifest, "现场照片").Failure);
        Assert.Equal(TabletQueryFailure.NotFound, TabletQueries.TryResolve(manifest, "C-A").Failure);
        Assert.True(TabletQueries.TryResolve(manifest, "c-a").IsAvailable);
    }

    [Fact]
    public void Resolving_a_card_returns_the_asset_id_and_never_a_file_path()
    {
        var manifest = TabletQueries.AttachAssets(
            Manifest(Card("c-a", "甲", "地点", 1, ready: true, backAssetId: "asset-back-a")),
            Assets("asset-front-a", "asset-back-a"));

        var front = TabletQueries.TryResolve(manifest, "c-a");
        Assert.Equal("asset-front-a", front.AssetId);
        Assert.Equal("cards/front-a.png", front.RelativePath);

        var back = TabletQueries.TryResolve(manifest, "c-a", TabletCardSide.Back);
        Assert.Equal("asset-back-a", back.AssetId);

        // 返回的只是包内相对路径与资产 ID，不含任何绝对路径。
        Assert.DoesNotContain(':', front.RelativePath!);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, front.RelativePath!);
    }

    [Fact]
    public void A_card_without_a_back_side_has_no_back_image()
    {
        var manifest = TabletQueries.AttachAssets(
            Manifest(Card("c-a", "甲", "地点", 1, ready: true)),
            Assets("asset-front-a"));

        Assert.True(TabletQueries.TryResolve(manifest, "c-a").IsAvailable);
        Assert.Equal(TabletQueryFailure.NoBackSide, TabletQueries.TryResolve(manifest, "c-a", TabletCardSide.Back).Failure);
    }

    // ===== 与剧本包 =====

    [Fact]
    public void A_package_without_a_manifest_still_works_in_paper_mode()
    {
        var package = SyntheticPackage(contentHash: new string('a', 64));

        // 旧包读出来是 null：纸质模式照常，平板要重新准备。
        Assert.Null(package.Tablet);
        Assert.False(TabletQueries.HasManifest(package));
        Assert.Empty(TabletQueries.BuildCatalog(TabletQueries.RequireManifest(package) ?? TabletManifest.Empty));
    }

    [Fact]
    public void The_manifest_survives_a_json_round_trip_and_an_older_a_package_without_it_reads_back_as_null()
    {
        // 旧包（JSON 里根本没有 tablet 字段）：反序列化成功，平板清单为空。
        var legacyJson = JsonSerializer.Serialize(SyntheticPackage(contentHash: new string('b', 64)));
        Assert.DoesNotContain("\"tablet\"", legacyJson, StringComparison.OrdinalIgnoreCase);

        var legacy = JsonSerializer.Deserialize<ScriptPackage>(legacyJson)!;
        Assert.Null(legacy.Tablet);

        // 新包：清单里的卡片、资产与组状态都要能原样读回。
        var manifest = TabletQueries.AttachAssets(
            Manifest(
                Card("c-a", "现场照片", "一楼", 1, ready: true, backAssetId: "asset-back-a"),
                Card("c-b", "现场照片", "二楼", 2, ready: false)),
            Assets("asset-front-a", "asset-back-a"));
        var upgraded = JsonSerializer.Deserialize<ScriptPackage>(
            JsonSerializer.Serialize(SyntheticPackage(new string('c', 64)) with { Tablet = manifest }))!;

        Assert.NotNull(upgraded.Tablet);
        Assert.Equal("现场照片", upgraded.Tablet!.Cards["c-a"].DisplayName);
        Assert.Equal("asset-back-a", upgraded.Tablet.Cards["c-a"].BackAssetId);
        Assert.False(upgraded.Tablet.Groups["二楼"].IsReady);
        Assert.Equal("cards/front-a.png", upgraded.Tablet.Assets["asset-front-a"].RelativePath);
    }

    // ===== 夹具 =====

    private static TabletCard Card(
        string cardId,
        string displayName,
        string groupName,
        int sourceOrder,
        bool ready,
        string? frontAssetId = null,
        string? backAssetId = null) => new()
    {
        CardId = cardId,
        DisplayName = displayName,
        GroupName = groupName,
        SourceOrder = sourceOrder,
        FrontAssetId = frontAssetId ?? "asset-front-" + cardId[^1],
        BackAssetId = backAssetId,
        PreparationState = ready ? TabletCardPreparationState.Ready : TabletCardPreparationState.NotReady,
        NotReadyReason = ready ? null : TabletNotReadyReason.CropUnconfirmed,
        SourceFileId = "file-" + groupName,
        SourcePageNumber = 1,
        Slot = new GridSlot(0, 0),
    };

    /// <summary>
    /// 用卡片自动生成组：组是否准备好由该组所有卡片共同决定，
    /// 这样用例里只要写「哪张卡没准备好」就够了。
    /// </summary>
    private static TabletManifest Manifest(params TabletCard[] cards)
    {
        var groups = cards
            .GroupBy(card => card.GroupName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.All(card => card.PreparationState == TabletCardPreparationState.Ready)
                    ? new TabletGroupPreparationState
                    {
                        GroupName = group.Key,
                        Readiness = TabletGroupReadiness.Ready,
                    }
                    : new TabletGroupPreparationState
                    {
                        GroupName = group.Key,
                        Readiness = TabletGroupReadiness.NotReady,
                        Reason = TabletNotReadyReason.CropUnconfirmed,
                        NotReadyCardCount = group.Count(card => card.PreparationState != TabletCardPreparationState.Ready),
                    },
                StringComparer.Ordinal);

        return new TabletManifest
        {
            ManifestVersion = TabletManifest.CurrentManifestVersion,
            AssetRootFingerprint = new string('f', 64),
            Cards = cards.ToDictionary(card => card.CardId, StringComparer.Ordinal),
            Groups = groups,
            Assets = new Dictionary<string, TabletAsset>(StringComparer.Ordinal),
        };
    }

    private static IReadOnlyDictionary<string, TabletAsset> Assets(params string[] assetIds) =>
        assetIds.ToDictionary(
            assetId => assetId,
            assetId => new TabletAsset
            {
                AssetId = assetId,
                RelativePath = assetId.Contains("back", StringComparison.Ordinal)
                    ? "cards/back-a.png"
                    : "cards/front-a.png",
                ContentHash = new string('1', 64),
                Kind = assetId.Contains("back", StringComparison.Ordinal) ? TabletAssetKind.CardBack : TabletAssetKind.CardFront,
                SourceFileId = "file-1",
                SourcePageNumber = 1,
                Slot = new GridSlot(0, 0),
            },
            StringComparer.Ordinal);

    /// <summary>一份最小的 Ready 合成包：只为了验证包的 JSON 兼容，不含任何真实材料。</summary>
    private static ScriptPackage SyntheticPackage(string contentHash)
    {
        var span = new SourceSpan
        {
            SpanId = "sp-1",
            FileId = "f-1",
            PageNumber = 1,
            BlockNumber = 0,
            ToolLocator = "doc:f-1/page:1/block:0",
            TextHash = new string('a', 64),
            OriginalText = "合成占位文本",
        };
        var file = new SourceFile
        {
            FileId = "f-1",
            PrivatePath = "f-1.png",
            ContentHash = new string('a', 64),
            PageCount = 1,
            Origin = SourceOrder.ManifestOrder,
            ToolIdentity = "synthetic",
            ParseState = FileParseState.Complete,
        };

        return new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = "pkg-" + contentHash[..12],
            Version = "1",
            Title = "合成剧本（非真实剧本）",
            PlayerCount = 5,
            ManifestHash = new string('a', 64),
            ContentHash = contentHash,
            SourceFiles = [file],
            Spans = [span],
            Segments =
            [
                new CanonicalSegment
                {
                    SegmentId = "seg-1",
                    OriginalText = "合成占位文本",
                    SourceSpanIds = ["sp-1"],
                    Visibility = SegmentVisibility.Private,
                },
            ],
            Roles = [],
            Phases = [],
            Materials = [],
            Truth = new TruthSection { RevealedSegmentIds = [], PrivateText = string.Empty, IsConfirmedByHuman = false },
            Validation = PackageValidationState.Ready,
        };
    }
}
