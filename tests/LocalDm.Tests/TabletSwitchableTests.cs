using LocalDm.Core.Tablet;
using LocalDm.Tablet;

namespace LocalDm.Tests;

/// <summary>平板内容热切换：换剧本时清空全部认领，没装好时不拿别的图顶替。</summary>
public sealed class TabletSwitchableTests
{
    [Fact]
    public void Switching_content_clears_every_claim_and_reports_state()
    {
        var content = new SwitchableTabletContent();
        var state = new TabletSpikeState(content);

        Assert.Equal(TabletStoryState.None, state.StoryState);
        Assert.Empty(state.Roles);

        content.SetPreparing("甲");
        Assert.Equal(TabletStoryState.Preparing, state.StoryState);
        Assert.Equal("甲", content.StoryTitle);

        content.Set(new FakeContent());
        Assert.Equal(TabletStoryState.Ready, state.StoryState);
        Assert.True(state.TryClaim("k1", "r1"));
        Assert.Contains("r1", state.ClaimedRoles);

        content.SetNone();
        Assert.Empty(state.ClaimedRoles);
        Assert.Null(state.Session("k1").RoleId);
        Assert.Equal(TabletStoryState.None, state.StoryState);
    }

    [Fact]
    public void Empty_content_never_serves_images()
    {
        var content = new SwitchableTabletContent();

        Assert.False(content.TryGetCardImage("x", TabletCardSide.Front, out _, out _, out var failure));
        Assert.Equal(TabletQueryFailure.NotFound, failure);
        Assert.False(content.TryGetRoleSourcePage("r1", 1, out _, out _));
    }

    private sealed class FakeContent : ITabletContent
    {
        public string StoryTitle => "乙";

        public IReadOnlyList<TabletRole> Roles => [new TabletRole("r1", "角色一")];

        public IReadOnlyList<TabletCatalogItem> Cards => [];

        public IReadOnlyDictionary<string, RoleBookText> RoleBooks => new Dictionary<string, RoleBookText>();

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
            return false;
        }

        public bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType)
        {
            bytes = [];
            contentType = "application/octet-stream";
            return false;
        }
    }
}
