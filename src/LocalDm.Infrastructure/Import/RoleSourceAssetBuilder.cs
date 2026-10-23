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

        var rolePages = GetRolePages(package);
        var pages = rolePages
            .SelectMany(role => role.Pages)
            .DistinctBy(page => (page.File.FileId, page.PageNumber))
            .ToArray();
        if (pages.Length == 0)
        {
            return [];
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("角色本原稿需要 Windows 10 1809 或更新版本。");
        }

        var fullAssetRoot = Path.GetFullPath(assetRoot);
        return Task.Run(() => MaterializeAsync(pages, fullAssetRoot)).GetAwaiter().GetResult();
    }

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

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<IReadOnlyList<TabletAsset>> MaterializeAsync(
        IReadOnlyList<RoleSourcePage> pages,
        string assetRoot)
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
                    assets.Add(WriteAsset(assetRoot, file, pageNumber, ".png", png));
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
                    assets.Add(WriteAsset(assetRoot, file, page.PageNumber, extension, sourceBytes));
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
        byte[] bytes)
    {
        var fileKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.FileId)));
        var assetId = $"role-page-{fileKey}-p{pageNumber}";
        var relativePath = $"role-pages/{fileKey}-p{pageNumber}{extension}";
        var fullPath = Path.Combine(assetRoot, "role-pages", $"{fileKey}-p{pageNumber}{extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);

        return new TabletAsset
        {
            AssetId = assetId,
            RelativePath = relativePath,
            ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Kind = TabletAssetKind.RolePage,
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
