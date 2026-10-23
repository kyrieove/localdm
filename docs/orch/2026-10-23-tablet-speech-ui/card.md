# tablet-speech-ui — 平板网页底部输入条：可展开对话面板、发送、停止朗读

## Goal
- 平板网页（`Home.razor`）底部固定一条输入条，**所有页面状态下都显示**（选角色页、角色本、线索卡、公共对话）：一个文本框、一个「发送」按钮、字数 `n/300`、一个提示行；在朗读时多一个「停止朗读」按钮；可展开一个对话面板，显示最近 8 条公开消息（在「公共对话」页签里不显示展开键，因为整页就是对话）。
- 状态规则：没认领角色 → 发送键灰掉、提示「请先认领角色」；主持忙 → 发送键灰掉、提示「主持正在回应」；发送后服务返回的文案（`TabletSayResult.Message`）显示在提示行（错误用印章红，成功用墨灰）；成功发送后清空文本框。
- 页面上的「公共对话」页签说明文字不再写「只读…」。
- 独立探针（合成模式）有一个演示用的 `SpikeTabletSpeech`，使条能在探针里点通：接受任何发言，之后「主持忙」3 秒。

## Risk
medium

## Parameters
- 文案（原样）：占位符 `对大家说点什么…`；按钮 `发送`、`停止朗读`；展开键 `对话` / 收起 `收起`；提示 `请先认领角色`、`主持正在回应`；字数上限用 `TabletSpeechService.MaxLength`（300）；文本框 `maxlength="300"`。
- 发送快捷键：Enter 发送，Shift+Enter 换行（`@onkeydown` 判断 `e.Key == "Enter" && !e.ShiftKey`，并 `@onkeydown:preventDefault` 只在这个条件下——Blazor 的 `preventDefault` 是整体开关，做法见 Steps）。
- 主持忙状态来源：`TabletSpeechService.HostBusy`、`HostSpeaking`；页面每次轮询公开消息（现有 2 秒的 `PollPublicFeedAsync`）时一并刷新重画。
- 样式：只加到 `src/LocalDm.Tablet/wwwroot/reader.css`（用文件里已有的 `--paper`、`--paper-deep`、`--ink`、`--ink-soft`、`--rule`、`--seal`），不动 `custom.css`（那是用户可编辑的覆盖层）。输入条 `position: sticky; bottom: 0`，竖屏窄宽度下单列排布，按钮高度不小于 44px（触控）。
- 来源：设计 `docs/plans/2026-10-23-tablet-live-chat.md` 第一期第 1–5、8 条；服务接口见上一张卡 `src/LocalDm.Tablet/TabletSpeechService.cs`（以仓库里的文件为准）。

## Files
may change: `src/LocalDm.Tablet/Pages/Home.razor`, `src/LocalDm.Tablet/Pages/SpeechBar.razor`, `src/LocalDm.Tablet/wwwroot/reader.css`, `src/LocalDm.Tablet/SpikeTabletSpeech.cs`, `src/LocalDm.Tablet/Program.cs`, `tests/LocalDm.Tests/TabletSpeechTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 5)
1. 新建 `src/LocalDm.Tablet/Pages/SpeechBar.razor`（无 `@page`；顶部 `@using LocalDm.Core.Tablet`、`@using LocalDm.Tablet`、`@inject TabletSpeechService Speech`）。参数：`[Parameter] public string SessionKey`、`[Parameter] public bool HasRole`、`[Parameter] public bool HostBusy`、`[Parameter] public bool HostSpeaking`、`[Parameter] public bool ShowPanelToggle`（默认 true）、`[Parameter] public IReadOnlyList<(string Kind, string Text)> RecentLines`（面板用，调用方传最近 8 条）。结构：
   - 外层 `<div class="speech-bar">`；若 `_open && ShowPanelToggle`，先渲染 `<ol class="speech-panel">`，每行 `<li class="@line.Kind">@line.Text</li>`。
   - 输入行：`<textarea class="speech-input" rows="2" maxlength="300" placeholder="对大家说点什么…" @bind="_text" @bind:event="oninput" @onkeydown="OnKeyDown" disabled="@(!HasRole)"></textarea>`；按钮 `发送`（`disabled="@(!CanSend)"`，`@onclick="SendAsync"`）；`HostSpeaking` 为真时再渲染 `停止朗读` 按钮（`@onclick="StopAsync"`）；`ShowPanelToggle` 为真时渲染展开键。
   - 提示行 `<p class="speech-hint @(_noticeError ? "error" : "")">`：优先级：`!HasRole` → `请先认领角色`；`HostBusy` → `主持正在回应`；否则显示 `_notice`。字数 `<span class="speech-count">@_text.Length/@TabletSpeechService.MaxLength</span>`。
   - `@code`：`private string _text = ""; private string _notice = ""; private bool _noticeError; private bool _sending; private bool _open;` `private bool CanSend => HasRole && !HostBusy && !_sending && !string.IsNullOrWhiteSpace(_text);`
   - `OnKeyDown(KeyboardEventArgs e)`：`e.Key == "Enter" && !e.ShiftKey && CanSend` → `await SendAsync()`。Blazor 里按 Enter 会在 textarea 里插入换行：在 `SendAsync` 成功后把 `_text` 清空即可覆盖这个换行（`oninput` 事件先于 `keydown` 之后的输入，不会残留）；不要引入 JS。
   - `SendAsync`：`_sending = true`；`var result = await Speech.SayAsync(SessionKey, _text);` `_notice = result.Message;` `_noticeError = result.Code is not (TabletSayCode.Accepted or TabletSayCode.Proposed);` 成功时 `_text = ""`；`finally { _sending = false; }`。
   - `StopAsync`：`Speech.Stop(SessionKey, out _)`。
2. `src/LocalDm.Tablet/Pages/Home.razor`：
   - 顶部加 `@inject TabletSpeechService Speech`。
   - 在最外层 `<div class="shell">` 的结尾（`</div>` 之前、整个 `@if … else …` 之后）放 `<SpeechBar SessionKey="@_sessionKey" HasRole="@HasRole" HostBusy="@_hostBusy" HostSpeaking="@_hostSpeaking" ShowPanelToggle="@(_tab != TabPublic)" RecentLines="@RecentLines" />`。
   - `@code` 里：`private bool _hostBusy, _hostSpeaking;` 在 `LoadPublicFeedAsync` 末尾（`StateHasChanged` 之前）刷新 `_hostBusy = Speech.HostBusy; _hostSpeaking = Speech.HostSpeaking;`，并且把 `if (_tab == TabPublic) { StateHasChanged(); }` 改为总是 `StateHasChanged();`（输入条在所有页签可见）；`private IReadOnlyList<(string Kind, string Text)> RecentLines => PublicFeed.TakeLast(8).ToArray();`
   - 「公共对话」页签里 `<p class="lead">只读。提问、推进、投票和揭秘都在电脑上完成。</p>` 改为 `<p class="lead">全桌可见。推进、投票和揭秘在电脑上完成。</p>`。
3. `src/LocalDm.Tablet/wwwroot/reader.css` 末尾加 `.speech-bar`、`.speech-panel`（最大高度 36vh，可滚动）、`.speech-input`（宽度 100%，字号 16px 以上，避免 iOS 聚焦时自动放大）、`.speech-hint`（`.error` 用 `var(--seal)`）、`.speech-count`、按钮样式（沿用文件里已有按钮的边框与底色写法，先 `grep -n "button" src/LocalDm.Tablet/wwwroot/reader.css` 看现有写法）。
4. 新建 `src/LocalDm.Tablet/SpikeTabletSpeech.cs`：`public sealed class SpikeTabletSpeech : ITabletSpeech`（探针演示用，中文注释写明「只用于独立探针，不代表真实主持」）：`SubmitAsync` 在忙时返回 `Rejected(Busy)`，否则把忙截止时间设为 `now + 3 秒`（用 `Interlocked`/`lock` 保证原子）并返回 `new TabletSpeechAck(Accepted, Task.Delay(3000))`；`IsBusy` = 现在早于截止时间；`IsSpeaking` 恒 false；`StopSpeaking` 恒 false。`src/LocalDm.Tablet/Program.cs`：合成模式（`content is SpikeTabletContent`）时 `TabletWebHost.Create(content, source, args, Directory.GetCurrentDirectory(), new SpikeTabletSpeech())`，包模式不传（用默认的空实现）。
5. `tests/LocalDm.Tests/TabletSpeechTests.cs` 追加：`Spike_speech_accepts_once_then_is_busy_until_the_deadline`（用 `ManualTime` 不方便时，给 `SpikeTabletSpeech` 加可选构造参数 `TimeProvider? time = null` 并用它取时间；先 Accepted，立即再发 Busy，3 秒后再发 Accepted）。
6. 运行 Check，再运行 `dotnet build src/LocalDm.Tablet` 确认 Razor 无警告（报告里贴最后一行结果）。

## Check
`dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletSpeechTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不新增 JavaScript 文件，不改 `tablet-shell.js`、`reader-pagination.js`、`custom.css`。
- 不改角色本阅读器（`RoleReader.razor`）和线索卡逻辑；不改认领流程。
- 发送逻辑只调用 `TabletSpeechService.SayAsync`；页面里不要自己判断长度、频率或忙碌之外的规则（按钮灰掉只是提示，规则以服务为准）。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.
