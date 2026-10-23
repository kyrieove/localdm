# tablet-ui-page — 平板网页镜像界面：把 `TabletUiProjection` 画在「公共对话」页签里

## Goal
- 平板「公共对话」页签（`Home.razor` 的 `TabPublic`）改成镜像界面：左栏（宽屏）显示剧本标题、环节列表（已完成/当前/未到三态）、当前环节与提示、七个按钮里 `Visible` 的那些（全部灰色不可点，旁边写 `ControlsNote`）、待放出/已放出材料数、投票汇总、揭秘文本；右侧（或下方）是公开对话；窄屏/竖屏左栏折叠成一行「当前环节」。
- 数据只取自 `ITabletUiSource`（`TabletUiProjection`），不显示备忘、上下文占用。投影为 null 时显示「等待主持人开始这一局」，公开对话照旧显示（`PublicFeed` 来源不变）。
- 底部 `SpeechBar` 不动：能点的仍只有「发送」「停止朗读」「对话」折叠钮。
- 独立探针（合成模式）也能看到这套界面：新增 `SpikeTabletUiSource` 返回一份合成投影。

## Risk
medium — 只加显示，不碰秘密边界（投影已在卡 `tablet-ui-projection` 按高风险验过）。Claude 另用 Playwright 在 1360×1000、834×1112、1112×834、430×932 四个视口截图并断言：灰色按钮全部 `disabled`、没有横向滚动、页面文本不含「备忘」「上下文」。

## Parameters
- `TabletUiProjection` 形状见 `src/LocalDm.Core/Tablet/TabletUiProjection.cs`（`Phases[{PhaseId,Title,Status}]`，`Status` 枚举 `TabletUiPhaseStatus.Done/Current/Upcoming`；`Controls[{Kind,Label,Visible,Enabled}]`；`PhaseIndex` 从 1 起；`VoteSummary` 为 `PublicVoteSummaryView?`，含 `Counts[{Option,Count}]` 与 `TotalVotes`，命名空间 `LocalDm.Core.Domain`；`RevealedTexts` 为 `IReadOnlyList<string>?`）。
- 接口 `LocalDm.Core.Tablet.ITabletUiSource.ReadAsync(long after, bool hostBusy, bool hostSpeaking, CancellationToken)`，已注册为单例（`TabletWebHost.cs` 第 48 行）。页面直接注入它，不走 HTTP（与 `Home.razor` 注入 `IPublicFeedSource` 同一做法）；`/tablet/ui` 接口保留作契约，页面不调用。
- 提示文案原文：`等待主持人开始这一局`；`请在电脑上操作` 来自 `Ui.ControlsNote`，不要在页面里写死。
- 窄屏断点沿用 `reader.css` 现有的 `@media (max-width: 640px)`；竖屏平板（834 宽）也要折叠，用 `@media (max-width: 900px)` 控制左栏变一行。
- 现有探针 `tools/tablet-qr/verify-reader.mjs` 第 82 行会点名为「公共对话」的按钮，并断言 `.public input,.public textarea` 数为 0：页签名字和 `.public` 容器类名都不能改，页签内不能出现输入框。

## Files
may change: `src/LocalDm.Tablet/Pages/LiveBoard.razor`, `src/LocalDm.Tablet/Pages/Home.razor`, `src/LocalDm.Tablet/SpikeTabletUiSource.cs`, `src/LocalDm.Tablet/Program.cs`, `src/LocalDm.Tablet/wwwroot/reader.css` / read-only: `stories/`, `.appdata/`, `.local/`, `src/LocalDm.Core/Tablet/`, `src/LocalDm.Core/Game/`, `src/LocalDm.Core/Domain/`, `src/LocalDm.App/ViewModels/`, `src/LocalDm.App/Views/`, `src/LocalDm.Tablet/Pages/SpeechBar.razor`, `src/LocalDm.Tablet/Pages/RoleReader.razor`, `src/LocalDm.Tablet/TabletWebHost.cs`, `tests/LocalDm.UiTests/TabletUiE2ETests.cs`, `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs`

## Steps (N = 5)
1. 新建 `src/LocalDm.Tablet/SpikeTabletUiSource.cs`（命名空间 `LocalDm.Tablet`，`public sealed class SpikeTabletUiSource(IPublicFeedSource feed) : ITabletUiSource`，一句话注释说明「探针用的合成界面投影，真机由桌面换成真实投影」）。`ReadAsync`：`var snapshot = await feed.ReadAsync(after, cancellationToken);` 然后返回 `new TabletUiProjection { SessionId = snapshot.SessionId, Title = "合成剧本", State = "Playing", Revision = 1, PhaseId = "phase-search", PhaseTitle = "搜证", PhaseIndex = 2, PhaseCount = 4, Hint = "合成提示：把线索卡摆开再讨论。", Phases = [开场 Done, 搜证 Current, 讨论 Upcoming, 揭秘 Upcoming]（`PhaseId` 依次 `phase-open`、`phase-search`、`phase-talk`、`phase-reveal`）, Controls = 七个，按 `TabletUiControlKind` 枚举顺序 Start/Advance/ReleaseMaterials/SubmitVote/Reveal/Complete/PauseResume：Start 不可见 label "开始这一局"；Advance 可见可用 label "进入讨论"；ReleaseMaterials 可见可用 label "发放材料 card-3"；SubmitVote 不可见 label "确认提交汇总"；Reveal 不可见 label "公开真相"；Complete 不可见 label "进入复盘"；PauseResume 可见可用 label "暂停本局"（不可见的 `Enabled = false`）, ControlsNote = "请在电脑上操作", PendingMaterialCount = 2, ReleasedMaterialCount = 1, VoteSummary = null, RevealedTexts = null, Messages = snapshot.Messages, NextSequence = snapshot.NextSequence, HostBusy = hostBusy, HostSpeaking = hostSpeaking }`。顶部 `using LocalDm.Core.Tablet;`。
2. 改 `src/LocalDm.Tablet/Program.cs` 末尾 `TabletWebHost.Create(...)` 调用：在最后一个参数 `content is SpikeTabletContent ? new SpikeTabletSpeech() : null` 后加 `, ui: content is SpikeTabletContent ? new SpikeTabletUiSource(source) : null`（按 `Create` 的参数名 `ui` 传，前面的位置参数不动，`time` 用默认）。写法：`null` 在三元里要让类型可推断，写成 `content is SpikeTabletContent ? new SpikeTabletUiSource(source) : (ITabletUiSource?)null`；同理保持原有 speech 参数那一行不变。
3. 新建 `src/LocalDm.Tablet/Pages/LiveBoard.razor`（非路由组件）：
   ```razor
   @using LocalDm.Core.Domain
   @using LocalDm.Core.Tablet

   <section class="public">
       <h2>公共对话</h2>
       @if (Ui is null)
       {
           <p class="lead board-wait">等待主持人开始这一局</p>
       }
       else
       {
           <div class="board">
               <aside class="board-rail" aria-label="本局进度">
                   <p class="board-oneline">@Ui.PhaseTitle · 第 @Ui.PhaseIndex/@Ui.PhaseCount 环节</p>
                   <div class="board-full">
                       <p class="board-title">@Ui.Title</p>
                       <ol class="board-phases">
                           @foreach (var phase in Ui.Phases)
                           {
                               <li class="@PhaseClass(phase.Status)" aria-current="@(phase.Status == TabletUiPhaseStatus.Current ? "step" : null)">@phase.Title</li>
                           }
                       </ol>
                       @if (!string.IsNullOrWhiteSpace(Ui.Hint))
                       {
                           <p class="board-hint">@Ui.Hint</p>
                       }
                       <div class="board-controls">
                           @foreach (var control in Ui.Controls.Where(c => c.Visible))
                           {
                               <button type="button" class="board-control" disabled>@control.Label</button>
                           }
                           <p class="board-note">@Ui.ControlsNote</p>
                       </div>
                       @if (Ui.PendingMaterialCount > 0 || Ui.ReleasedMaterialCount > 0)
                       {
                           <p class="board-meta">待放出材料 @Ui.PendingMaterialCount · 已放出 @Ui.ReleasedMaterialCount</p>
                       }
                       @if (Ui.VoteSummary is { } vote)
                       {
                           <p class="board-meta">投票汇总：@string.Join("，", vote.Counts.Select(c => $"{c.Option} {c.Count} 票")) · 共 @vote.TotalVotes 票</p>
                       }
                       @if (Ui.RevealedTexts is { Count: > 0 } revealed)
                       {
                           <div class="board-reveal">
                               @foreach (var text in revealed)
                               {
                                   <p>@text</p>
                               }
                           </div>
                       }
                   </div>
               </aside>
               <div class="board-main">
                   @FeedBlock
               </div>
           </div>
       }
       @if (Ui is null)
       {
           @FeedBlock
       }
   </section>

   @code {
       [Parameter] public TabletUiProjection? Ui { get; set; }
       [Parameter] public IReadOnlyList<(string Kind, string Text)> Lines { get; set; } = [];
       [Parameter] public bool FeedConnected { get; set; }
       [Parameter] public bool FeedFailed { get; set; }

       private static string PhaseClass(TabletUiPhaseStatus status) => status switch
       {
           TabletUiPhaseStatus.Done => "done",
           TabletUiPhaseStatus.Current => "current",
           _ => "upcoming",
       };

       private RenderFragment FeedBlock => @<text>
           @if (Lines.Count == 0)
           {
               <p class="lead">@(FeedConnected ? "本局还没有公开消息，电脑上的公开发言会自动显示在这里。" : "电脑尚未接入本局公开对话。")</p>
           }
           @if (FeedFailed)
           {
               <p class="lead">公开对话暂时未同步，正在重试。</p>
           }
           <ol class="feed">
               @foreach (var line in Lines)
               {
                   <li class="@line.Kind">@line.Text</li>
               }
           </ol>
       </text>;
   }
   ```
   （`FeedBlock` 是同一份对话块，Ui 为 null 时放在等待提示下面，不为 null 时放进 `.board-main`；上面的 `@code` 里的 `@<text>` 写法如编译报错，改成把对话块抽成一个小的私有方法组件也可以，但行为不变。）
4. 改 `src/LocalDm.Tablet/Pages/Home.razor`：
   - 顶部加 `@inject ITabletUiSource Ui`（放在 `@inject TabletSpeechService Speech` 后）。
   - 把 `else` 分支里整个 `<section class="public">…</section>`（原第 145–162 行）替换成 `<LiveBoard Ui="_ui" Lines="PublicFeed" FeedConnected="@(_publicSessionId.Length != 0)" FeedFailed="_feedFailed" />`。
   - `@code` 加字段 `private TabletUiProjection? _ui;`；在 `LoadPublicFeedAsync` 的 `catch (Exception) { _feedFailed = true; }` 之后、`// 输入条在所有页签都显示` 注释之前加：
     ```csharp
     // 界面投影只取环节与按钮状态；公开消息已由上面的来源累积，这里不再用 Messages。
     try
     {
         _ui = await Ui.ReadAsync(_publicSequence, Speech.HostBusy, Speech.HostSpeaking, _feedCancellation.Token);
     }
     catch (OperationCanceledException) when (_feedCancellation.IsCancellationRequested) { }
     catch (Exception) { _ui = null; }
     ```
   - 注意字段名 `_ui` 与注入名 `Ui` 区分，不要重名；`@using LocalDm.Core.Tablet` 已在文件顶部。
5. 改 `src/LocalDm.Tablet/wwwroot/reader.css`：在文件末尾（第二版视觉之后、`prefers-color-scheme: dark` 块之前或之后均可，保持与现有分段一致，用一行 `/* ===== 公共对话镜像 ===== */` 起头）加样式，颜色一律用已有变量：
   - `.board { display: grid; grid-template-columns: 260px minmax(0, 1fr); gap: 24px; align-items: start; }`；`.board-main { min-width: 0; }`。
   - `.board-rail { position: sticky; top: 120px; border: 1px solid var(--rule); background: var(--paper); border-radius: 10px; padding: 16px; }`；`.board-oneline { display: none; margin: 0; font: 500 15px/1.6 var(--serif); }`。
   - `.board-title { font: 500 16px/1.5 var(--serif); margin: 0 0 10px; }`；`.board-phases { list-style: none; margin: 0 0 12px; padding: 0; }`；`.board-phases li { padding: 8px 0 8px 12px; border-left: 2px solid var(--rule); color: var(--muted); font-size: 14px; }`；`.board-phases li.done { color: var(--ink-soft); }`；`.board-phases li.current { color: var(--ink); font-weight: 600; border-left-color: var(--seal); }`。
   - `.board-hint, .board-meta { font-size: 13px; color: var(--ink-soft); line-height: 1.8; margin: 0 0 10px; }`；`.board-controls { display: flex; flex-direction: column; gap: 8px; margin: 12px 0; }`；`.board-control { min-height: 44px; border: 1px solid var(--rule); background: var(--paper-deep); color: var(--muted); border-radius: 8px; padding: 0 14px; text-align: left; opacity: 1; cursor: default; }`（覆盖全局 `button:disabled` 的透明度，灰色用背景/文字色表达）；`.board-note { font-size: 12px; color: var(--muted); margin: 0; }`；`.board-reveal { border-top: 1px solid var(--rule); margin-top: 10px; padding-top: 10px; font-size: 14px; line-height: 1.9; }`；`.board-wait { margin-top: 8px; }`。
   - 窄屏/竖屏：`@media (max-width: 900px) { .board { grid-template-columns: 1fr; gap: 12px; } .board-rail { position: static; padding: 10px 14px; } .board-oneline { display: block; } .board-full { display: none; } }`。
   - 注意：`.public .feed li` 现有规则不要动；`.board-main .feed` 沿用它。

## Check
`dotnet build LocalDm.slnx && dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletUiE2ETests" --filter-class "LocalDm.UiTests.TabletSpeechE2ETests" && dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletSpeechTests"`

## Expect
基线 Check 本来就会绿（没有针对页面的自动测试）；页面行为由 Claude 在验收时用 Playwright 单独验证，不算本卡的 Check。

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不改 `SpeechBar.razor`、`RoleReader.razor`、`TabletWebHost.cs`、`src/LocalDm.Core/`、`src/LocalDm.App/`、`tests/`；不显示备忘/上下文占用；不加任何可点的新按钮；页签名「公共对话」与 `.public` 类名不变。
- 不读 `stories/`、`.appdata/`、`.local/`；不删除、不跳过已有测试。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

## Acceptance (draft)
- verdict: review pending (codex quota used up) — TODO accept provisionally
- files: src/LocalDm.Tablet/Pages/Home.razor, src/LocalDm.Tablet/Pages/LiveBoard.razor, src/LocalDm.Tablet/Program.cs, src/LocalDm.Tablet/SpikeTabletUiSource.cs, src/LocalDm.Tablet/wwwroot/reader.css
- check: `dotnet build LocalDm.slnx && dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletUiE2ETests" --filter-class "LocalDm.UiTests.TabletSpeechE2ETests" && dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletSpeechTests"` → exit 0 (run by run-card)
- tokens:
  - round 1: exec custom-local:antigravity/gemini-3.8-flash: fresh 113k, cached 1409k, out 9k  (agy-tablet-ui-page.jsonl) · review gpt-6.1-sol: fresh 0k, cached 0k, out 0k  (codex-review-tablet-ui-page.jsonl)
- review findings: TODO N real / M false positive (scope S, card C, wrong W) / K missed (medium, gpt-6.1-sol)
