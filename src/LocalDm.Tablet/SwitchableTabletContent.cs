using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

public enum TabletStoryState { None, Preparing, Ready }

/// <summary>
/// 可热切换的平板内容：服务先起，剧本后装。没装好时 Roles / Cards 为空，取图一律失败，
/// 不用任何演示材料顶替。每次切换都触发 <see cref="Changed"/>。
/// </summary>
public sealed class SwitchableTabletContent : ITabletContent
{
    private static readonly IReadOnlyDictionary<string, RoleBookText> NoBooks = new Dictionary<string, RoleBookText>();
    private volatile ITabletContent? _inner;
    private volatile string _title = string.Empty;
    private volatile TabletStoryState _state = TabletStoryState.None;

    public event Action? Changed;

    public TabletStoryState StoryState => _state;

    public string StoryTitle => _inner?.StoryTitle ?? _title;

    public IReadOnlyList<TabletRole> Roles => _inner?.Roles ?? [];

    public IReadOnlyList<TabletCatalogItem> Cards => _inner?.Cards ?? [];

    public IReadOnlyDictionary<string, RoleBookText> RoleBooks => _inner?.RoleBooks ?? NoBooks;

    public void SetNone()
    {
        _inner = null;
        _title = string.Empty;
        _state = TabletStoryState.None;
        Changed?.Invoke();
    }

    public void SetPreparing(string title)
    {
        _inner = null;
        _title = title;
        _state = TabletStoryState.Preparing;
        Changed?.Invoke();
    }

    public void Set(ITabletContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _inner = content;
        _title = content.StoryTitle;
        _state = TabletStoryState.Ready;
        Changed?.Invoke();
    }

    public bool TryGetCardImage(
        string cardId,
        TabletCardSide side,
        out byte[] bytes,
        out string contentType,
        out TabletQueryFailure failure)
    {
        if (_inner is { } inner)
        {
            return inner.TryGetCardImage(cardId, side, out bytes, out contentType, out failure);
        }

        bytes = [];
        contentType = "application/octet-stream";
        failure = TabletQueryFailure.NotFound;
        return false;
    }

    public int HandbookPageCount => _inner?.HandbookPageCount ?? 0;

    public bool TryGetHandbookPage(int pageNumber, out byte[] bytes, out string contentType)
    {
        if (_inner is { } inner)
        {
            return inner.TryGetHandbookPage(pageNumber, out bytes, out contentType);
        }

        bytes = [];
        contentType = "application/octet-stream";
        return false;
    }

    public bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType)
    {
        if (_inner is { } inner)
        {
            return inner.TryGetRoleSourcePage(roleId, pageNumber, out bytes, out contentType);
        }

        bytes = [];
        contentType = "application/octet-stream";
        return false;
    }
}
