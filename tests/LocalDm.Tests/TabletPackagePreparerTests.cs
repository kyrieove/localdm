using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Tests;

/// <summary>
/// 平板资产准备的拒绝与冻结行为。输入只用测试里生成的图片和合成包。
/// </summary>
public sealed class TabletPackagePreparerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-tablet-preparer-" + Guid.NewGuid().ToString("N"));

    public TabletPackagePreparerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void An_unconfirmed_contact_sheet_stays_paused_and_is_not_materialized_as_a_card()
    {
        // 一张编号文件里有两个不同色块，默认布局未知，不能整张当作一张 Ready 卡。
        var package = CreatePackage(("clues", "1.png", 1));
        var source = package.SourceFiles[0];
        File.WriteAllBytes(source.PrivatePath, PngCodec.Encode(ContactSheetImage()));
        package = package with
        {
            SourceFiles = [source with { ContentHash = TabletAssetBuilder.ComputeFileHash(source.PrivatePath) }],
        };

        var manifest = TabletPackagePreparer.Prepare(package, Path.Combine(_root, "assets"));

        var card = Assert.Single(manifest.Cards.Values);
        Assert.False(manifest.Groups["clues"].IsReady);
        Assert.Equal(TabletNotReadyReason.CropUnconfirmed, manifest.Groups["clues"].Reason);
        Assert.Equal(TabletCardPreparationState.NotReady, card.PreparationState);
        Assert.Equal(TabletNotReadyReason.CropUnconfirmed, card.NotReadyReason);
        Assert.Empty(manifest.Assets);
    }

    [Fact]
    public void A_pdf_group_becomes_one_ready_card_per_page_without_confirmation()
    {
        var pdfPath = Path.Combine(_root, "round.pdf");
        PackageRoleSourceTests.WriteTwoPagePdf(pdfPath);
        var package = CreatePackage(("round", "1.png", 1));
        var pdf = package.SourceFiles[0] with
        {
            FileId = "pdf-file",
            PrivatePath = pdfPath,
            ContentHash = TabletAssetBuilder.ComputeFileHash(pdfPath),
            PageCount = 2,
        };
        // 识别只在第 1 页找到两块文字：材料里同一个 PDF 重复出现，第 2 页没有 span，也必须出卡。
        var first = package.Spans[0] with { FileId = pdf.FileId };
        var second = first with { SpanId = "span-1", BlockNumber = 2 };
        package = package with
        {
            SourceFiles = [pdf],
            Spans = [first, second],
            Materials = [package.Materials[0] with { SourceSpanIds = [first.SpanId, second.SpanId] }],
        };

        var manifest = TabletPackagePreparer.Prepare(package, Path.Combine(_root, "assets"));

        Assert.True(manifest.Groups["round"].IsReady);
        Assert.Equal(["第 1 页", "第 2 页"], manifest.Cards.Values.OrderBy(c => c.SourceOrder).Select(c => c.DisplayName));
        Assert.All(manifest.Cards.Values, card =>
        {
            Assert.Equal(TabletCardPreparationState.Ready, card.PreparationState);
            Assert.Null(card.BackAssetId);
        });
        var assets = manifest.Assets.Values.OrderBy(a => a.SourcePageNumber).ToArray();
        Assert.Equal([1, 2], assets.Select(a => a.SourcePageNumber));
        Assert.All(assets, asset =>
        {
            Assert.Equal(TabletAssetKind.CardFront, asset.Kind);
            Assert.EndsWith(".png", asset.RelativePath);
        });
        var bytes = assets.Select(a => File.ReadAllBytes(Path.Combine(
            _root, "assets", manifest.AssetRootFingerprint, a.RelativePath.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
        Assert.False(bytes[0].SequenceEqual(bytes[1]));
    }

    [Fact]
    public void Confirmed_single_card_group_materializes_its_card()
    {
        var package = CreatePackage(("clues", "1.png", 7));

        var manifest = TabletPackagePreparer.Prepare(
            package,
            Path.Combine(_root, "assets"),
            new HashSet<string>(["clues"], StringComparer.Ordinal));

        var card = Assert.Single(manifest.Cards.Values);
        Assert.True(manifest.Groups["clues"].IsReady);
        Assert.Equal(TabletCardPreparationState.Ready, card.PreparationState);
        var asset = manifest.Assets[card.FrontAssetId];
        var path = Path.Combine(
            _root,
            "assets",
            manifest.AssetRootFingerprint,
            asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(File.ReadAllBytes(package.SourceFiles[0].PrivatePath), File.ReadAllBytes(path));
    }

    [Fact]
    public void A_card_back_asset_keeps_the_back_file_as_its_source()
    {
        var package = CreatePackage(("clues", "1.png", 3), ("clues", "1背.png", 9));

        var manifest = TabletPackagePreparer.Prepare(
            package,
            Path.Combine(_root, "assets"),
            new HashSet<string>(["clues"], StringComparer.Ordinal));

        var card = Assert.Single(manifest.Cards.Values);
        var backAsset = manifest.Assets[card.BackAssetId!];
        Assert.Equal("file-1", backAsset.SourceFileId);
        Assert.Equal(package.SourceFiles[1].ContentHash, backAsset.SourceContentHash);
        Assert.Equal(TabletCardPreparationState.Ready, card.PreparationState);
    }

    [Fact]
    public void Similar_material_names_keep_their_card_files_distinct()
    {
        var package = CreatePackage(
            ("A.B", "1.png", 3),
            ("A B", "1.png", 9));

        var manifest = TabletPackagePreparer.Prepare(
            package,
            Path.Combine(_root, "assets"),
            new HashSet<string>(["A.B", "A B"], StringComparer.Ordinal));

        var first = manifest.Cards["A.B-1"];
        var second = manifest.Cards["A B-1"];
        var firstAsset = manifest.Assets[first.FrontAssetId];
        var secondAsset = manifest.Assets[second.FrontAssetId];
        Assert.NotEqual(firstAsset.RelativePath, secondAsset.RelativePath);

        var assetRoot = Path.Combine(_root, "assets", manifest.AssetRootFingerprint);
        var firstBytes = File.ReadAllBytes(Path.Combine(assetRoot, firstAsset.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var secondBytes = File.ReadAllBytes(Path.Combine(assetRoot, secondAsset.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(File.ReadAllBytes(package.SourceFiles[0].PrivatePath), firstBytes);
        Assert.Equal(File.ReadAllBytes(package.SourceFiles[1].PrivatePath), secondBytes);
        Assert.NotEqual(firstAsset.ContentHash, secondAsset.ContentHash);
    }

    [Fact]
    public void The_asset_root_fingerprint_includes_role_source_content()
    {
        var package = CreatePackage(("clues", "1.png", 3));
        var source = package.SourceFiles[0];
        var rolePackage = package with
        {
            Roles = [new RoleIndex
            {
                RoleId = "role",
                PrivateMaterialRef = "synthetic",
                SourceSpanIds = [package.Spans[0].SpanId],
            }],
        };

        var first = TabletPackagePreparer.Prepare(rolePackage, Path.Combine(_root, "assets"));
        File.WriteAllBytes(source.PrivatePath, PngCodec.Encode(SolidImage(8)));
        var changedPackage = rolePackage with
        {
            SourceFiles = [source with { ContentHash = TabletAssetBuilder.ComputeFileHash(source.PrivatePath) }],
        };
        var second = TabletPackagePreparer.Prepare(changedPackage, Path.Combine(_root, "assets"));

        Assert.NotEqual(first.AssetRootFingerprint, second.AssetRootFingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_declared_card_back_that_is_missing_or_changed_pauses_its_group(bool removeBack)
    {
        const string group = "clues";
        var package = CreatePackage((group, "1.png", 1), (group, "1背.png", 2));
        var back = package.SourceFiles.Single(file => file.PrivatePath.EndsWith("1背.png", StringComparison.Ordinal));
        if (removeBack)
        {
            File.Delete(back.PrivatePath);
        }
        else
        {
            File.WriteAllBytes(back.PrivatePath, PngCodec.Encode(SolidImage(4)));
        }

        var manifest = TabletPackagePreparer.Prepare(
            package,
            Path.Combine(_root, "assets"),
            new HashSet<string>([group], StringComparer.Ordinal));

        Assert.False(manifest.Groups[group].IsReady);
        Assert.Equal(TabletNotReadyReason.AssetMissing, manifest.Groups[group].Reason);
        var card = Assert.Single(manifest.Cards.Values);
        Assert.Equal(TabletCardPreparationState.NotReady, card.PreparationState);
        Assert.Equal(TabletNotReadyReason.AssetMissing, card.NotReadyReason);
        Assert.NotNull(card.BackAssetId);
        Assert.Empty(manifest.Assets);
    }

    private ScriptPackage CreatePackage(params (string Group, string FileName, byte Marker)[] sourceSpecs)
    {
        var sourceFiles = new List<SourceFile>();
        var spans = new List<SourceSpan>();
        var materialSpans = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        for (var index = 0; index < sourceSpecs.Length; index++)
        {
            var spec = sourceSpecs[index];
            var sourceDirectory = Path.Combine(_root, $"source-{index}");
            Directory.CreateDirectory(sourceDirectory);
            var path = Path.Combine(sourceDirectory, spec.FileName);
            File.WriteAllBytes(path, PngCodec.Encode(SolidImage(spec.Marker)));
            var fileId = $"file-{index}";
            var spanId = $"span-{index}";
            sourceFiles.Add(new SourceFile
            {
                FileId = fileId,
                PrivatePath = path,
                ContentHash = TabletAssetBuilder.ComputeFileHash(path),
                PageCount = 1,
                Origin = SourceOrder.PageOrder,
                ToolIdentity = "synthetic",
                ParseState = FileParseState.Complete,
            });
            spans.Add(new SourceSpan
            {
                SpanId = spanId,
                FileId = fileId,
                PageNumber = 1,
                BlockNumber = 1,
                ToolLocator = $"doc:{fileId}/page:1/block:1",
                TextHash = new string('a', 64),
                OriginalText = "synthetic",
            });
            if (!materialSpans.TryGetValue(spec.Group, out var ids))
            {
                ids = [];
                materialSpans.Add(spec.Group, ids);
            }

            ids.Add(spanId);
        }

        return new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = "pkg-synthetic-tablet-preparer",
            Version = "1",
            Title = "合成平板准备测试",
            PlayerCount = 1,
            ManifestHash = new string('b', 64),
            ContentHash = new string('c', 64),
            SourceFiles = sourceFiles,
            Spans = spans,
            Segments = [],
            Roles = [],
            Phases = [],
            Materials = materialSpans.Select(pair => new Material
            {
                MaterialId = pair.Key,
                Label = pair.Key,
                SourceSpanIds = pair.Value,
            }).ToArray(),
            Truth = new TruthSection
            {
                RevealedSegmentIds = [],
                PrivateText = string.Empty,
                IsConfirmedByHuman = false,
            },
            Validation = PackageValidationState.Ready,
        };
    }

    private static RasterImage SolidImage(byte marker)
    {
        var image = new RasterImage(8, 8);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                image.Set(x, y, (marker, marker, marker));
            }
        }

        return image;
    }

    private static RasterImage ContactSheetImage()
    {
        var image = new RasterImage(8, 4);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var marker = (byte)(x < image.Width / 2 ? 1 : 2);
                image.Set(x, y, (marker, marker, marker));
            }
        }

        return image;
    }
}
