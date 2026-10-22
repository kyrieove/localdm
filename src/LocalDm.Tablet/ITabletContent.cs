using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 平板页面的内容来源。
///
/// 合成探针与真实剧本包都走这一个口子：网页只认角色、卡目录、角色本与图片，
/// 不关心背后是合成数据还是冻结包。
/// </summary>
public interface ITabletContent
{
    /// <summary>剧本名，显示在阅读器顶部。</summary>
    string StoryTitle { get; }

    /// <summary>本局可以认领的角色。</summary>
    IReadOnlyList<TabletRole> Roles { get; }

    /// <summary>卡目录：按名称列出全部卡，带分组与编号。</summary>
    IReadOnlyList<TabletCatalogItem> Cards { get; }

    /// <summary>每个角色的阅读材料（只给本人）。</summary>
    IReadOnlyDictionary<string, RoleBookText> RoleBooks { get; }

    /// <summary>
    /// 取一张卡的图片字节。
    ///
    /// 取不到就返回 false，并给出失败原因：「这一组暂停分发」和「没有这张卡」
    /// 是两回事 —— 前者是 409（换组别的卡），后者是 404。
    /// 不拿别的图顶替，也不返回整页图。
    /// </summary>
    bool TryGetCardImage(
        string cardId,
        TabletCardSide side,
        out byte[] bytes,
        out string contentType,
        out TabletQueryFailure failure);

    /// <summary>取角色本的原稿页图片（只给本人）。</summary>
    bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType);
}

/// <summary>合成探针的内容来源：全部是本地生成的假数据，不读任何真实剧本。</summary>
public sealed class SpikeTabletContent : ITabletContent
{
    private readonly TabletSpikeData _data;

    public SpikeTabletContent(TabletSpikeData data) => _data = data;

    public string StoryTitle => _data.StoryTitle;

    public IReadOnlyList<TabletRole> Roles => _data.Roles;

    public IReadOnlyList<TabletCatalogItem> Cards => _data.Cards
        .Select(card => new TabletCatalogItem
        {
            CardId = card.CardId,
            DisplayName = card.DisplayName,
            Label = card.DisplayName,
            GroupName = card.GroupName,
            SourceOrder = card.SourceOrder,
            IsAvailable = true,
        })
        .ToArray();

    public IReadOnlyDictionary<string, RoleBookText> RoleBooks => _data.RoleBooks;

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

        if (side != TabletCardSide.Front)
        {
            failure = TabletQueryFailure.NoBackSide;
            return false;
        }

        if (!_data.Cards.Any(c => c.CardId == cardId))
        {
            return false;
        }

        var path = Path.Combine(_data.AssetRoot, cardId + ".png");
        if (!File.Exists(path))
        {
            failure = TabletQueryFailure.AssetMissing;
            return false;
        }

        bytes = File.ReadAllBytes(path);
        contentType = "image/png";
        failure = TabletQueryFailure.None;
        return true;
    }

    public bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType)
    {
        bytes = [];
        contentType = "application/octet-stream";

        var path = Path.Combine(_data.AssetRoot, $"{roleId}-source-{pageNumber}.png");
        if (!File.Exists(path))
        {
            return false;
        }

        bytes = File.ReadAllBytes(path);
        contentType = "image/png";
        return true;
    }
}
