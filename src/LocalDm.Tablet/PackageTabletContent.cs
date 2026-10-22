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
    private readonly ScriptPackage _package;
    private readonly string _assetRoot;

    public PackageTabletContent(ScriptPackage package, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);

        _package = package;
        _assetRoot = assetRoot;

        var manifest = package.Tablet
            ?? throw new InvalidOperationException("这个剧本包没有平板清单，要重新准备后才能上平板。");

        Cards = TabletQueries.BuildCatalog(manifest)
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
        RoleBooks = BuildRoleBooks(package);
    }

    public string StoryTitle => _package.Title;

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

        var lookup = TabletQueries.TryResolve(_package.Tablet!, cardId, side);
        if (!lookup.IsAvailable || lookup.RelativePath is null)
        {
            // 组没准备好与卡不存在要分开报：网页据此给 409 或 404。
            failure = lookup.Failure;
            return false;
        }

        var path = Path.Combine(_assetRoot, lookup.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            failure = TabletQueryFailure.AssetMissing;
            return false;
        }

        bytes = File.ReadAllBytes(path);
        contentType = ContentTypeFor(path);
        failure = TabletQueryFailure.None;
        return true;
    }

    public bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType)
    {
        bytes = [];
        contentType = "application/octet-stream";

        var path = SourcePagePath(roleId, pageNumber);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        bytes = File.ReadAllBytes(path);
        contentType = ContentTypeFor(path);
        return true;
    }

    /// <summary>角色本的原稿页路径：按页序取该角色自己的那一页。</summary>
    private string? SourcePagePath(string roleId, int pageNumber)
    {
        var role = _package.Roles.FirstOrDefault(r => string.Equals(r.RoleId, roleId, StringComparison.Ordinal));
        if (role is null || pageNumber < 1)
        {
            return null;
        }

        var spans = _package.Spans
            .Where(span => role.SourceSpanIds.Contains(span.SpanId, StringComparer.Ordinal))
            .OrderBy(span => span.PageNumber)
            .ToArray();

        if (pageNumber > spans.Length)
        {
            return null;
        }

        var file = _package.SourceFiles.FirstOrDefault(f =>
            string.Equals(f.FileId, spans[pageNumber - 1].FileId, StringComparison.Ordinal));
        return file?.PrivatePath;
    }

    /// <summary>
    /// 角色本：一页一章，文字原样来自识别结果，不润色、不概括。
    /// 识别为空的那几页照实记在置信里，界面提示「可与原稿核对」。
    /// </summary>
    private static IReadOnlyDictionary<string, RoleBookText> BuildRoleBooks(ScriptPackage package)
    {
        var spans = package.Spans.ToDictionary(span => span.SpanId, StringComparer.Ordinal);
        var books = new Dictionary<string, RoleBookText>(StringComparer.Ordinal);

        foreach (var role in package.Roles)
        {
            var pages = role.SourceSpanIds
                .Where(id => spans.ContainsKey(id))
                .Select(id => spans[id])
                .OrderBy(span => span.PageNumber)
                .ToArray();

            var chapters = new List<RoleBookChapter>();
            var empty = 0;
            var characters = 0;

            foreach (var page in pages)
            {
                var text = string.Join(
                    "\n",
                    package.Segments
                        .Where(segment => segment.SourceSpanIds.Contains(page.SpanId, StringComparer.Ordinal))
                        .Select(segment => segment.OriginalText)
                        .Where(part => !string.IsNullOrWhiteSpace(part)));

                if (text.Length == 0)
                {
                    empty++;
                }

                characters += text.Length;
                chapters.Add(new RoleBookChapter
                {
                    ChapterId = $"{role.RoleId}-p{page.PageNumber}",
                    Title = $"第 {page.PageNumber} 页",
                    Blocks = text.Length == 0
                        ? []
                        : [new RoleBookBlock
                        {
                            BlockId = $"{role.RoleId}-p{page.PageNumber}-b0",
                            Kind = RoleBookBlockKind.Paragraph,
                            Text = text,
                            SourceSpanId = page.SpanId,
                            SourcePageNumber = page.PageNumber,
                        }],
                });
            }

            books[role.RoleId] = new RoleBookText
            {
                RoleId = role.RoleId,
                Chapters = chapters,
                SourcePageAssetIds = pages.Select(page => page.FileId).ToArray(),
                Confidence = new RoleBookTextConfidence
                {
                    TotalBlocks = chapters.Count,
                    EmptyOrSuspectBlocks = empty,
                    TotalCharacters = characters,
                    HasSourcePages = true,
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
}
