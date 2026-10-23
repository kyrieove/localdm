# tablet-photo-viewer — 平板原稿和线索卡点开后可以像相册一样全屏看

## Goal
- 在平板的「我的角色本」原稿模式、「组织者手册」页签、线索卡大图上，点一张图就进入黑底全屏查看器。
- 全屏里，原稿可以左右滑动或点屏幕边缘，在整本之间连续翻页；线索卡只有一张，不显示箭头。
- 支持捏合缩放、双击放大和复原、放大后拖动。翻页时保持当前放大倍数，新的一页停在左上角。
- 可以用 ✕ 或浏览器返回键关闭。关闭后，阅读器跳到最后看的那页所在的一屏，进度也会写回（角色本会存下来，手册只留在内存里）。
- 全屏期间：
  - 主持人或系统发来新的公开消息，顶部会出现横幅。
  - 翻到第一页或最后一页时，画面回弹，并提示「已是第一页」或「已是最后一页」。
  - 先预加载当前页前后各一页，还没加载完的页显示「加载中…」。
  - 尝试让浏览器真全屏，并尝试保持屏幕常亮；浏览器不支持就静默跳过。
- 嵌入模式原有的「整页 / 适合宽度」和 ＋/− 按钮保持不变。

## Risk
medium — 普通前端功能，加一个纯函数（按页码算该显示哪一屏）的单元测试。

## Parameters
- 交互参照：`docs/preview/fullscreen-viewer.html` 第 161–300 行（用户已试过并认可）。新代码按它的逻辑移植，再加下面这些改动。
- 缩放范围 1–5；双击放大到 2.5 倍；两次点击间隔 < 320ms、距离 < 30px 才算双击。
- 滑动翻页的阈值：拖动距离超过画面宽度的 0.18，或者 300ms 内拖动超过 40px。
- 点屏幕边缘翻页：
  - 单击落在画面左侧 20% 内翻到上一页，右侧 20% 内翻到下一页，中间区域不翻页。
  - 单击要等 320ms，确认不是双击之后才执行。
  - 线索卡（`single`）没有这个功能。
- 到头的提示文字：`已是第一页` / `已是最后一页`，显示 1.5 秒，同时画面回弹。
- 新消息横幅：
  - 只显示主持人回复（`host`）和系统事件（`event`），不显示玩家发言。
  - 内容是最新一条消息的文字，超过 40 字截断并加 `…`。
  - 显示 6 秒；点横幅会关闭全屏查看器。
- 顶部页码：`第 {i+1} / {n} 页`；线索卡显示 `线索卡`。
- 预加载：当前页前后各一页（`i-1`、`i+1`）。只给这三页的 `<img>` 设置 `src`，其余页先留空，翻到附近时再设置。未加载完时显示 `加载中…`。
- 真全屏：
  - 打开时，如果 `document.fullscreenEnabled` 为真，调用 `document.documentElement.requestFullscreen()`，并 `.catch(() => {})`。
  - 关闭时，如果 `document.fullscreenElement` 存在，调用 `document.exitFullscreen().catch(() => {})`。
- 保持常亮：
  - 只在 `'wakeLock' in navigator && window.isSecureContext` 时，执行 `navigator.wakeLock.request('screen')`，失败就忽略。
  - 关闭时 `release()`。
- 浏览器返回键：打开时执行 `history.pushState({ tabletViewer: 1 }, '', location.href)`；收到 `popstate` 时关闭。
- 关闭后显示哪一屏，用纯函数 `SourceSpread.StartFor(currentStart, page, columns, pageCount)` 计算：
  - 先算 `start = currentStart + floorDiv(page - currentStart, columns) * columns`，`floorDiv` 对负数向下取整。
  - 再把 `start` 限制在 `1..max(1, pageCount)` 之间。
  - 例子：`(1,4,2,12)→3`；`(3,1,2,12)→1`；`(2,1,2,12)→0→1`；`(2,5,2,12)→4`；`(5,5,1,12)→5`；`(1,12,2,12)→11`。

## Files
may change: `src/LocalDm.Tablet/wwwroot/photo-viewer.js`, `src/LocalDm.Tablet/wwwroot/reader.css`, `src/LocalDm.Tablet/App.razor`, `src/LocalDm.Tablet/Pages/RoleReader.razor`, `src/LocalDm.Tablet/Pages/Home.razor`, `src/LocalDm.Core/Tablet/SourceSpread.cs`, `tests/LocalDm.Tests/SourceSpreadTests.cs`
read-only: `stories/`, `.local/`, `docs/preview/`

## Steps (N = 7)
1. 新建 `src/LocalDm.Core/Tablet/SourceSpread.cs`。
   - 命名空间 `LocalDm.Core.Tablet`，类 `public static class SourceSpread`。
   - 方法 `public static int StartFor(int currentStart, int page, int columns, int pageCount)`，按 Parameters 里的公式实现。
   - `columns < 1` 时按 1 算。
   - 写一行中文 XML 注释：「全屏看完某页后，阅读器应该从哪一页开始显示这一屏；保持原来的分屏对齐」。
2. 新建 `tests/LocalDm.Tests/SourceSpreadTests.cs`。
   - 用 xUnit `[Theory]` 加 `[InlineData]`，覆盖 Parameters 里的 6 个例子。
   - 再加一条：`pageCount = 0` 时返回 1。
3. 新建 `src/LocalDm.Tablet/wwwroot/photo-viewer.js`。
   - 暴露 `window.tabletViewer = { open(urls, index, single, dotnetRef), close(), notify(text) }`。
   - DOM 在第一次 `open` 时创建一次，`append` 到 `document.body`，不放进 Blazor 管的节点里，所以每 2 秒的重渲染不会碰到它。结构参照预览页的 `#fs`、`#stage`、`#track`、`✕`、左右箭头和页码。类名全部加前缀 `pv-`。
   - 手势和缩放从预览页第 161–300 行移植：`applyTrack`、`clampPan`、`applyZoom`、`zoomAt`、`go`、pointer 事件、滚轮、键盘（Esc / ← / →）、resize。移植时做下面这些改动：
     a. `go(d)`：
        - 不再调用 `resetZoom()`，保留 `V.s`。
        - 把 `V.tx`、`V.ty` 设成让新页左上角对齐画面左上角的值：`V.tx = 最大可平移 x`，`V.ty = 最大可平移 y`，用 `clampPan` 同样的公式算，正值表示往右下移。放大倍数为 1 时两者都是 0。
        - 越界时（`j < 0` 或 `j >= n`，或者是 `single`）回弹，并显示到头提示（线索卡不提示）。
     b. 单击（没有移动的 pointerup）：
        - 记下这次单击，用 `setTimeout` 等 320ms。
        - 期间来了第二次点击，就取消计时器，按双击处理，逻辑同预览页。
        - 计时到了，就按落点判断：x 在 stage 左侧 20% 内调 `go(-1)`，右侧 20% 内调 `go(1)`。`single` 时什么都不做。
     c. 预加载：每张 slide 的 `<img>` 起初只有 `data-src`。`updateUi` 时给 `i-1..i+1` 设置 `src`。每张 slide 里放一个 `<span class="pv-loading">加载中…</span>`，在 img 的 `load` 和 `error` 事件里隐藏它。
     d. `open`：
        - 依次执行：记下 `dotnetRef`（可以为 null）、建 slide、`pushState`、`requestFullscreen`、wakeLock、`document.body.style.overflow = 'hidden'`。
        - `alt`：`single` 时为 `线索卡`，否则为 `原稿第 k 页`。
     e. 关闭：
        - 内部的 `closeUi()`：隐藏 DOM、恢复 overflow、退出真全屏、释放 wakeLock。如果有 `dotnetRef`，执行 `dotnetRef.invokeMethodAsync('ViewerClosed', V.i + 1).catch(() => {})`，然后把 `dotnetRef` 置空。
        - `requestClose()`：`history.state?.tabletViewer` 存在就 `history.back()`，否则直接 `closeUi()`。
        - ✕ 和 Esc 走 `requestClose`。
        - 对外的 `close()`（组件销毁时调用）：先把 `dotnetRef` 置空，再走 `requestClose()`，不回调 .NET。
     f. `notify(text)`：
        - 查看器没打开就直接返回。
        - 否则在顶部显示 `.pv-banner`，文字按 Parameters 截断，6 秒后隐藏；再来新消息就重新计时。
        - 点横幅调用 `requestClose()`。
   - 文件开头写一行中文注释，说明这个文件是平板原稿和线索卡的全屏查看器，交互来自 `docs/preview/fullscreen-viewer.html`。
4. `src/LocalDm.Tablet/wwwroot/reader.css` 末尾追加一段 `/* 全屏查看器 */`。
   - 样式从预览页 `<style>` 里 `#fs` 相关的规则移植，选择器改成 `pv-` 前缀。
   - 新增 `.pv-banner` 和 `.pv-toast`：顶部居中，半透明深底，白字，圆角。
   - 新增 `.pv-loading`：居中，灰字。
   - 嵌入模式下让人知道能点：`.source-pages img, .cards-view .shot img { cursor: zoom-in; }`。
5. `src/LocalDm.Tablet/App.razor`：在 `<script src="tablet-shell.js"></script>` 后面加一行 `<script src="photo-viewer.js"></script>`。
6. `src/LocalDm.Tablet/Pages/RoleReader.razor`：
   - 第 63 行的 `<img>` 加 `@onclick="() => OpenViewerAsync(pg)"`，写法为 `var page = pg;` 再 `@onclick="() => OpenViewerAsync(page)"`，避免循环变量被闭包捕获后都变成最后一个值。
   - `@code` 里新增 `private async Task OpenViewerAsync(int page)`：
     - `_reference ??= DotNetObjectReference.Create(this);`
     - 地址数组是 `Enumerable.Range(1, SourcePageCount).Select(p => SourceUrlPrefix + p).ToArray()`。和嵌入模式用的是同一个前缀，权限也和嵌入模式一样，不要另外拼地址。
     - 调用 `await Js.InvokeVoidAsync("tabletViewer.open", urls, page - 1, false, _reference);`
   - 新增 `[JSInvokable] public Task ViewerClosed(int page)`：
     - 用 `InvokeAsync` 包住，在里面算 `var start = SourceSpread.StartFor(SourcePage, page, _activeSettings.Columns, SourcePageCount);`。
     - `start != SourcePage` 时，照 `Turn` 的源码分支：设置 `_activeSettings = _activeSettings with { SourcePageNumber = start }`，然后 `await OnProgress.InvokeAsync(new ReadingProgressUpdate(_offset, _activeSettings));`。
     - 最后 `StateHasChanged()`。
   - `DisposeAsync` 在释放 `_reference` 之前，`try { await Js.InvokeVoidAsync("tabletViewer.close"); } catch (JSDisconnectedException) { }`。注意 `_mounted` 为 false 时也要关。
7. `src/LocalDm.Tablet/Pages/Home.razor`：
   - 第 166 行线索卡的 `<img>` 加 `@onclick="() => OpenCardAsync(session.SelectedCardId!)"`。
   - 新增方法 `private Task OpenCardAsync(string cardId) => Js.InvokeVoidAsync("tabletViewer.open", new[] { $"/tablet/cards/{Uri.EscapeDataString(cardId)}/front" }, 0, true, null).AsTask();`。
   - 横幅：
     - `LoadPublicFeedAsync` 加一个参数 `bool notify = false`，`PollPublicFeedAsync` 里调用时传 `notify: true`。`OnInitializedAsync` 那次（可能在预渲染阶段）不传，所以不会在预渲染时调 JS。
     - 在 `PublicFeed.AddRange(...)` 之前，把这次新增的消息先取到一个局部变量 `fresh`，再 `AddRange(fresh)`。
     - `notify` 为真时，找 `fresh` 里最后一条 `Kind` 是 `host` 或 `event` 的消息。找到就执行 `try { await Js.InvokeVoidAsync("tabletViewer.notify", text); } catch (JSDisconnectedException) { } catch (InvalidOperationException) { }`。
     - 被清空重读的那一批（换了会话）不通知。

## Check
```
dotnet build LocalDm.slnx && dotnet test tests/LocalDm.Tests && node --check src/LocalDm.Tablet/wwwroot/photo-viewer.js
```

## Don't
- No commits, no other files, no refactors beyond the steps.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- Reuse existing functions; don't copy existing logic.
- No rm, no git push, no git checkout . / restore . / reset --hard.
- 不引入任何第三方库或 CDN；不改 `docs/preview/` 下的预览页；不改嵌入模式现有的缩放 / 整页按钮行为。
- 不读取 `stories/`、`.appdata/`、`.local/`。

## Acceptance
- verdict: accepted
- files: src/LocalDm.Core/Tablet/SourceSpread.cs, src/LocalDm.Tablet/App.razor, src/LocalDm.Tablet/Pages/Home.razor, src/LocalDm.Tablet/Pages/RoleReader.razor, src/LocalDm.Tablet/wwwroot/photo-viewer.js, src/LocalDm.Tablet/wwwroot/reader.css, tests/LocalDm.Tests/SourceSpreadTests.cs
- check: `dotnet build LocalDm.slnx && dotnet test tests/LocalDm.Tests && node --check src/LocalDm.Tablet/wwwroot/photo-viewer.js` → exit 1 (run by run-card)
- tokens:
  - round 1: exec custom-local:antigravity/gemini-3.8-flash: fresh 219k, cached 2815k, out 36k  (agy-tablet-photo-viewer.jsonl)
- review findings: 3 real / 0 false positive / 1 missed (medium, no review)
- claude fix: RoleReader.razor 文字里的 `@page` 被 Razor 当成指令，改成 `@(page)`（编译错误 RZ9979）
- claude fix: 按 sol 审查改 photo-viewer.js 三处——放大翻到未加载页时图到后重算左上角平移；关闭时隐藏横幅和到头提示；关闭后才回来的 wakeLock 立刻释放
- env: 工作树缺不入库的 fixtures/synthetic/.private/expected.json，用 scripts/sync-local-fixtures.ps1 补齐后 Check 通过
- claude fix: 浏览器实测发现返回键触发 Blazor 页面合并，把挂在 body 上的查看器节点摘掉，第二次点图打不开；`ensureDom` 发现节点脱离页面就挂回去
- browser check (127.0.0.1:5099 合成剧本): 点原稿进全屏「第 2 / 3 页」→ › 到第 3 页 → 返回键关闭，阅读器停在「第 3 / 3 页」；再点能重新打开；✕、Esc 能关；线索卡全屏标题「线索卡」且不显示箭头。未验：新消息横幅、捏合、点边缘翻页、真全屏 / 常亮（需真平板）
