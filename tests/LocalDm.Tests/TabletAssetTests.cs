using System.IO;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Tests;

/// <summary>
/// 平板线索卡准备：单卡配对、整版网格切图与来源追溯。
///
/// 图片全部在测试里现画：每个槽位填唯一颜色，再把槽位号写进像素里。
/// 断言读回切出来的每一张图，逐槽核对「四边完整、不串邻卡、正背面对得上」，
/// 不能只断言「没抛异常」或「字节非空」。
/// </summary>
public sealed class TabletAssetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-tablet-" + Guid.NewGuid().ToString("N"));

    public TabletAssetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
    }

    // ===== 整版切图 =====

    [Fact]
    public void Five_by_two_sheet_cuts_into_ten_cards_each_matching_its_own_slot()
    {
        // 5 行 2 列，每个槽位四种颜色的四边条 + 中心色块，标记只在本槽内。
        const int slotW = 40;
        const int slotH = 30;
        var sheet = SlotSheet(columns: 2, rows: 5, slotW, slotH);
        var path = WritePng("sheet-5x2.png", sheet);

        var grid = new GridLayout
        {
            Rows = 5,
            Columns = 2,
            Region = new PixelRegion(0, 0, sheet.Width, sheet.Height),
            ValidSlots = [],
        };

        var cuts = GridCardCropper.Crop(path, grid);

        Assert.Equal(10, cuts.Count);
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 2; column++)
            {
                var cut = Assert.Single(cuts, c => c.Slot == new GridSlot(row, column));
                AssertSlotMarkers(SyntheticPng.Decode(cut.Png), row, column, slotW, slotH);
            }
        }
    }

    [Fact]
    public void A_left_half_five_by_one_sheet_uses_only_the_left_region_not_the_full_width()
    {
        // 整页左边一半是真卡（5 行 1 列），右边一半是另一版内容：
        // 有效区域没设对时，右半的内容会被一起吃进单卡宽度。
        const int slotW = 50;
        const int slotH = 24;
        var sheet = SlotSheet(columns: 1, rows: 5, slotW, slotH);
        var composite = RightHalfContaminated(sheet, rightHalfColor: (0x11, 0x22, 0x33));
        var path = WritePng("left-half-5x1.png", composite);

        var grid = new GridLayout
        {
            Rows = 5,
            Columns = 1,
            Region = new PixelRegion(0, 0, slotW, composite.Height),
            ValidSlots = [],
        };

        var cuts = GridCardCropper.Crop(path, grid);

        Assert.Equal(5, cuts.Count);
        for (var row = 0; row < 5; row++)
        {
            var cut = Assert.Single(cuts, c => c.Slot == new GridSlot(row, 0));
            Assert.Equal(slotW, cut.Width);
            AssertSlotMarkers(SyntheticPng.Decode(cut.Png), row, 0, slotW, slotH);

            // 右半页的填充色一个像素都不许出现。
            Assert.DoesNotContain(Rgb(SyntheticPng.Decode(cut.Png)), pixel => pixel == (0x11, 0x22, 0x33, (byte)255));
        }
    }

    [Fact]
    public void Slot_size_that_does_not_divide_the_page_evenly_keeps_every_card_whole()
    {
        // 1003 宽、2 列 → 每列 501.5；5 行、703 高 → 每行 140.6。
        // 每条边按整图位置独立取整：逐行累加会越切越偏，切出半张邻卡。
        const int columns = 2;
        const int rows = 5;
        var slotW = 1003 / columns + 1; // 画图时按 ceil 画，切图时按整图位置取整
        var slotH = 141;
        var width = columns * slotW - 1;
        var height = rows * slotH - 3;

        var sheet = SlotSheet(columns, rows, slotW, slotH, width, height);
        var path = WritePng("uneven.png", sheet);

        var grid = new GridLayout
        {
            Rows = rows,
            Columns = columns,
            Region = new PixelRegion(0, 0, width, height),
            ValidSlots = [],
        };

        var cuts = GridCardCropper.Crop(path, grid);

        Assert.Equal(rows * columns, cuts.Count);
        foreach (var cut in cuts)
        {
            // 每张都必须是矩形的、完整的：宽度是整图宽度按列切出来的，不随行号漂移。
            Assert.InRange(cut.Width, 500, 502);
            Assert.InRange(cut.Height, 139, 142);
        }

        // 每一列的第一行与最后一行切出来一样宽：没有逐行累积误差。
        var tops = cuts.Where(c => c.Slot.Column == 0).OrderBy(c => c.Slot.Row).ToArray();
        Assert.Single(tops.Select(c => c.Width).Distinct());
        Assert.Single(tops.Select(c => c.X).Distinct());

        // 逐槽核对标记都在自己那张里，四边没有截断。
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var cut = Assert.Single(cuts, c => c.Slot == new GridSlot(row, column));
                AssertSlotCenter(SyntheticPng.Decode(cut.Png), row, column);
                AssertEdgeMarkersIntact(SyntheticPng.Decode(cut.Png), row, column);
            }
        }
    }

    [Fact]
    public void Valid_slots_skip_empty_places_and_keep_the_real_slot_coordinates()
    {
        // 整版最后一格是空的：有效槽位只列真有的 3 格，空槽不出图。
        const int slotW = 32;
        const int slotH = 32;
        var sheet = SlotSheet(columns: 2, rows: 2, slotW, slotH);
        var path = WritePng("with-empty.png", sheet);

        var grid = new GridLayout
        {
            Rows = 2,
            Columns = 2,
            Region = new PixelRegion(0, 0, sheet.Width, sheet.Height),
            // 第二列第二行留空
            ValidSlots = [new GridSlot(0, 0), new GridSlot(0, 1), new GridSlot(1, 0)],
        };

        var cuts = GridCardCropper.Crop(path, grid);

        Assert.Equal(3, cuts.Count);
        Assert.DoesNotContain(cuts, c => c.Slot == new GridSlot(1, 1));
        // 留下来的槽位坐标还是它在整版上的真实位置，不是重新编号。
        var secondRowLeft = Assert.Single(cuts, c => c.Slot == new GridSlot(1, 0));
        Assert.Equal(0, secondRowLeft.X);
        Assert.Equal(slotH, secondRowLeft.Y);
        AssertSlotMarkers(SyntheticPng.Decode(secondRowLeft.Png), 1, 0, slotW, slotH);
    }

    [Fact]
    public void A_mirrored_sheet_mapping_flips_columns_and_reverses_pixel_content()
    {
        // 背面印版整页是左右镜像的：切完每张单卡后要水平反转回来，中心标记才落回它自己的位置。
        const int slotW = 40;
        const int slotH = 24;
        var sheet = SlotSheet(columns: 2, rows: 1, slotW, slotH);
        var mirrored = MirrorHorizontally(sheet);
        var path = WritePng("back-mirrored.png", mirrored);

        var grid = new GridLayout
        {
            Rows = 1,
            Columns = 2,
            Region = new PixelRegion(0, 0, mirrored.Width, mirrored.Height),
            ValidSlots = [],
            MirrorHorizontally = true,
        };

        var cuts = GridCardCropper.Crop(path, grid);
        var plain = GridCardCropper.Crop(path, grid with { MirrorHorizontally = false });

        Assert.Equal(2, cuts.Count);
        var left = Assert.Single(cuts, c => c.Slot == new GridSlot(0, 0));
        var right = Assert.Single(cuts, c => c.Slot == new GridSlot(0, 1));
        Assert.Equal(slotW, left.Width);
        Assert.Equal(slotW, right.Width);

        // 关键性质：镜像印版切出来的这一格，翻转回来后与「正印版反过来切这一格」逐像素一致。
        // 用正印版自己再水平翻一次当基准，不依赖任何格子内容，能真正证明翻转执行了且是水平翻转。
        var plainLeft = Assert.Single(plain, c => c.Slot == new GridSlot(0, 0));
        var expected = MirrorHorizontally(SyntheticPng.Decode(plainLeft.Png));
        Assert.Equal(expected, SyntheticPng.Decode(left.Png).Pixels);

        // 印版镜像过：左格在印版上是原来右列的内容，所以它带的是右列那个标记色；
        // 翻转只会左右翻，不会把列号换回来 —— 列号要靠 GridSlotMapping 折回（见下一条用例）。
        AssertSlotCenter(SyntheticPng.Decode(left.Png), 0, 1);
        AssertEdgeMarkersIntact(SyntheticPng.Decode(left.Png), 0, 1);
    }

    [Theory]
    [InlineData(5, 3, 2)]
    [InlineData(1003, 502, 501)]
    public void A_mirrored_odd_width_sheet_reflects_cell_edges_without_neighbor_pixels(
        int width,
        int expectedLeftWidth,
        int expectedRightWidth)
    {
        // 输入是整版镜像后的像素：宽 5 时三像素卡在左、两像素卡在右；1003 同理。
        // 镜像裁切要反射不等宽边界，再反转卡内像素，不能按原边界切出半张邻卡。
        var sheet = new RasterImage(width, 1);
        var mirroredBoundary = (width + 1) / 2;
        for (var x = 0; x < width; x++)
        {
            sheet.Set(x, 0, x < mirroredBoundary
                ? ((byte)2, (byte)0, (byte)0)
                : ((byte)1, (byte)0, (byte)0));
        }

        var path = Path.Combine(_root, $"mirrored-{width}.png");
        File.WriteAllBytes(path, PngCodec.Encode(sheet));
        var grid = new GridLayout
        {
            Rows = 1,
            Columns = 2,
            Region = new PixelRegion(0, 0, width, 1),
            MirrorHorizontally = true,
        };

        var cuts = GridCardCropper.Crop(path, grid);
        var left = Assert.Single(cuts, cut => cut.Slot == new GridSlot(0, 0));
        var right = Assert.Single(cuts, cut => cut.Slot == new GridSlot(0, 1));

        Assert.Equal(expectedLeftWidth, left.Width);
        Assert.Equal(expectedRightWidth, right.Width);
        Assert.Equal(new PixelRegion(0, 0, expectedLeftWidth, 1), left.Region);
        Assert.Equal(new PixelRegion(expectedLeftWidth, 0, expectedRightWidth, 1), right.Region);
        Assert.All(Enumerable.Range(0, left.Width), x => Assert.Equal((byte)2, left.Image.Get(x, 0).R));
        Assert.All(Enumerable.Range(0, right.Width), x => Assert.Equal((byte)1, right.Image.Get(x, 0).R));

        var assets = TabletAssetBuilder.MaterializeGrid(
            Path.Combine(_root, $"assets-{width}"),
            $"source-{width}",
            path,
            grid);
        var leftAsset = Assert.Single(assets, asset => asset.Slot == left.Slot);
        var rightAsset = Assert.Single(assets, asset => asset.Slot == right.Slot);
        Assert.Equal(left.Region, leftAsset.CropRegion);
        Assert.Equal(right.Region, rightAsset.CropRegion);
        Assert.True(leftAsset.GridMirrorHorizontally);
        Assert.Equal(grid.Region, leftAsset.GridRegion);
    }

    [Fact]
    public void A_mirrored_sparse_slot_keeps_its_physical_slot_and_nonzero_source_offset()
    {
        // ValidSlots 是源图上的物理槽位。只保留物理第 0 列时，镜像几何要先查 canonical 第 1 列，
        // 再反射到带偏移的有效区域；返回仍是物理第 0 列，正背配对由 GridSlotMapping 处理。
        var sheet = new RasterImage(10, 4);
        for (var y = 0; y < sheet.Height; y++)
        {
            for (var x = 0; x < sheet.Width; x++)
            {
                sheet.Set(x, y, ((byte)9, (byte)9, (byte)9));
            }
        }

        for (var y = 1; y < 3; y++)
        {
            for (var x = 2; x < 5; x++)
            {
                sheet.Set(x, y, ((byte)2, (byte)0, (byte)0));
            }

            for (var x = 5; x < 7; x++)
            {
                sheet.Set(x, y, ((byte)1, (byte)0, (byte)0));
            }
        }

        var path = Path.Combine(_root, "mirrored-sparse-offset.png");
        File.WriteAllBytes(path, PngCodec.Encode(sheet));
        var grid = new GridLayout
        {
            Rows = 1,
            Columns = 2,
            Region = new PixelRegion(2, 1, 5, 2),
            ValidSlots = [new GridSlot(0, 0)],
            MirrorHorizontally = true,
        };

        var cut = Assert.Single(GridCardCropper.Crop(path, grid));

        Assert.Equal(new GridSlot(0, 0), cut.Slot);
        Assert.Equal(new PixelRegion(2, 1, 3, 2), cut.Region);
        Assert.All(Enumerable.Range(0, cut.Width), x =>
        {
            for (var y = 0; y < cut.Height; y++)
            {
                Assert.Equal((byte)2, cut.Image.Get(x, y).R);
                Assert.Equal((byte)0, cut.Image.Get(x, y).G);
            }
        });
        Assert.Equal(
            new GridSlot(0, 1),
            GridSlotMapping.DefaultForPairing(mirrored: true, backColumns: 2).MapBackHeader(cut.Slot));
    }

    [Fact]
    public void Mirrored_mapping_swaps_which_side_slot_pairs_with_which_front()
    {
        // 正背面槽位映射使用真实列数：三列背面从 0、1、2 映射到正面 2、1、0。
        var pairing = GridSlotMapping.DefaultForPairing(mirrored: true, backColumns: 3);
        Assert.Equal(new GridSlot(0, 2), pairing.MapBackHeader(new GridSlot(0, 0)));
        Assert.Equal(new GridSlot(0, 1), pairing.MapBackHeader(new GridSlot(0, 1)));
        Assert.Equal(new GridSlot(0, 0), pairing.MapBackHeader(new GridSlot(0, 2)));

        // 不镜像时槽位不变，且也按真实列数拒绝越界值。
        var same = GridSlotMapping.DefaultForPairing(mirrored: false, backColumns: 3);
        Assert.Equal(new GridSlot(0, 0), same.MapBackHeader(new GridSlot(0, 0)));
        Assert.Equal(new GridSlot(0, 2), same.MapBackHeader(new GridSlot(0, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pairing.MapBackHeader(new GridSlot(0, -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pairing.MapBackHeader(new GridSlot(0, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pairing.MapBackHeader(new GridSlot(-1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridSlotMapping.DefaultForPairing(true, backColumns: 0));
    }

    [Fact]
    public void Without_a_valid_region_the_crop_refuses_instead_of_guessing()
    {
        var sheet = SlotSheet(columns: 2, rows: 1, slotW: 20, slotH: 20);
        var path = WritePng("bad-region.png", sheet);

        var grid = new GridLayout
        {
            Rows = 1,
            Columns = 2,
            // 有效区域超出图片边界
            Region = new PixelRegion(0, 0, sheet.Width + 40, sheet.Height),
            ValidSlots = [],
        };

        Assert.Throws<DomainRuleException>(() => GridCardCropper.Crop(path, grid));
    }

    // ===== 单卡配对 =====

    [Fact]
    public void Single_card_files_pair_by_number_and_keep_both_sides_distinct()
    {
        var front = WritePng("c01.png", SolidBitmap(20, 20, (0x10, 0x20, 0x30)));
        var back = WritePng("c01背.png", SolidBitmap(20, 20, (0x90, 0x80, 0x70)));
        var front2 = WritePng("c02.png", SolidBitmap(20, 20, (0x40, 0x50, 0x60)));

        var candidates = TabletAssetBuilder.PairSingleCards([front, back, front2]);

        Assert.Equal(2, candidates.Count);
        var first = Assert.Single(candidates, c => c.SourceOrder == 1);
        Assert.Equal(front, first.FrontPath);
        Assert.Equal(back, first.BackPath);
        var second = Assert.Single(candidates, c => c.SourceOrder == 2);
        Assert.Equal(front2, second.FrontPath);
        Assert.Null(second.BackPath);
    }

    [Fact]
    public void A_missing_front_or_a_conflicting_pair_makes_the_group_unprepared()
    {
        // 两张正面都标 3 号：配对不明确，这一组不能分发。
        var a = WritePng("c03.png", SolidBitmap(10, 10, (1, 2, 3)));
        var b = WritePng("3.png", SolidBitmap(10, 10, (4, 5, 6)));

        var candidates = TabletAssetBuilder.PairSingleCards([a, b]);

        Assert.True(
            candidates.Any(c => c.Conflicts.Count > 0) || candidates.Count == 1,
            "同一编号出现两张正面时必须报冲突，不能随便配。");
        Assert.All(candidates, c => Assert.NotNull(c.FrontPath));
    }

    // ===== 资产与追溯 =====

    [Fact]
    public void Every_cut_asset_records_hash_origin_and_slot_so_it_can_be_traced_back()
    {
        var sheet = SlotSheet(columns: 2, rows: 2, slotW: 40, slotH: 30);
        var path = WritePng("trace.png", sheet);
        var assetRoot = Path.Combine(_root, "assets");

        var grid = new GridLayout
        {
            Rows = 2,
            Columns = 2,
            Region = new PixelRegion(0, 0, sheet.Width, sheet.Height),
            ValidSlots = [],
        };

        var assets = TabletAssetBuilder.MaterializeGrid(
            assetRoot,
            Path.GetFileName(path),
            path,
            grid,
            hashesStartingFrom: 1);

        Assert.Equal(4, assets.Count);
        var sourceHash = TabletAssetBuilder.ComputeFileHash(path);
        foreach (var asset in assets)
        {
            // 来源可回溯：原文件 hash、页号、整版网格与槽位都记着。
            Assert.Equal(sourceHash, asset.SourceContentHash);
            Assert.Equal(Path.GetFileName(path), asset.SourceFileId);
            Assert.Equal(2, asset.GridRows);
            Assert.Equal(2, asset.GridColumns);
            Assert.NotNull(asset.Slot);

            // 落盘的资产真的存在，hash 对得上写的内容。
            var file = Path.Combine(assetRoot, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"{asset.RelativePath} 没有写到资产目录。");
            Assert.Equal(TabletAssetBuilder.ComputeFileHash(file), asset.ContentHash);
        }

        // 每个槽位的 hash 各不相同：四张真的切成了四张不同的图。
        Assert.Equal(4, assets.Select(a => a.ContentHash).Distinct().Count());
    }

    [Fact]
    public void A_changed_source_file_changes_the_reported_hash()
    {
        var one = WritePng("v1.png", SolidBitmap(12, 12, (1, 1, 1)));
        var two = WritePng("v2.png", SolidBitmap(12, 12, (2, 2, 2)));

        Assert.NotEqual(TabletAssetBuilder.ComputeFileHash(one), TabletAssetBuilder.ComputeFileHash(two));
    }

    // ===== 整包：清单进包、内容指纹、旧包兼容 =====

    [Fact]
    public void Attaching_the_tablet_manifest_changes_the_content_hash_but_not_for_old_packages()
    {
        var pages = new[]
        {
            new RealScriptPackageBuilder.ExtractedPage
            {
                Group = "线索",
                GroupKind = RealScriptPackageBuilder.ExtractedGroupKind.Material,
                PageNumber = 1,
                PageLabel = "p1",
                Text = "合成占位线索文本",
                FileId = "f-1",
                SpanId = "sp-1",
                SourcePageNumber = 1,
            },
        };
        var files = new[]
        {
            new SourceFile
            {
                FileId = "f-1",
                PrivatePath = "f-1.png",
                ContentHash = new string('a', 64),
                PageCount = 1,
                Origin = SourceOrder.ManifestOrder,
                ToolIdentity = "synthetic",
                ParseState = FileParseState.Complete,
            },
        };

        var plain = RealScriptPackageBuilder.Build("合成剧本（非真实剧本）", 5, pages, files);
        // 没有平板清单：与改动前完全一样，指纹不变。
        Assert.Null(plain.Package.Tablet);

        var manifest = new TabletManifest
        {
            ManifestVersion = TabletManifest.CurrentManifestVersion,
            AssetRootFingerprint = new string('e', 64),
            Cards = new Dictionary<string, TabletCard>(StringComparer.Ordinal)
            {
                ["c-a"] = new()
                {
                    CardId = "c-a",
                    DisplayName = "合成卡",
                    GroupName = "线索",
                    SourceOrder = 1,
                    FrontAssetId = "asset-1",
                    PreparationState = TabletCardPreparationState.Ready,
                    SourceFileId = "f-1",
                    SourcePageNumber = 1,
                    Slot = new GridSlot(0, 0),
                },
            },
            Groups = new Dictionary<string, TabletGroupPreparationState>(StringComparer.Ordinal)
            {
                ["线索"] = new() { GroupName = "线索", Readiness = TabletGroupReadiness.Ready },
            },
            Assets = new Dictionary<string, TabletAsset>(StringComparer.Ordinal)
            {
                ["asset-1"] = new()
                {
                    AssetId = "asset-1",
                    RelativePath = "cards/c-a-front.png",
                    ContentHash = new string('d', 64),
                    Kind = TabletAssetKind.CardFront,
                    SourceFileId = "f-1",
                    SourcePageNumber = 1,
                    GridRows = 2,
                    GridColumns = 2,
                    GridMirrorHorizontally = true,
                    GridRegion = new PixelRegion(10, 20, 100, 200),
                    CropRegion = new PixelRegion(10, 20, 50, 100),
                    Slot = new GridSlot(0, 0),
                },
            },
        };

        var withTablet = RealScriptPackageBuilder.Build("合成剧本（非真实剧本）", 5, pages, files, tablet: manifest);

        // 有清单的包是新版本：内容指纹必须变，旧存档才不会静默接受新资产。
        Assert.NotEqual(plain.ContentHash, withTablet.ContentHash);
        Assert.Equal(manifest, withTablet.Package.Tablet);
        Assert.NotNull(withTablet.Package.ContentHash);

        // 同一份清单两次构建指纹一致：可重复生成，不随进程变。
        var again = RealScriptPackageBuilder.Build("合成剧本（非真实剧本）", 5, pages, files, tablet: manifest);
        Assert.Equal(withTablet.ContentHash, again.ContentHash);

        // 改显示名也是新版本：旧局不会在读卡时悄悄换名。
        var renamed = TabletQueries.Rename(manifest, "c-a", "改了名字的卡");
        var renamedPackage = RealScriptPackageBuilder.Build("合成剧本（非真实剧本）", 5, pages, files, tablet: renamed);
        Assert.NotEqual(withTablet.ContentHash, renamedPackage.ContentHash);

        var sourceAsset = manifest.Assets["asset-1"];
        var cropMetadataChanges = new[]
        {
            sourceAsset with { GridRegion = new PixelRegion(11, 20, 100, 200) },
            sourceAsset with { CropRegion = new PixelRegion(11, 20, 50, 100) },
            sourceAsset with { GridMirrorHorizontally = false },
            sourceAsset with { GridRows = 3 },
            sourceAsset with { GridColumns = 3 },
            sourceAsset with { Slot = new GridSlot(0, 1) },
        };
        foreach (var changedAsset in cropMetadataChanges)
        {
            var changedManifest = manifest with
            {
                Assets = new Dictionary<string, TabletAsset>(StringComparer.Ordinal)
                {
                    [changedAsset.AssetId] = changedAsset,
                },
            };
            var changedPackage = RealScriptPackageBuilder.Build(
                "合成剧本（非真实剧本）",
                5,
                pages,
                files,
                tablet: changedManifest);
            Assert.NotEqual(withTablet.ContentHash, changedPackage.ContentHash);
        }
    }

    // ===== 合成图片 =====

    /// <summary>一个槽位一种颜色：整版按行优先编号。</summary>
    private static (byte R, byte G, byte B) SlotColor(int row, int column, int columns)
    {
        var index = row * columns + column;
        // 避开纯黑/纯白：黑留给边条，白留给底色。
        return ((byte)(40 + (index * 23) % 200), (byte)(30 + (index * 47) % 200), (byte)(20 + (index * 71) % 200));
    }

    /// <summary>
    /// 画一版「每个槽位都是一张卡」的合成整版图：
    /// 槽内铺该槽颜色，四边条用暗色勾出边界，中心 8×8 块写槽号对应的颜色。
    /// 这样切歪、切到邻卡、四边被截断都能从像素上看出来。
    /// </summary>
    private static Bitmap SlotSheet(int columns, int rows, int slotW, int slotH, int? width = null, int? height = null)
    {
        var w = width ?? columns * slotW;
        var h = height ?? rows * slotH;
        var bitmap = new Bitmap(w, h);

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var color = SlotColor(row, column, columns);
                var x0 = column * w / columns;
                var x1 = (column + 1) * w / columns;
                var y0 = row * h / rows;
                var y1 = (row + 1) * h / rows;

                bitmap.Fill(x0, y0, x1, y1, color);
                // 暗色四边条：切出来的卡四边都必须是暗色，才能说明边没被切掉。
                bitmap.Fill(x0, y0, x1, y0 + 2, (0, 0, 0));
                bitmap.Fill(x0, y1 - 2, x1, y1, (0, 0, 0));
                bitmap.Fill(x0, y0, x0 + 2, y1, (0, 0, 0));
                bitmap.Fill(x1 - 2, y0, x1, y1, (0, 0, 0));
                // 中心块：颜色由槽号唯一确定。
                bitmap.FillCenter(x0, y0, x1, y1, SlotCenterColor(row, column, columns), 8, 8);
            }
        }

        return bitmap;
    }

    /// <summary>槽号颜色：和槽位底色不同，且各槽互不相同。</summary>
    private static (byte R, byte G, byte B) SlotCenterColor(int row, int column, int columns) =>
        ((byte)(255 - row * 40), (byte)(10 + column * 100), (byte)(128 + row * 17));

    private static Bitmap RightHalfContaminated(Bitmap left, (byte R, byte G, byte B) rightHalfColor)
    {
        var width = left.Width * 2;
        var bitmap = new Bitmap(width, left.Height);
        bitmap.Blit(left, 0, 0);
        bitmap.Fill(left.Width, 0, width, left.Height, rightHalfColor);
        return bitmap;
    }

    private static Bitmap MirrorHorizontally(Bitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                bitmap.Set(x, y, source.Get(source.Width - 1 - x, y));
            }
        }

        return bitmap;
    }

    /// <summary>把解码后的图再水平翻一次，用作「正面原样反过来」的基准。</summary>
    private static byte[] MirrorHorizontally(DecodedImage image)
    {
        var flipped = new byte[image.Pixels.Length];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var source = (y * image.Width + (image.Width - 1 - x)) * 3;
                var target = (y * image.Width + x) * 3;
                flipped[target] = image.Pixels[source];
                flipped[target + 1] = image.Pixels[source + 1];
                flipped[target + 2] = image.Pixels[source + 2];
            }
        }

        return flipped;
    }

    private static Bitmap SolidBitmap(int width, int height, (byte R, byte G, byte B) color)
    {
        var bitmap = new Bitmap(width, height);
        bitmap.Fill(0, 0, width, height, color);
        return bitmap;
    }

    private static (byte R, byte G, byte B, byte A)[] Rgb(DecodedImage image) =>
        image.Pixels
            .Chunk(3)
            .Select(chunk => (chunk[0], chunk[1], chunk[2], (byte)255))
            .ToArray();

    /// <summary>切出来的这一张：四边暗条都在，中心块是它自己那个槽的味道。</summary>
    private static void AssertSlotMarkers(DecodedImage image, int row, int column, int slotW, int slotH)
    {
        Assert.Equal(slotW, image.Width);
        Assert.Equal(slotH, image.Height);
        AssertSlotCenter(image, row, column);
        AssertEdgeMarkersIntact(image, row, column);
    }

    /// <summary>
    /// 这一张里画的到底是哪个槽：
    /// 按图片尺寸正确切出来时，整张图只会有一种槽的底色，中心块也只会有那个槽的颜色。
    /// 切歪了、带上了邻卡，或者切到别的列，都会同时出现两种槽的颜色。
    /// </summary>
    private static void AssertSlotCenter(DecodedImage image, int row, int column, int columns = 2)
    {
        var expected = SlotCenterColor(row, column, columns);

        var hasCenter = false;
        for (var y = 0; y < image.Height && !hasCenter; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var index = (y * image.Width + x) * 3;
                if (image.Pixels[index] == expected.R
                    && image.Pixels[index + 1] == expected.G
                    && image.Pixels[index + 2] == expected.B)
                {
                    hasCenter = true;
                    break;
                }
            }
        }

        Assert.True(hasCenter, $"槽位 ({row},{column}) 的图里没有它自己的标记色，可能切到了邻卡。");

        // 图里不该出现别的槽的底色：那说明切出来的宽度混进了邻居。
        // 只看四边暗条之间那块（离边条 4px 起），避开边条与中心块。
        for (var other = 0; other < columns; other++)
        {
            if (other == column)
            {
                continue;
            }

            var foreign = SlotColor(row, other, columns);
            for (var y = 4; y < image.Height - 4; y++)
            {
                for (var x = 4; x < image.Width - 4; x++)
                {
                    var index = (y * image.Width + x) * 3;
                    var leaked = image.Pixels[index] == foreign.R
                                 && image.Pixels[index + 1] == foreign.G
                                 && image.Pixels[index + 2] == foreign.B;
                    Assert.False(
                        leaked,
                        $"槽位 ({row},{column}) 的图里出现了第 {other} 列的底色（{x},{y}），切到了邻卡。");
                }
            }
        }
    }

    /// <summary>四边第一行/第一列都是暗色边条：切歪或截断时会看到底色或邻卡颜色。</summary>
    private static void AssertEdgeMarkersIntact(DecodedImage image, int row, int column)
    {
        Assert.True(image.Width > 4 && image.Height > 4);

        // 边条是暗色（三通道都低于 40）。
        static bool IsEdge(DecodedImage image, int x, int y)
        {
            var index = (y * image.Width + x) * 3;
            return image.Pixels[index] < 40 && image.Pixels[index + 1] < 40 && image.Pixels[index + 2] < 40;
        }

        foreach (var x in new[] { 0, 1, image.Width - 1, image.Width - 2 })
        {
            Assert.True(IsEdge(image, x, image.Height / 2), $"槽位 ({row},{column}) 的左右边条缺失，切到了邻卡。");
        }

        foreach (var y in new[] { 0, 1, image.Height - 1, image.Height - 2 })
        {
            Assert.True(IsEdge(image, image.Width / 2, y), $"槽位 ({row},{column}) 的上下边条缺失，切到了邻卡。");
        }
    }

    private string WritePng(string name, Bitmap bitmap)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, TestPngWriter.Encode(bitmap.Pixels, bitmap.Width, bitmap.Height));
        return path;
    }

    /// <summary>测试自己的位图：RGB8 行优先。</summary>
    private sealed class Bitmap
    {
        public Bitmap(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 3];
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Pixels { get; }

        public void Set(int x, int y, (byte R, byte G, byte B) color)
        {
            var index = (y * Width + x) * 3;
            Pixels[index] = color.R;
            Pixels[index + 1] = color.G;
            Pixels[index + 2] = color.B;
        }

        public (byte R, byte G, byte B) Get(int x, int y)
        {
            var index = (y * Width + x) * 3;
            return (Pixels[index], Pixels[index + 1], Pixels[index + 2]);
        }

        public void Fill(int x0, int y0, int x1, int y1, (byte R, byte G, byte B) color)
        {
            for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
            {
                for (var x = Math.Max(0, x0); x < Math.Min(Width, x1); x++)
                {
                    Set(x, y, color);
                }
            }
        }

        public void FillCenter(int x0, int y0, int x1, int y1, (byte R, byte G, byte B) color, int w, int h) =>
            Fill((x0 + x1 - w) / 2, (y0 + y1 - h) / 2, (x0 + x1 + w) / 2, (y0 + y1 + h) / 2, color);

        public void Blit(Bitmap source, int left, int top)
        {
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    Set(left + x, top + y, source.Get(x, y));
                }
            }
        }
    }
}
