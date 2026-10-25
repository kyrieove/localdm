# role-page-flip — 平板角色本原稿页默认盖着，玩家按顺序轻点翻开

## Goal
- 平板「角色本」原稿模式下，没翻开的页显示素笺卡背（「第 N 页 · 轻点翻开」），只有下一页能翻；点了还没轮到的页，卡片晃一下，卡背上写「先翻开第 K 页」。
- 轻点下一页的卡背：卡片 3D 翻转露出原稿图，翻开状态存在服务端会话里（刷新、断线重连都还在；换角色、释放角色、换剧本时清零）。
- 已翻开的原稿图点一下仍然打开全屏查看；全屏查看里只能翻到已翻开的页。卡背不会触发全屏，图片不会触发翻开（两个不同元素）。
- 文字重排模式只显示已翻开页上的文字块。
- 组织者手册（`SourceOnly`）不受影响，照旧全部可见。
- 这是防误看，不是防作弊：服务端接口不加拦截。

## Risk
medium

## Parameters
- 默认双栏：`ReadingSettings.Columns` 已经默认 2，不改。
- 卡背样式：预览稿 `docs/preview/role-flip-20261025/index.html` 里的 `skin-paper`（素笺）与 `.back`、`.ring`、`.seal`、`.no`、`.hint`；翻转动画 `lift`、光泽 `glint`、晃动 `shake`、下一页呼吸光 `breathe`；`prefers-reduced-motion` 时都关掉。
- 翻转时长 0.8s；翻完 1100ms 后清掉「刚翻开」标记。
- 新字段：`TabletSpikeState.SessionEntry.OpenedPages`（int，已翻开的连续页数，初值 0）。

## Files
may change: `src/LocalDm.Tablet/TabletSpikeState.cs`, `src/LocalDm.Tablet/Pages/RoleReader.razor`, `src/LocalDm.Tablet/Pages/Home.razor`, `src/LocalDm.Tablet/wwwroot/reader.css`, `src/LocalDm.Tablet/wwwroot/page-flip.js`, `src/LocalDm.Tablet/App.razor`, `tests/LocalDm.Tests/TabletPageFlipTests.cs` / read-only: `docs/preview/role-flip-20261025/index.html`, `tests/LocalDm.Tests/TabletSpeechTests.cs`

## Steps (N = 5)
1. `src/LocalDm.Tablet/TabletSpikeState.cs`：
   - `SessionEntry` 加 `/// <summary>角色本原稿已按顺序翻开的页数；没翻开的页平板上显示卡背。</summary> public int OpenedPages { get; set; }`。
   - `ResetAll`、`TryClaim`（给 session 设新 RoleId 的那段）、`Release` 里，在 `session.ReadingOffset = 0;` 后面各加 `session.OpenedPages = 0;`。
   - 在 `Release` 之后加：
     ```csharp
     /// <summary>翻开第 page 页：只有紧接着的下一页能翻，返回是否翻开。</summary>
     public bool OpenPage(string key, int page)
     {
         lock (_claimGate)
         {
             var session = Session(key);
             if (session.RoleId is null || page != session.OpenedPages + 1)
             {
                 return false;
             }

             session.OpenedPages = page;
             return true;
         }
     }
     ```
2. `src/LocalDm.Tablet/Pages/RoleReader.razor`：
   - 参数区（`[Parameter] public bool SourceOnly` 附近）加：`[Parameter] public bool FlipPages { get; set; }`、`[Parameter] public int OpenedPages { get; set; }`、`[Parameter] public EventCallback<int> OnOpenPage { get; set; }`；字段 `private int _justOpened;`、`private int _refused;`、`private int _lastOpened = -1;`。
   - 加 `private bool IsCovered(int page) => FlipPages && page > OpenedPages;`。
   - 原稿 `for` 循环里（第 61–65 行）：`IsCovered(page)` 为假时照旧输出 `<img … @onclick="() => OpenViewerAsync(page)" />`，但外面包一层 `<div class="flip-card open @(page == _justOpened ? "flipping" : "")">…<div class="face front">img</div></div>`（仅 `FlipPages` 时包；不翻页时保持原样输出 img）。`IsCovered(page)` 为真时输出：
     ```razor
     <div class="flip-card @(page == OpenedPages + 1 ? "next" : "waiting") @(page == _refused ? "shake" : "")">
         <button type="button" class="face back" @onclick="() => TryOpenAsync(page)" aria-label="翻开第 @(page) 页">
             <span class="seal">阅</span>
             <span class="no">第 @(page) 页</span>
             <span class="hint">@(page == OpenedPages + 1 ? "轻点翻开" : $"先翻开第 {OpenedPages + 1} 页")</span>
         </button>
     </div>
     ```
     盖着的页不输出 img（不请求原稿图）。
   - 加方法：
     ```csharp
     private async Task TryOpenAsync(int page)
     {
         if (page != OpenedPages + 1)
         {
             _refused = 0;
             StateHasChanged();
             await Task.Yield();
             _refused = page;
             return;
         }

         _justOpened = page;
         await OnOpenPage.InvokeAsync(page);
         _ = ClearJustOpenedAsync(page);
     }

     private async Task ClearJustOpenedAsync(int page)
     {
         await Task.Delay(1100);
         if (_disposed || _justOpened != page) return;
         _justOpened = 0;
         await InvokeAsync(StateHasChanged);
     }
     ```
   - `OpenViewerAsync`（第 273 行附近）：`urls` 的页数改成 `FlipPages ? Math.Min(OpenedPages, SourcePageCount) : SourcePageCount`。
   - `Flatten`（第 52 行附近）：`FlipPages` 为真时跳过 `SourcePageNumber > OpenedPages` 的块；章节标题行用的 `chapter.Blocks.FirstOrDefault()?.SourcePageNumber` 大于 `OpenedPages` 时整章跳过。`SourcePageNumber` 为 null 的块保留。
   - `OnParametersSet`：在现有分支之后加 `if (OpenedPages != _lastOpened) { if (_lastOpened >= 0) _needsPagination = true; _lastOpened = OpenedPages; }`。
3. `src/LocalDm.Tablet/Pages/Home.razor` 第 122–123 行角色本那个 `<RoleReader>`：加 `FlipPages="true" OpenedPages="session.OpenedPages" OnOpenPage="OpenPageCallback"`；在 `ProgressCallback` 附近加：
   ```csharp
   private EventCallback<int> OpenPageCallback =>
       EventCallback.Factory.Create<int>(this, page => { State.OpenPage(_sessionKey, page); });
   ```
   手册那个 `<RoleReader>`（第 128 行）不加。
4. `src/LocalDm.Tablet/wwwroot/reader.css` 文件末尾加一节 `/* ===== 角色本翻页：原稿默认盖着，按顺序轻点翻开 ===== */`：
   - `.source-pages .flip-card { position: relative; perspective: 2000px; }` 内部 `.flip-card` 变换容器用 `transform-style: preserve-3d`；`.face { backface-visibility: hidden; -webkit-backface-visibility: hidden; border-radius: 10px; }`。
   - 盖着的页：`.flip-card .back` 占满原稿位置，高度用 `aspect-ratio: 1 / 1.414`（A4 竖版），宽度 100%；样式照抄预览稿的 `.skin-paper .back`（颜色用本文件已有的 `--paper-deep`、`--rule`、`--seal` 变量）、`.seal`（圆框里一个字）、`.no`、`.hint`；`border: 0; width: 100%; cursor: pointer; font: inherit;`。
   - 翻开动画：`.flip-card.flipping .face.front { animation: flip-in .8s cubic-bezier(.22,.9,.25,1); }`，`@keyframes flip-in { 0% { transform: rotateY(180deg) translateZ(0) scale(1); } 55% { transform: rotateY(85deg) translateZ(80px) scale(1.05); } 100% { transform: rotateY(0) translateZ(0) scale(1); } }`；光泽照抄预览稿的 `::after` + `glint`（挂在 `.flip-card.flipping .face.front` 上，`position: relative` 由 `.face.front` 提供）。
   - `.flip-card.waiting .back { filter: saturate(.35) brightness(.92); cursor: not-allowed; }`；`.flip-card.shake { animation: shake .42s; }`；`.flip-card.next .back { animation: breathe 2.4s ease-in-out infinite; }`，keyframes 照抄预览稿。
   - `@media (prefers-reduced-motion: reduce)` 里把 `flip-in`、`glint`、`shake`、`breathe` 动画都设为 `none`。
   - 不改本文件已有的类名和规则。
5. 新建 `tests/LocalDm.Tests/TabletPageFlipTests.cs`：照 `TabletSpeechTests.cs` 第 110–115 行建 `TabletSpikeState` 的方式建 state，认领一个角色（`state.TryClaim("k", <content 里第一个角色的 RoleId>)`），然后断言：
   - `state.OpenPage("k", 2)` 为 false，`OpenedPages == 0`；
   - `state.OpenPage("k", 1)` 为 true，再 `OpenPage("k", 2)` 为 true，`OpenedPages == 2`；
   - `state.OpenPage("k", 2)`（重复）为 false；
   - `state.Release("k")` 后 `OpenedPages == 0`，且未认领时 `OpenPage("k", 1)` 为 false。

## Check
```
dotnet build LocalDm.slnx && dotnet test tests/LocalDm.Tests --filter "FullyQualifiedName~TabletPageFlipTests|FullyQualifiedName~TabletAppTests|FullyQualifiedName~TabletPackageModeTests"
```

## Don't
- No commits, no other files, no refactors beyond the steps.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- Reuse existing functions; don't copy existing logic.
- No rm, no git push, no git checkout . / restore . / reset --hard.
- 不改任何 HTTP 接口（`TabletWebHost.cs` 不动）；不改组织者手册的阅读方式。
- 不读取、不打印 `stories/`、`.appdata/`、`.local/` 下的文件。

## Fix 1
两件事：A 修审查 P1（未翻开章节的标题从目录和页脚漏出）；B 用户改主意，翻开从「轻点」改成「长按约 0.65 秒、圆环走满才翻开」。

### Steps (N = 6)
1. `src/LocalDm.Tablet/Pages/RoleReader.razor`：加
   ```csharp
   private bool IsChapterVisible(RoleBookChapter chapter) =>
       !FlipPages || !(chapter.Blocks.FirstOrDefault()?.SourcePageNumber > OpenedPages);

   private IReadOnlyList<RoleBookChapter> VisibleChapters =>
       Book is null ? [] : Book.Chapters.Where(IsChapterVisible).ToArray();
   ```
   `Flatten` 里章节跳过的那段 `if (FlipPages && chapterPage > OpenedPages) continue;` 改成 `if (!IsChapterVisible(chapter)) continue;`（`chapterPage` 变量仍用于 `sourcePageNumber`）。
2. 同文件章节目录 `<nav class="reader-chapters">`（第 98–103 行附近）：循环改成遍历 `VisibleChapters`（`i < VisibleChapters.Count`、`@VisibleChapters[i].Title`）。`SyncStateAsync`（第 262 行附近）：`Book.Chapters.Count` / `Book.Chapters[_chapter].Title` 改成 `VisibleChapters`；`_chapter` 不在 `VisibleChapters` 范围内时把 `_chapterLabel`、`_chapterTitle` 都设成 `""`。
3. 同文件下一页的卡背（`IsCovered(page)` 且 `page == OpenedPages + 1`）：删掉这个按钮的 `@onclick`，加属性 `data-flip-page="@page"`，hint 文字改成「按住翻开」，把 `<span class="seal">阅</span>` 换成
   ```razor
   <span class="ring"><svg viewBox="0 0 86 86" aria-hidden="true"><circle class="track" cx="43" cy="43" r="40" /><circle class="fill" cx="43" cy="43" r="40" /></svg><span class="seal">阅</span></span>
   ```
   还没轮到的页（`waiting`）保留 `@onclick="() => TryOpenAsync(page)"`（点了晃一下），不加 `data-flip-page`。两种卡背可以拆成两个 `if` 分支输出。
   加方法：`[JSInvokable] public Task HoldOpen(int page) => InvokeAsync(() => TryOpenAsync(page));`
   `OnAfterRenderAsync` 方法最开头（在 `if (IsSourceMode || _disposed) return;` 之前）加：
   ```csharp
   if (firstRender && FlipPages)
   {
       _reference ??= DotNetObjectReference.Create(this);
       await Js.InvokeVoidAsync("tabletFlip.attach", _reference);
   }
   ```
   `DisposeAsync` 里在 `tabletViewer.close` 那行后面加：`if (FlipPages) { try { await Js.InvokeVoidAsync("tabletFlip.detach"); } catch (JSDisconnectedException) { } }`。
4. 新建 `src/LocalDm.Tablet/wwwroot/page-flip.js`（内容逐字）：
   ```js
   // 角色本翻页：按住约 0.65 秒、圆环走满才翻开，避免手滑多翻一页。计时在浏览器里做，服务端渲染的往返延迟不影响手感。
   (() => {
       const HOLD_MS = 650;
       const FULL = 251;
       let dotnet = null;

       document.addEventListener("pointerdown", event => {
           const back = event.target.closest("[data-flip-page]");
           if (!back || !dotnet) return;
           const fill = back.querySelector(".fill");
           const page = Number(back.dataset.flipPage);
           const started = performance.now();
           let frame = 0;
           let done = false;
           const tick = now => {
               if (fill) fill.style.strokeDashoffset = FULL * (1 - Math.min(1, (now - started) / HOLD_MS));
               frame = requestAnimationFrame(tick);
           };
           const stop = () => {
               clearTimeout(timer);
               cancelAnimationFrame(frame);
               if (!done && fill) fill.style.strokeDashoffset = FULL;
               ["pointerup", "pointercancel", "pointerleave"].forEach(type => back.removeEventListener(type, stop));
           };
           const timer = setTimeout(() => {
               done = true;
               stop();
               if (navigator.vibrate) navigator.vibrate(12);
               dotnet.invokeMethodAsync("HoldOpen", page);
           }, HOLD_MS);
           frame = requestAnimationFrame(tick);
           ["pointerup", "pointercancel", "pointerleave"].forEach(type => back.addEventListener(type, stop));
       });

       // 长按时别弹出系统菜单。
       document.addEventListener("contextmenu", event => {
           if (event.target.closest("[data-flip-page]")) event.preventDefault();
       });

       window.tabletFlip = {
           attach: reference => { dotnet = reference; },
           detach: () => { dotnet = null; },
       };
   })();
   ```
   `src/LocalDm.Tablet/App.razor` 第 26 行 `<script src="photo-viewer.js"></script>` 后面加一行 `<script src="page-flip.js"></script>`。
5. `src/LocalDm.Tablet/wwwroot/reader.css` 在上一轮加的「角色本翻页」一节末尾（`prefers-reduced-motion` 块之前）加：
   ```css
   .flip-card .back { -webkit-touch-callout: none; -webkit-user-select: none; user-select: none; touch-action: manipulation; }
   .flip-card .back .ring { position: relative; width: 86px; height: 86px; display: block; }
   .flip-card .back .ring svg { position: absolute; inset: 0; transform: rotate(-90deg); }
   .flip-card .back .ring circle { fill: none; stroke-width: 4; }
   .flip-card .back .ring .track { stroke: rgba(148, 63, 50, .2); }
   .flip-card .back .ring .fill { stroke: var(--seal); stroke-dasharray: 251; stroke-dashoffset: 251; }
   .flip-card .back .ring .seal { position: absolute; inset: 12px; }
   ```
   如果上一轮的 `.seal` 规则用了 `width`/`height` 固定尺寸，`.ring .seal` 里再加 `width: auto; height: auto;` 让它跟着 inset 走。
6. 不改 `TabletPageFlipTests.cs`（审查 P2 的重复夹具记为 deferred）。

### Files
may change: `src/LocalDm.Tablet/Pages/RoleReader.razor`, `src/LocalDm.Tablet/wwwroot/page-flip.js`, `src/LocalDm.Tablet/App.razor`, `src/LocalDm.Tablet/wwwroot/reader.css`

## Acceptance
- verdict: accepted
- files: src/LocalDm.Tablet/App.razor, src/LocalDm.Tablet/Pages/Home.razor, src/LocalDm.Tablet/Pages/RoleReader.razor, src/LocalDm.Tablet/TabletSpikeState.cs, src/LocalDm.Tablet/wwwroot/page-flip.js, src/LocalDm.Tablet/wwwroot/reader.css, tests/LocalDm.Tests/TabletPageFlipTests.cs
- check: `dotnet build LocalDm.slnx && dotnet test tests/LocalDm.Tests --filter "FullyQualifiedName~TabletPageFlipTests|FullyQualifiedName~TabletAppTests|FullyQualifiedName~TabletPackageModeTests"` → exit 0 (run by run-card)
- tokens:
  - round 1: exec antigravity/gemini-3.8-flash: fresh 197k, cached 1192k, out 26k  (agy-role-page-flip.jsonl) · review gpt-6.1-sol: fresh 47k, cached 161k, out 2k  (codex-review-role-page-flip.jsonl)
  - Fix 1: exec antigravity/gemini-3.8-flash: fresh 149k, cached 796k, out 19k  (agy-role-page-flip.jsonl)
- review findings: 1 real / 0 false positive / 0 missed (medium, gpt-6.1-sol)
