using System.Security.Cryptography;
using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 真实剧本包的内容来源：给平板发这一局真正的东西。
///
/// 卡目录与资产都来自冻结包里的平板清单：
/// 清单里没有的卡、没准备好的组一律取不到（返回 false），不用整页图顶替。
/// 角色本只给本人：按认领的角色取它自己的页与文字。
/// </summary>
public sealed class PackageTabletContent : ITabletContent
{
    private readonly TabletManifest _manifest;
    private readonly string _assetRoot;
    private readonly string _storyTitle;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<RoleSourcePage>> _roleSourcePages;

    public PackageTabletContent(ScriptPackage package, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);

        _storyTitle = package.Title;
        _assetRoot = Path.GetFullPath(assetRoot);
        _manifest = package.Tablet
            ?? throw new InvalidOperationException("这个剧本包没有平板清单，要重新准备后才能上平板。");

        Cards = TabletQueries.BuildCatalog(_manifest)
            .Select(item => new TabletCatalogItem
            {
                CardId = item.CardId,
                DisplayName = item.DisplayName,
                Label = item.Label,
                GroupName = item.GroupName,
                SourceOrder = item.SourceOrder,
                IsAvailable = item.IsAvailable,
                NotReadyReason = item.NotReadyReason,
            })
            .ToArray();

        Roles = package.Roles.Select(role => new TabletRole(role.RoleId, role.RoleId)).ToArray();
        _roleSourcePages = BuildRoleSourcePages(package, _manifest.Assets);
        RoleBooks = BuildRoleBooks(package, _roleSourcePages);
    }

    public string StoryTitle => _storyTitle;

    public IReadOnlyList<TabletRole> Roles { get; }

    public IReadOnlyList<TabletCatalogItem> Cards { get; }

    public IReadOnlyDictionary<string, RoleBookText> RoleBooks { get; }

    public bool TryGetCardImage(
        string cardId,
        TabletCardSide side,
        out byte[] bytes,
        out string contentType,
        out TabletQueryFailure failure)
    {
        bytes = [];
        contentType = "application/octet-stream";
        failure = TabletQueryFailure.NotFound;

        var lookup = TabletQueries.TryResolve(_manifest, cardId, side);
        if (!lookup.IsAvailable || lookup.RelativePath is null)
        {
            // 组没准备好与卡不存在要分开报：网页据此给 409 或 404。
            failure = lookup.Failure == TabletQueryFailure.AssetMissing && lookup.Card is not null
                ? TabletQueryFailure.GroupNotReady
                : lookup.Failure;
            return false;
        }

        if (lookup.Card is null
            || lookup.AssetId is null
            || !TryReadGroupAssets(lookup.Card, lookup.AssetId, out bytes, out var path))
        {
            failure = TabletQueryFailure.GroupNotReady;
            return false;
        }

        contentType = ContentTypeFor(path);
        failure = TabletQueryFailure.None;
        return true;
    }

    private bool TryReadGroupAssets(TabletCard requestedCard, string requestedAssetId, out byte[] requestedBytes, out string requestedPath)
    {
        requestedBytes = [];
        requestedPath = string.Empty;
        var readAssets = new Dictionary<string, (byte[] Bytes, string Path)>(StringComparer.Ordinal);

        foreach (var card in _manifest.Cards.Values.Where(card => string.Equals(
                     card.GroupName,
                     requestedCard.GroupName,
                     StringComparison.Ordinal)))
        {
            var backAssetId = card.BackAssetId;
            if (!ReadDeclaredAsset(card.FrontAssetId, TabletAssetKind.CardFront)
                || (backAssetId is not null && !ReadDeclaredAsset(backAssetId, TabletAssetKind.CardBack)))
            {
                return false;
            }
        }

        if (!readAssets.TryGetValue(requestedAssetId, out var requested))
        {
            return false;
        }

        requestedBytes = requested.Bytes;
        requestedPath = requested.Path;
        return true;

        bool ReadDeclaredAsset(string assetId, TabletAssetKind expectedKind)
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                return false;
            }

            if (readAssets.ContainsKey(assetId))
            {
                return _manifest.Assets.TryGetValue(assetId, out var existing) && existing.Kind == expectedKind;
            }

            if (!_manifest.Assets.TryGetValue(assetId, out var asset)
                || asset.Kind != expectedKind
                || !TryReadAsset(asset.RelativePath, asset.ContentHash, out var assetBytes, out var assetPath))
            {
                return false;
            }

            readAssets.Add(assetId, (assetBytes, assetPath));
            return true;
        }
    }

    public bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType)
    {
        bytes = [];
        contentType = "application/octet-stream";

        if (pageNumber < 1
            || !_roleSourcePages.TryGetValue(roleId, out var pages)
            || pageNumber > pages.Count
            || pages[pageNumber - 1].Asset is not { } asset
            || !TryReadAsset(asset.RelativePath, asset.ContentHash, out bytes, out var path))
        {
            return false;
        }

        contentType = ContentTypeFor(path);
        return true;
    }

    private bool TryReadAsset(string relativePath, string expectedHash, out byte[] bytes, out string path)
    {
        bytes = [];
        path = string.Empty;
        if (!TryResolveAssetPath(relativePath, out var resolved))
        {
            return false;
        }

        byte[] content;
        try
        {
            // Read once so the bytes returned are exactly the bytes whose hash was checked.
            content = File.ReadAllBytes(resolved);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bytes = content;
        path = resolved;
        return true;
    }

    private bool TryResolveAssetPath(string relativePath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(_assetRoot, normalized));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }

        var rootWithSeparator = _assetRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _assetRoot
            : _assetRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.StartsWith(rootWithSeparator, comparison))
        {
            return false;
        }

        path = resolved;
        return true;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<RoleSourcePage>> BuildRoleSourcePages(
        ScriptPackage package,
        IReadOnlyDictionary<string, TabletAsset> assets)
    {
        var spans = package.Spans.ToDictionary(span => span.SpanId, StringComparer.Ordinal);
        var rolePageAssets = assets.Values
            .Where(asset => asset.Kind == TabletAssetKind.RolePage)
            .ToDictionary(asset => (asset.SourceFileId, asset.SourcePageNumber));
        var rolePages = new Dictionary<string, IReadOnlyList<RoleSourcePage>>(StringComparer.Ordinal);

        foreach (var role in package.Roles)
        {
            var pages = new List<RoleSourcePage>();
            var bySourcePage = new Dictionary<(string FileId, int PageNumber), RoleSourcePage>();
            foreach (var spanId in role.SourceSpanIds)
            {
                if (!spans.TryGetValue(spanId, out var span))
                {
                    continue;
                }

                var key = (span.FileId, span.PageNumber);
                if (!bySourcePage.TryGetValue(key, out var page))
                {
                    rolePageAssets.TryGetValue(key, out var asset);
                    page = new RoleSourcePage(asset, []);
                    bySourcePage.Add(key, page);
                    pages.Add(page);
                }

                page.SpanIds.Add(spanId);
            }

            rolePages.Add(role.RoleId, pages);
        }

        return rolePages;
    }

    /// <summary>
    /// 角色本：一页一章，文字原样来自识别结果，不润色、不概括。
    /// 识别为空的那几页照实记在置信里，界面提示「可与原稿核对」。
    /// </summary>
    private static IReadOnlyDictionary<string, RoleBookText> BuildRoleBooks(
        ScriptPackage package,
        IReadOnlyDictionary<string, IReadOnlyList<RoleSourcePage>> roleSourcePages)
    {
        var books = new Dictionary<string, RoleBookText>(StringComparer.Ordinal);

        foreach (var role in package.Roles)
        {
            var pages = roleSourcePages[role.RoleId];
            var chapters = new List<RoleBookChapter>();
            var sourcePageAssets = new List<string>();
            var allSourcePagesAvailable = pages.Count > 0;
            var usedSegmentIds = new HashSet<string>(StringComparer.Ordinal);
            var empty = 0;
            var characters = 0;

            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                var page = pages[pageIndex];
                var logicalPageNumber = pageIndex + 1;
                var pageSpanIds = page.SpanIds.ToHashSet(StringComparer.Ordinal);
                var text = string.Join(
                    "\n",
                    package.Segments
                        .Where(segment => !usedSegmentIds.Contains(segment.SegmentId)
                                          && segment.SourceSpanIds.Any(pageSpanIds.Contains))
                        .Where(segment => usedSegmentIds.Add(segment.SegmentId))
                        .Select(segment => segment.OriginalText)
                        .Where(part => !string.IsNullOrWhiteSpace(part)));

                if (text.Length == 0)
                {
                    empty++;
                }

                characters += text.Length;
                chapters.Add(new RoleBookChapter
                {
                    ChapterId = $"{role.RoleId}-chapter-{logicalPageNumber}",
                    Title = $"第 {logicalPageNumber} 页",
                    Blocks = text.Length == 0
                        ? []
                        : [new RoleBookBlock
                        {
                            BlockId = $"{role.RoleId}-chapter-{logicalPageNumber}-block-0",
                            Kind = RoleBookBlockKind.Paragraph,
                            Text = text,
                            SourceSpanId = page.SpanIds[0],
                            // Reader source URLs use the role's logical 1..N order;
                            // the frozen asset retains the source file's internal page number.
                            SourcePageNumber = logicalPageNumber,
                        }],
                });

                if (page.Asset is { } asset)
                {
                    sourcePageAssets.Add(asset.AssetId);
                }
                else
                {
                    allSourcePagesAvailable = false;
                }
            }

            books[role.RoleId] = new RoleBookText
            {
                RoleId = role.RoleId,
                Chapters = chapters,
                SourcePageAssetIds = allSourcePagesAvailable ? sourcePageAssets : [],
                Confidence = new RoleBookTextConfidence
                {
                    TotalBlocks = chapters.Count,
                    EmptyOrSuspectBlocks = empty,
                    TotalCharacters = characters,
                    HasSourcePages = allSourcePagesAvailable,
                },
            };
        }

        return books;
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    private sealed record RoleSourcePage(TabletAsset? Asset, List<string> SpanIds);
}
