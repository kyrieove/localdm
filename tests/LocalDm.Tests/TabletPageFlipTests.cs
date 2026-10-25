using LocalDm.Core.Tablet;
using LocalDm.Tablet;

namespace LocalDm.Tests;

public sealed class TabletPageFlipTests
{
    private sealed class MinimalTabletContent : ITabletContent
    {
        public string StoryTitle => "测试剧本";

        public IReadOnlyList<TabletRole> Roles { get; } =
        [
            new TabletRole("role-1", "角色甲"),
            new TabletRole("role-2", "角色乙"),
        ];

        public IReadOnlyList<TabletCatalogItem> Cards => [];

        public IReadOnlyDictionary<string, RoleBookText> RoleBooks => new Dictionary<string, RoleBookText>();

        public bool TryGetCardImage(string cardId, TabletCardSide side, out byte[] bytes, out string contentType, out TabletQueryFailure failure)
        {
            bytes = [];
            contentType = string.Empty;
            failure = TabletQueryFailure.AssetMissing;
            return false;
        }

        public bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType)
        {
            bytes = [];
            contentType = string.Empty;
            return false;
        }
    }

    [Fact]
    public void Page_flip_follows_order_and_resets_on_release()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);

        Assert.True(state.TryClaim("k", content.Roles[0].RoleId));

        Assert.False(state.OpenPage("k", 2));
        Assert.Equal(0, state.Session("k").OpenedPages);

        Assert.True(state.OpenPage("k", 1));
        Assert.True(state.OpenPage("k", 2));
        Assert.Equal(2, state.Session("k").OpenedPages);

        Assert.False(state.OpenPage("k", 2));
        Assert.Equal(2, state.Session("k").OpenedPages);

        state.Release("k");
        Assert.Equal(0, state.Session("k").OpenedPages);
        Assert.False(state.OpenPage("k", 1));
    }
}
