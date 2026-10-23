# tablet-speech-wiring — 把平板发言接进电脑端：UI 线程桥接、AppComposition 接线、端到端并发测试

## Goal
- `LocalDm.App` 里有 `TabletSpeechBridge : ITabletSpeech`：把平板服务（Kestrel 线程）的调用切到 WPF UI 线程，再调 `HostViewModel.SubmitTabletSpeech` / `StopSpeakingForTablet`；`IsBusy`/`IsSpeaking` 直接读主持台的当前值。
- `TabletJoinViewModel` 能接收一个 `ITabletSpeech`（可选参数，默认不传），启动平板服务时传给 `TabletWebHost.Create`；`AppComposition` 在建好 `HostViewModel` 之后把 `new TabletSpeechBridge(host, Dispatcher)` 传给平板入口。
- 端到端测试（真实 `HostViewModel` + 真实平板 HTTP 服务 + WPF 调度器，模型用假网关）：两块平板同时发送、电脑输入框和平板同时发送、AI 回答中途平板发送、朗读中平板发送——每种都只有一个请求被接受，其余得到 `409 BUSY` 与文案「主持正在回应」；平板发言在电脑对话里是署名玩家消息。

## Risk
medium — 与卡 A 同一决定：不做计划审查，Claude 独立重算。要独立重算的关键数字：N 块平板同时 `POST /tablet/say` 时 `202` 的个数恰好 1，`gateway.CompletionCount` 恰好 1。

## Parameters
- 桥接类线程规则：`SubmitAsync` 用 `dispatcher.InvokeAsync(() => host.SubmitTabletSpeech(roleName, text)).Task`（返回 `Task<TabletSpeechAck>`）；`StopSpeaking` 用 `dispatcher.Invoke(host.StopSpeakingForTablet)`；`IsBusy => host.IsBusy`，`IsSpeaking => host.IsSpeaking`（布尔读取，不切线程）。
- 平板接口与文案以仓库里的 `src/LocalDm.Tablet/TabletSpeechService.cs` 为准：`POST /tablet/say` 的 `202 ACCEPTED`、`409 BUSY`、`403 NO_ROLE`、`429 TOO_FAST`；busy 文案「主持正在回应」。
- 前置：`ITabletSpeech`、`HostViewModel.SubmitTabletSpeech`、`TabletSpeechService`、`TabletWebHost.Create(..., ITabletSpeech? speech, TimeProvider? time)` 已合入。
- 为避开 2 秒间隔干扰并发判断，测试里让不同平板用不同会话（不同 Cookie、不同角色）；同一平板连点的情形已在服务层测试覆盖。

## Files
may change: `src/LocalDm.App/TabletSpeechBridge.cs`, `src/LocalDm.App/ViewModels/TabletJoinViewModel.cs`, `src/LocalDm.App/AppComposition.cs`, `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 4)
1. 新建 `src/LocalDm.App/TabletSpeechBridge.cs`（命名空间 `LocalDm.App`，中文注释，风格同 `CurrentSessionPublicFeedSource.cs`）：`public sealed class TabletSpeechBridge(HostViewModel host, System.Windows.Threading.Dispatcher dispatcher) : ITabletSpeech`，成员按 Parameters 实现。注释写明：`HostViewModel` 只允许在 UI 线程被修改，平板请求来自 Kestrel 线程，所以必须经调度器串行化。
2. `src/LocalDm.App/ViewModels/TabletJoinViewModel.cs`：构造函数 `TabletJoinViewModel(IPublicFeedSource feed, string? contentRoot = null)`（约第 41 行）末尾加可选参数 `ITabletSpeech? speech = null`，存到 `private readonly ITabletSpeech? _speech;`；`StartAsync` 里 `TabletWebHost.Create(_content, _feed, ["--urls", …], _webRoot)`（约第 158 行）改为末尾追加 `_speech`（`Create` 的参数顺序见 `src/LocalDm.Tablet/TabletWebHost.cs`：`…, contentRoot, speech, time`，`time` 不传）。现有 4 处测试调用点不改。
3. `src/LocalDm.App/AppComposition.cs`（约第 134 行）：`TabletJoinViewModel` 必须在 `HostViewModel` 创建之后构造（现状已是），把参数换成 `new TabletJoinViewModel(new CurrentSessionPublicFeedSource(…), speech: new TabletSpeechBridge(host, System.Windows.Application.Current.Dispatcher))`（具名参数；`contentRoot` 保持不传）。
4. 新建 `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs`（命名空间 `LocalDm.UiTests`；仿 `tests/LocalDm.UiTests/TabletJoinTests.cs` 第 172 行附近「真实 `TabletJoinViewModel` + 真实 `HostViewModel`」的搭法，以及 `PublicSyncBannerTests.cs` 里 `NewHost` 与假网关、假朗读的写法；每个用例整体包在 `WpfRunner.RunAsync` 里；`HostViewModel` 要装载一局：`await host.LoadDemoAsync()`）。公共辅助（本文件内私有）：
   - 一个 `GatedGateway : IMagpieGateway`：`CompleteAsync` 计数并等待可由测试放开的门，返回固定文本；一个不出声的 `ITtsService`（朗读门可选，用来测朗读阶段）。
   - 起平板服务：`await using var join = new TabletJoinViewModel(feed, root, speech: new TabletSpeechBridge(host, Dispatcher.CurrentDispatcher))`，`await join.StartAsync()`，取实际端口/地址（参照 `TabletJoinTests` 取地址的方式），再设置装载的剧本使角色可认领（`TabletJoinTests` 里 `join.SetPackageAsync(...)` 的用法；演示剧本包取自 `host`/`LoadDemoAsync` 用的同一个包）。
   - 一个 `NewTablet(roleId)` 辅助：新建带独立 `CookieContainer` 的 `HttpClient`，`POST /tablet/claim?roleId=…`，返回该客户端。
   用例：
   - `Two_tablets_post_at_once_only_one_is_accepted`：门挂住；两块平板同时（`Task.WhenAll`）`POST /tablet/say`；恰好一个 `202`、另一个 `409` 且 `code == "BUSY"`、`message == "主持正在回应"`；放开门后 `gateway.CompletionCount == 1`；`host.Messages` 里恰好一条玩家消息，文本以 `【` 开头并含该平板认领的角色名。
   - `Desktop_send_and_tablet_post_at_once_only_one_is_accepted`：电脑先 `host.InputText = "电脑的话"; _ = host.SendAsync();`，平板再 `POST` → `409 BUSY`；放开门后 `CompletionCount == 1`，且 `host.Messages` 里没有平板的话。
   - `Tablet_post_while_host_is_generating_or_speaking_is_busy`：生成中（门挂住）→ `409`；放开后进入朗读（朗读门挂住，`host.IsSpeaking`）→ 平板再 `POST` → `409`；`GET /tablet/public` 的 JSON 里 `host.busy == true` 且 `host.speaking == true`；`POST /tablet/say/stop` → 200 且 `stopped == true`，随后 `host.IsBusy` 变 false。
   - `Many_tablets_post_at_once_exactly_one_reaches_the_model`：6 块平板各认领一个角色（演示包若不足 6 个角色就用它实际有的数量，至少 3 块），同时 `POST`；`202` 的个数恰好 1，其余全是 `409`；`CompletionCount == 1`。
   - `Tablet_advance_phrase_is_a_proposal_and_does_not_advance`：平板发 `进入下一环节` → `202`，`code == "PROPOSED"`；`host.Session.Revision` 不变；模型 `CompletionCount == 0`。
   - 每个用例结束时放开所有门并等 `host.IsBusy` 为 false，再释放 `join`。若某个用例因宿主环境起不来而做不到，保留其余用例，在回复里写明原因，不要改产品代码绕过。

## Check
`dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletSpeechE2ETests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 桥接类里不写忙碌判断、署名、长度/频率限制或意图识别：这些分别在 `HostViewModel.SubmitTabletSpeech` 和 `TabletSpeechService`，这里只切线程并转发。
- 不改 `HostViewModel`、`TabletSpeechService`、`TabletWebHost`、网页文件；发现它们有问题就停下来在回复里写明，不要自己改。
- 不删除、不跳过已有测试。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.
