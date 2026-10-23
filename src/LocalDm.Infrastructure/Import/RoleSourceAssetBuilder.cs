using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using Windows.UI;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// Copies or renders a role's original pages into the frozen tablet asset directory.
/// </summary>
public static class RoleSourceAssetBuilder
{
    private const string RenderingRecipe = "role-source-assets-v1|images=original-bytes|pdf=Windows.Data.Pdf-192dpi-PNG";

    /// <summary>
    /// Materializes the pages referenced by role indexes. Image bytes are preserved;
    /// PDF pages are rendered individually to PNG. Sources are read and verified once per file.
    /// </summary>
    public static IReadOnlyList<TabletAsset> Materialize(ScriptPackage package, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);

        var pages = GetRolePages(package)
            .SelectMany(role => role.Pages)
            .DistinctBy(page => (page.File.FileId, page.PageNumber))
            .ToArray();
        return Materialize(pages, assetRoot, TabletAssetKind.RolePage, "role-page");
    }

    /// <summary>
    /// 组织者手册整份逐页（含真相页与不入包的页），按包内文件顺序、文件内页序返回（ADR 0009）。
    /// </summary>
    public static IReadOnlyList<TabletAsset> MaterializeHandbook(ScriptPackage package, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        return Materialize(HandbookPages(package), assetRoot, TabletAssetKind.HandbookPage, "handbook-page");
    }

    private static IReadOnlyList<TabletAsset> Materialize(
        IReadOnlyList<RoleSourcePage> pages,
        string assetRoot,
        TabletAssetKind kind,
        string idPrefix)
    {
        if (pages.Count == 0)
        {
            return [];
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("原稿页需要 Windows 10 1809 或更新版本。");
        }

        var fullAssetRoot = Path.GetFullPath(assetRoot);
        return Task.Run(() => MaterializeAsync(pages, fullAssetRoot, kind, idPrefix)).GetAwaiter().GetResult();
    }

    private static RoleSourcePage[] HandbookPages(ScriptPackage package) =>
        [.. LocalDm.Core.Hosting.HostNarration.HandbookFiles(package)
            .SelectMany(file => Enumerable.Range(1, file.PageCount).Select(page => new RoleSourcePage(file, page, [])))];

    /// <summary>
    /// Stable fingerprint of ordered role source identities and the page rendering recipe.
    /// </summary>
    public static string SourceFingerprint(ScriptPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var rolePages = GetRolePages(package);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, RenderingRecipe);
        Add(hash, rolePages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var role in rolePages)
        {
            Add(hash, role.RoleId);
            Add(hash, role.Pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var page in role.Pages)
            {
                Add(hash, page.File.FileId);
                Add(hash, page.File.ContentHash.ToLowerInvariant());
                Add(hash, page.File.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Add(hash, page.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Add(hash, page.SpanIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var spanId in page.SpanIds)
                {
                    Add(hash, spanId);
                }
            }
        }

        var handbookPages = HandbookPages(package);
        Add(hash, "handbook=" + handbookPages.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var page in handbookPages)
        {
            Add(hash, page.File.FileId);
            Add(hash, page.File.ContentHash.ToLowerInvariant());
            Add(hash, page.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<IReadOnlyList<TabletAsset>> MaterializeAsync(
        IReadOnlyList<RoleSourcePage> pages,
        string assetRoot,
        TabletAssetKind kind,
        string idPrefix)
    {
        var assets = new List<TabletAsset>(pages.Count);
        Directory.CreateDirectory(assetRoot);

        foreach (var filePages in pages.GroupBy(page => page.File.FileId, StringComparer.Ordinal))
        {
            var file = filePages.First().File;
            var sourceBytes = ReadAndVerify(file);
            var extension = Path.GetExtension(file.PrivatePath).ToLowerInvariant();

            if (extension == ".pdf")
            {
                var rendered = await RenderPagesAsync(file, sourceBytes, filePages.Select(page => page.PageNumber));
                foreach (var (pageNumber, png) in rendered)
                {
                    assets.Add(WriteAsset(assetRoot, file, pageNumber, ".png", png, kind, idPrefix));
                }
            }
            else if (extension is ".png" or ".jpg" or ".jpeg" or ".webp")
            {
                if (filePages.Any(page => page.PageNumber != 1))
                {
                    throw new InvalidDataException($"角色图片来源文件 {file.FileId} 只能有第 1 页。");
                }

                foreach (var page in filePages)
                {
                    assets.Add(WriteAsset(assetRoot, file, page.PageNumber, extension, sourceBytes, kind, idPrefix));
                }
            }
            else
            {
                throw new InvalidDataException($"角色来源文件 {file.FileId} 不是支持的图片或 PDF。");
            }
        }

        return assets;
    }

    /// <summary>
    /// Renders the given pages of a frozen PDF to PNG (same recipe as role pages), verifying the source hash first.
    /// </summary>
    internal static IReadOnlyList<(int PageNumber, byte[] Png)> RenderPdfPages(
        SourceFile file,
        IReadOnlyList<int> pageNumbers)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("PDF 原稿需要 Windows 10 1809 或更新版本。");
        }

        var sourceBytes = ReadAndVerify(file);
        return Task.Run(() => RenderPagesAsync(file, sourceBytes, pageNumbers)).GetAwaiter().GetResult();
    }

    private static async Task<IReadOnlyList<(int PageNumber, byte[] Png)>> RenderPagesAsync(
        SourceFile file,
        byte[] sourceBytes,
        IEnumerable<int> pageNumbers)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(sourceBytes.AsBuffer());
        stream.Seek(0);
        var document = await PdfDocument.LoadFromStreamAsync(stream);
        if (document.PageCount != file.PageCount)
        {
            throw new InvalidDataException($"来源文件 {file.FileId} 的页数与冻结记录不符。");
        }

        var rendered = new List<(int, byte[])>();
        foreach (var pageNumber in pageNumbers)
        {
            rendered.Add((pageNumber, await RenderPdfPageAsync(document, pageNumber - 1)));
        }

        return rendered;
    }

    private static byte[] ReadAndVerify(SourceFile file)
    {
        if (!File.Exists(file.PrivatePath))
        {
            throw new InvalidDataException($"角色来源文件 {file.FileId} 缺失。");
        }

        var bytes = File.ReadAllBytes(file.PrivatePath);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, file.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"角色来源文件 {file.FileId} 与冻结 hash 不符。");
        }

        return bytes;
    }

    private static async Task<byte[]> RenderPdfPageAsync(PdfDocument document, int pageIndex)
    {
        using var page = document.GetPage((uint)pageIndex);
        using var rendered = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Ceiling(page.Size.Width * 2)),
            DestinationHeight = (uint)Math.Max(1, Math.Ceiling(page.Size.Height * 2)),
            BackgroundColor = Color.FromArgb(255, 255, 255, 255),
        });

        rendered.Seek(0);
        using var reader = new DataReader(rendered.GetInputStreamAt(0));
        var renderedLength = checked((uint)rendered.Size);
        await reader.LoadAsync(renderedLength);
        var renderedBytes = new byte[checked((int)renderedLength)];
        reader.ReadBytes(renderedBytes);

        using var source = new MemoryStream(renderedBytes, writable: false);
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        using var pngStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(decoder.Frames[0]);
        encoder.Save(pngStream);
        return pngStream.ToArray();
    }

    private static TabletAsset WriteAsset(
        string assetRoot,
        SourceFile file,
        int pageNumber,
        string extension,
        byte[] bytes,
        TabletAssetKind kind,
        string idPrefix)
    {
        var fileKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.FileId)));
        var assetId = $"{idPrefix}-{fileKey}-p{pageNumber}";
        var relativePath = $"{idPrefix}s/{fileKey}-p{pageNumber}{extension}";
        var fullPath = Path.Combine(assetRoot, $"{idPrefix}s", $"{fileKey}-p{pageNumber}{extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);

        return new TabletAsset
        {
            AssetId = assetId,
            RelativePath = relativePath,
            ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Kind = kind,
            SourceFileId = file.FileId,
            SourceContentHash = file.ContentHash,
            SourcePageNumber = pageNumber,
        };
    }

    private static IReadOnlyList<RoleSourcePages> GetRolePages(ScriptPackage package)
    {
        if (package.Roles.Count == 0)
        {
            return [];
        }

        var files = package.SourceFiles.ToDictionary(file => file.FileId, StringComparer.Ordinal);
        var spans = package.Spans.ToDictionary(span => span.SpanId, StringComparer.Ordinal);
        var result = new List<RoleSourcePages>(package.Roles.Count);

        foreach (var role in package.Roles)
        {
            var pages = new List<RoleSourcePage>();
            var spanIdsByPage = new Dictionary<(string FileId, int PageNumber), List<string>>();

            foreach (var spanId in role.SourceSpanIds)
            {
                if (!spans.TryGetValue(spanId, out var span)
                    || !files.TryGetValue(span.FileId, out var file)
                    || span.PageNumber < 1
                    || span.PageNumber > file.PageCount)
                {
                    throw new InvalidDataException($"角色 {role.RoleId} 引用了无效的来源页。");
                }

                var key = (span.FileId, span.PageNumber);
                if (spanIdsByPage.TryGetValue(key, out var spanIds))
                {
                    spanIds.Add(spanId);
                    continue;
                }

                spanIdsByPage.Add(key, [spanId]);
                pages.Add(new RoleSourcePage(file, span.PageNumber, spanIdsByPage[key]));
            }

            result.Add(new RoleSourcePages(role.RoleId, pages));
        }

        return result;
    }

    private static void Add(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private sealed record RoleSourcePages(string RoleId, IReadOnlyList<RoleSourcePage> Pages);

    private sealed record RoleSourcePage(SourceFile File, int PageNumber, IReadOnlyList<string> SpanIds);
}
