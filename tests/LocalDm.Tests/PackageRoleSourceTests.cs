using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Import;
using LocalDm.Tablet;

namespace LocalDm.Tests;

public sealed class PackageRoleSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localdm-role-source-" + Guid.NewGuid().ToString("N"));

    public PackageRoleSourceTests() => Directory.CreateDirectory(_root);

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
    public void Pdf_pages_become_distinct_frozen_png_assets_that_survive_source_removal()
    {
        var sources = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sources);
        var pdfPath = Path.Combine(sources, "role-book.pdf");
        WriteTwoPagePdf(pdfPath);
        var file = SourceFileOf("pdf-role", pdfPath, 2, SourceOrder.PageOrder);
        var spans = new[] { Span("pdf-p1", file.FileId, 1), Span("pdf-p2", file.FileId, 2) };
        var package = CreatePackage(
            [file],
            spans,
            ["pdf-p1", "pdf-p2"],
            [Segment("seg-p1", "合成第一页文字。", "pdf-p1"), Segment("seg-p2", "合成第二页文字。", "pdf-p2")]);
        var assetRoot = Path.Combine(_root, "assets");

        var assets = RoleSourceAssetBuilder.Materialize(package, assetRoot);

        Assert.Equal(2, assets.Count);
        Assert.All(assets, asset => Assert.Equal(TabletAssetKind.RolePage, asset.Kind));
        Assert.Equal(2, assets.Select(asset => asset.RelativePath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(assets, asset => Assert.Equal("image/png", MimeFor(asset.RelativePath)));
        var pngs = assets
            .Select(asset => File.ReadAllBytes(Path.Combine(assetRoot, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        var pixels = pngs.Select(DecodePngPixels).ToArray();
        Assert.False(pixels[0].SequenceEqual(pixels[1]));

        var prepared = package with { Tablet = Manifest(assets) };
        Directory.Delete(sources, recursive: true);
        var content = new PackageTabletContent(prepared, assetRoot);

        Assert.True(content.RoleBooks["role"].Confidence.HasSourcePages);
        Assert.True(content.TryGetRoleSourcePage("role", 1, out var first, out var firstType));
        Assert.True(content.TryGetRoleSourcePage("role", 2, out var second, out var secondType));
        Assert.Equal("image/png", firstType);
        Assert.Equal("image/png", secondType);
        Assert.Equal(pngs[0], first);
        Assert.Equal(pngs[1], second);
    }

    [Fact]
    public void Image_role_pages_preserve_declared_order_and_group_spans_without_repeating_text()
    {
        var sources = Path.Combine(_root, "images");
        Directory.CreateDirectory(sources);
        var thirdPath = WriteImage(sources, "third.png", 0x33);
        var firstPath = WriteImage(sources, "first.png", 0x11);
        var secondPath = WriteImage(sources, "second.png", 0x22);
        var third = SourceFileOf("file-third", thirdPath, 1, SourceOrder.ManifestOrder);
        var first = SourceFileOf("file-first", firstPath, 1, SourceOrder.ManifestOrder);
        var second = SourceFileOf("file-second", secondPath, 1, SourceOrder.ManifestOrder);
        var spans = new[]
        {
            Span("third-a", third.FileId, 1),
            Span("third-b", third.FileId, 1),
            Span("first", first.FileId, 1),
            Span("second", second.FileId, 1),
        };
        var segments = new[]
        {
            Segment("seg-third-a", "合成第三页的第一块。", "third-a", "third-b"),
            Segment("seg-third-b", "合成第三页的第二块。", "third-b"),
            Segment("seg-first", "合成第一页。", "first"),
            Segment("seg-second", "合成第二页。", "second"),
        };
        var package = CreatePackage(
            [third, first, second],
            spans,
            ["third-a", "third-b", "first", "second"],
            segments);
        var assetRoot = Path.Combine(_root, "image-assets");

        var assets = RoleSourceAssetBuilder.Materialize(package, assetRoot);
        var content = new PackageTabletContent(package with { Tablet = Manifest(assets) }, assetRoot);
        var book = content.RoleBooks["role"];

        Assert.Equal(3, book.Chapters.Count);
        Assert.Equal(new[] { "第 1 页", "第 2 页", "第 3 页" }, book.Chapters.Select(chapter => chapter.Title));
        Assert.Equal(3, book.Chapters.Select(chapter => chapter.ChapterId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, book.Chapters.SelectMany(chapter => chapter.Blocks).Select(block => block.BlockId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("合成第三页的第一块。\n合成第三页的第二块。", book.Chapters[0].Blocks.Single().Text);
        Assert.Equal(new int?[] { 1, 2, 3 }, book.Chapters.Select(chapter => chapter.Blocks.Single().SourcePageNumber));
        Assert.Equal(new[] { 1, 1, 1 }, assets.Select(asset => asset.SourcePageNumber));

        Assert.True(content.TryGetRoleSourcePage("role", 1, out var firstPage, out _));
        Assert.True(content.TryGetRoleSourcePage("role", 2, out var secondPage, out _));
        Assert.True(content.TryGetRoleSourcePage("role", 3, out var thirdPage, out _));
        Assert.Equal(File.ReadAllBytes(thirdPath), firstPage);
        Assert.Equal(File.ReadAllBytes(firstPath), secondPage);
        Assert.Equal(File.ReadAllBytes(secondPath), thirdPage);
        Assert.NotEqual(RoleSourceAssetBuilder.SourceFingerprint(package), RoleSourceAssetBuilder.SourceFingerprint(
            package with { Roles = [package.Roles[0] with { SourceSpanIds = ["first", "third-a", "third-b", "second"] }] }));
    }

    [Fact]
    public void Legacy_package_without_role_assets_does_not_fall_back_to_private_source_path()
    {
        var path = WriteImage(_root, "legacy-source.png", 0x55);
        var file = SourceFileOf("legacy-role", path, 1, SourceOrder.ManifestOrder);
        var package = CreatePackage(
            [file],
            [Span("legacy-span", file.FileId, 1)],
            ["legacy-span"],
            [Segment("legacy-segment", "合成角色本文字。", "legacy-span")]) with
        {
            Tablet = TabletManifest.Empty,
        };
        var content = new PackageTabletContent(package, Path.Combine(_root, "empty-assets"));

        Assert.Equal("合成角色本文字。", content.RoleBooks["role"].Chapters.Single().Blocks.Single().Text);
        Assert.False(content.RoleBooks["role"].Confidence.HasSourcePages);
        Assert.Empty(content.RoleBooks["role"].SourcePageAssetIds);
        Assert.False(content.TryGetRoleSourcePage("role", 1, out _, out _));
    }

    [Fact]
    public void Role_source_asset_tampering_and_path_traversal_are_rejected()
    {
        var sourcePath = WriteImage(_root, "role-source.png", 0x66);
        var file = SourceFileOf("role-file", sourcePath, 1, SourceOrder.ManifestOrder);
        var package = CreatePackage(
            [file],
            [Span("role-span", file.FileId, 1)],
            ["role-span"],
            [Segment("role-segment", "合成文字。", "role-span")]);
        var assetRoot = Path.Combine(_root, "role-assets");
        var generated = RoleSourceAssetBuilder.Materialize(package, assetRoot).Single();
        var tamperedPath = Path.Combine(assetRoot, generated.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(tamperedPath, SolidPng(0x77));
        var tamperedContent = new PackageTabletContent(package with { Tablet = Manifest([generated]) }, assetRoot);

        Assert.False(tamperedContent.TryGetRoleSourcePage("role", 1, out _, out _));

        var outsidePath = Path.Combine(_root, "outside.png");
        var outsideBytes = SolidPng(0x88);
        File.WriteAllBytes(outsidePath, outsideBytes);
        var traversalAsset = generated with
        {
            RelativePath = "../outside.png",
            ContentHash = Hash(outsideBytes),
        };
        var traversalContent = new PackageTabletContent(package with { Tablet = Manifest([traversalAsset]) }, assetRoot);

        Assert.False(traversalContent.TryGetRoleSourcePage("role", 1, out _, out _));
    }

    [Fact]
    public void Declared_source_hash_mismatch_fails_materialization()
    {
        var sourcePath = WriteImage(_root, "changed-role-source.png", 0x99);
        var file = SourceFileOf("changed-role", sourcePath, 1, SourceOrder.ManifestOrder) with
        {
            ContentHash = new string('0', 64),
        };
        var package = CreatePackage(
            [file],
            [Span("changed-span", file.FileId, 1)],
            ["changed-span"],
            [Segment("changed-segment", "合成文字。", "changed-span")]);

        Assert.Throws<InvalidDataException>(() => RoleSourceAssetBuilder.Materialize(package, Path.Combine(_root, "bad-assets")));
    }

    [Fact]
    public void Package_without_roles_does_not_read_or_create_assets_for_source_files()
    {
        var package = CreatePackage(
            [new SourceFile
            {
                FileId = "unused-file",
                PrivatePath = Path.Combine(_root, "missing-source.png"),
                ContentHash = new string('a', 64),
                PageCount = 1,
                Origin = SourceOrder.ManifestOrder,
                ToolIdentity = "synthetic",
                ParseState = FileParseState.Complete,
            }],
            [],
            [],
            []) with
        {
            Roles = [],
        };
        var assetRoot = Path.Combine(_root, "no-role-assets");

        Assert.Empty(RoleSourceAssetBuilder.Materialize(package, assetRoot));
        Assert.False(Directory.Exists(assetRoot));
        var fingerprint = RoleSourceAssetBuilder.SourceFingerprint(package);
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint, RoleSourceAssetBuilder.SourceFingerprint(package));
    }

    [Fact]
    public void Card_group_is_unavailable_when_a_sibling_asset_hash_does_not_match()
    {
        var assetRoot = Path.Combine(_root, "card-assets");
        Directory.CreateDirectory(assetRoot);
        var requestedBytes = SolidPng(0x21);
        var siblingBytes = SolidPng(0x43);
        File.WriteAllBytes(Path.Combine(assetRoot, "card-a.png"), requestedBytes);
        File.WriteAllBytes(Path.Combine(assetRoot, "card-b.png"), SolidPng(0x65));
        var assets = new Dictionary<string, TabletAsset>(StringComparer.Ordinal)
        {
            ["asset-a"] = CardAsset("asset-a", "card-a.png", requestedBytes, "card-a"),
            ["asset-b"] = CardAsset("asset-b", "card-b.png", siblingBytes, "card-b"),
        };
        var cards = new Dictionary<string, TabletCard>(StringComparer.Ordinal)
        {
            ["card-a"] = Card("card-a", "asset-a"),
            ["card-b"] = Card("card-b", "asset-b"),
        };
        var manifest = TabletManifest.Empty with
        {
            AssetRootFingerprint = new string('c', 64),
            Cards = cards,
            Groups = new Dictionary<string, TabletGroupPreparationState>(StringComparer.Ordinal)
            {
                ["group"] = new TabletGroupPreparationState
                {
                    GroupName = "group",
                    Readiness = TabletGroupReadiness.Ready,
                },
            },
            Assets = assets,
        };
        var package = CreatePackage([], [], [], []) with { Roles = [], Tablet = manifest };
        var content = new PackageTabletContent(package, assetRoot);

        Assert.False(content.TryGetCardImage("card-a", TabletCardSide.Front, out var bytes, out _, out var failure));
        Assert.Empty(bytes);
        Assert.Equal(TabletQueryFailure.GroupNotReady, failure);
    }

    private static ScriptPackage CreatePackage(
        IReadOnlyList<SourceFile> files,
        IReadOnlyList<SourceSpan> spans,
        IReadOnlyList<string> roleSpanIds,
        IReadOnlyList<CanonicalSegment> segments) => new()
    {
        SchemaVersion = ScriptPackage.CurrentSchemaVersion,
        PackageId = "package-role-test",
        Version = "1",
        Title = "合成角色本测试",
        PlayerCount = 1,
        ManifestHash = new string('a', 64),
        SourceFiles = files,
        Spans = spans,
        Segments = segments,
        Roles = [new RoleIndex { RoleId = "role", PrivateMaterialRef = "synthetic", SourceSpanIds = roleSpanIds }],
        Phases = [],
        Materials = [],
        Truth = new TruthSection { RevealedSegmentIds = [], PrivateText = "合成内容", IsConfirmedByHuman = false },
        Validation = PackageValidationState.Ready,
    };

    private static TabletManifest Manifest(IReadOnlyList<TabletAsset> assets) => TabletManifest.Empty with
    {
        AssetRootFingerprint = new string('c', 64),
        Assets = assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal),
    };

    private static TabletCard Card(string cardId, string assetId) => new()
    {
        CardId = cardId,
        DisplayName = cardId,
        GroupName = "group",
        SourceOrder = cardId == "card-a" ? 1 : 2,
        FrontAssetId = assetId,
        PreparationState = TabletCardPreparationState.Ready,
        SourceFileId = cardId,
        SourcePageNumber = 1,
    };

    private static TabletAsset CardAsset(string assetId, string relativePath, byte[] expectedBytes, string fileId) => new()
    {
        AssetId = assetId,
        RelativePath = relativePath,
        ContentHash = Hash(expectedBytes),
        Kind = TabletAssetKind.CardFront,
        SourceFileId = fileId,
        SourcePageNumber = 1,
    };

    private static SourceFile SourceFileOf(string fileId, string path, int pageCount, SourceOrder sourceOrder) => new()
    {
        FileId = fileId,
        PrivatePath = path,
        ContentHash = Hash(File.ReadAllBytes(path)),
        PageCount = pageCount,
        Origin = sourceOrder,
        ToolIdentity = "synthetic",
        ParseState = FileParseState.Complete,
    };

    private static SourceSpan Span(string spanId, string fileId, int pageNumber) => new()
    {
        SpanId = spanId,
        FileId = fileId,
        PageNumber = pageNumber,
        BlockNumber = 0,
        ToolLocator = $"synthetic/{fileId}/{pageNumber}/{spanId}",
        TextHash = Hash(Encoding.UTF8.GetBytes(spanId)),
        OriginalText = "合成来源文本。",
    };

    private static CanonicalSegment Segment(string segmentId, string text, params string[] spanIds) => new()
    {
        SegmentId = segmentId,
        OriginalText = text,
        SourceSpanIds = spanIds,
        Visibility = SegmentVisibility.Private,
    };

    private static string WriteImage(string directory, string name, byte marker)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, SolidPng(marker));
        return path;
    }

    private static byte[] SolidPng(byte marker)
    {
        var pixels = Enumerable.Repeat(marker, 20 * 12 * 3).ToArray();
        return TestPngWriter.Encode(pixels, 20, 12);
    }

    private static void WriteTwoPagePdf(string path)
    {
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<long> { 0, builder.Length };
        builder.Append("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        offsets.Add(builder.Length);
        builder.Append("2 0 obj<</Type/Pages/Count 2/Kids[3 0 R 5 0 R]>>endobj\n");

        for (var page = 0; page < 2; page++)
        {
            var pageObject = 3 + page * 2;
            offsets.Add(builder.Length);
            builder.Append($"{pageObject} 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 300]/Contents {pageObject + 1} 0 R/Resources<<>>>>endobj\n");
            var drawing = page == 0 ? "1 0 0 rg 10 200 180 50 re f" : "0 0 1 rg 10 50 180 50 re f";
            offsets.Add(builder.Length);
            builder.Append($"{pageObject + 1} 0 obj<</Length {drawing.Length}>>stream\n{drawing}\nendstream endobj\n");
        }

        var xref = builder.Length;
        builder.Append("xref\n0 ").Append(offsets.Count).Append('\n');
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        builder.Append("trailer<</Size ").Append(offsets.Count).Append("/Root 1 0 R>>\nstartxref\n")
            .Append(xref).Append("\n%%EOF");
        File.WriteAllText(path, builder.ToString(), Encoding.Latin1);
    }

    private static byte[] DecodePngPixels(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 3];
        converted.CopyPixels(pixels, converted.PixelWidth * 3, 0);
        return pixels;
    }

    private static string MimeFor(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "unknown";

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
