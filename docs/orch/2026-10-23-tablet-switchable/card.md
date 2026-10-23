# tablet-switchable — 平板网页内容可热切换：等待页、材料准备中页、换剧本清空认领

## Goal
- 平板服务可以先起、后装剧本：没剧本时平板首页显示「等待主持人选剧本」，准备材料时显示「材料准备中」，装好后自动变成选角色页，不需要刷新。
- 换剧本（或回到没剧本）时，所有设备的角色认领、所选卡、阅读位置清空，页面自动回到选角色页或等待页。
- 现有的合成探针和真实包模式行为不变（它们的内容始终是 Ready）。

## Risk
medium

## Parameters
- `TabletStoryState` 三个值：`None`、`Preparing`、`Ready`。
- 非 Ready 时：`Roles` 空、`Cards` 空、`RoleBooks` 空字典，取图/取原稿页一律失败（`TabletQueryFailure.NotFound`），`StoryTitle` 用最近一次给的标题（没有则空串）。
- 等待页文案：标题「等待主持人选剧本」，说明「电脑一打开剧本，这里会自动变成选角色页，不用刷新。」；准备中页：标题「材料准备中」，说明「《{标题}》的卡片正在准备，几秒后自动开始。」。

## Files
may change: `src/LocalDm.Tablet/ITabletContent.cs`, `src/LocalDm.Tablet/SwitchableTabletContent.cs`, `src/LocalDm.Tablet/TabletSpikeState.cs`, `src/LocalDm.Tablet/Pages/Home.razor`, `src/LocalDm.Tablet/wwwroot/reader.css`, `tests/LocalDm.Tests/TabletSwitchableTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 6)
1. `src/LocalDm.Tablet/ITabletContent.cs`：在接口 `ITabletContent` 里 `string StoryTitle { get; }` 之后加一行 `TabletStoryState StoryState => TabletStoryState.Ready;`（接口默认实现，现有两个实现类不用改）。
2. 新建 `src/LocalDm.Tablet/SwitchableTabletContent.cs`，原样写入：
```csharp
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
```
3. `src/LocalDm.Tablet/TabletSpikeState.cs`：把这一行 `    public TabletSpikeState(ITabletContent content) => Content = content;` 整体替换为：
```csharp
    public TabletSpikeState(ITabletContent content)
    {
        Content = content;
        if (content is SwitchableTabletContent switchable)
        {
            // 换剧本：所有设备的认领与阅读状态一并清掉，页面自动回到选角色页或等待页。
            switchable.Changed += () =>
            {
                ResetAll();
                TabletSessions.RaiseChanged();
            };
        }
    }

    /// <summary>当前剧本状态；合成探针与真实包始终是 Ready。</summary>
    public TabletStoryState StoryState => Content.StoryState;

    private void ResetAll()
    {
        lock (_claimGate)
        {
            foreach (var session in _sessions.Values)
            {
                session.RoleId = null;
                session.SelectedCardId = null;
                session.ReadingOffset = 0;
                session.Settings = session.Settings with { SourcePageNumber = 1 };
            }
        }
    }
```
4. `src/LocalDm.Tablet/Pages/Home.razor`：文件里有两处含 `session.RoleId`：header 里的 `@if (session.RoleId is not null)`（不要动）和 `</header>` 之后的这一行 `    @if (session.RoleId is null)`（紧接着是 `{` 和 `<section class="join">`）。把后者那一行替换成下面这段（后面原有的 `{ <section class="join"> ... }` 与 `else { <div class="workspace"> ... }` 保持不动）：
```razor
    @if (State.StoryState == TabletStoryState.None)
    {
        <section class="wait">
            <div class="pulse" aria-hidden="true"></div>
            <h1>等待主持人选剧本</h1>
            <p class="lead">电脑一打开剧本，这里会自动变成选角色页，不用刷新。</p>
        </section>
    }
    else if (State.StoryState == TabletStoryState.Preparing)
    {
        <section class="wait">
            <div class="pulse" aria-hidden="true"></div>
            <h1>材料准备中</h1>
            <p class="lead">《@State.Content.StoryTitle》的卡片正在准备，几秒后自动开始。</p>
        </section>
    }
    else if (session.RoleId is null)
```
并在文件顶部 `@using LocalDm.Core.Tablet` 之后加一行 `@using LocalDm.Tablet`（文件里还没有这行时才加）。
5. `src/LocalDm.Tablet/wwwroot/reader.css` 末尾追加：
```css
/* ===== 等待页 / 准备中页 ===== */
.wait { max-width: 520px; margin: 0 auto; padding: 72px 24px; text-align: center; }
.wait h1 { font: 500 26px/1.4 var(--serif); margin: 0 0 8px; }
.wait .lead { margin: 0 auto; }
.wait .pulse { width: 54px; height: 54px; border-radius: 50%; margin: 0 auto 20px; border: 2px solid var(--seal); animation: wait-pulse 1.8s ease-in-out infinite; }
@keyframes wait-pulse { 0%, 100% { transform: scale(.85); opacity: .4; } 50% { transform: scale(1.1); opacity: 1; } }
@media (prefers-reduced-motion: reduce) { .wait .pulse { animation: none; } }
```
6. 新建 `tests/LocalDm.Tests/TabletSwitchableTests.cs`（命名空间 `LocalDm.Tests`，`using LocalDm.Core.Tablet; using LocalDm.Tablet;`），一个 `public sealed class TabletSwitchableTests`、两个 `[Fact]`：
   - `Switching_content_clears_every_claim_and_reports_state`：`var content = new SwitchableTabletContent(); var state = new TabletSpikeState(content);` 断言 `state.StoryState == TabletStoryState.None` 且 `state.Roles` 为空；`content.SetPreparing("甲")` 后状态是 `Preparing`、`content.StoryTitle == "甲"`；用本文件里定义的私有嵌套类 `FakeContent : ITabletContent`（`StoryTitle => "乙"`，`Roles => [new TabletRole("r1", "角色一")]`，`Cards => []`，`RoleBooks => new Dictionary<string, RoleBookText>()`，两个 TryGet 方法给 out 参数赋值 `[]`、`"application/octet-stream"`、`TabletQueryFailure.NotFound` 并返回 false）调用 `content.Set(new FakeContent())` 后状态是 `Ready`，`state.TryClaim("k1", "r1")` 为 true，`state.ClaimedRoles` 含 `"r1"`；再调用 `content.SetNone()`，断言 `state.ClaimedRoles` 为空、`state.Session("k1").RoleId` 为 null、`state.StoryState == TabletStoryState.None`。
   - `Empty_content_never_serves_images`：新建 `SwitchableTabletContent`，`TryGetCardImage("x", TabletCardSide.Front, out _, out _, out var failure)` 返回 false 且 `failure == TabletQueryFailure.NotFound`；`TryGetRoleSourcePage("r1", 1, out _, out _)` 返回 false。

## Check
`dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletSwitchableTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.
