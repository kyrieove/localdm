# tablet-speech-wiring-v2 — 端到端并发测试：真实 HostViewModel + 真实平板 HTTP 服务 + WPF 调度器

## Goal
- 新测试文件 `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs`：真实 `HostViewModel`（假网关、假朗读）经 `TabletSpeechBridge`（UI 线程调度）接到真实 `TabletWebHost`，用多个带独立 Cookie 的 `HttpClient` 打 `POST /tablet/say`，验证并发、忙碌与署名。
- 前置已合入（以仓库为准）：`src/LocalDm.App/TabletSpeechBridge.cs`、`HostViewModel.SubmitTabletSpeech`、`src/LocalDm.Tablet/TabletSpeechService.cs`、`TabletWebHost.Create(content, source, args, contentRoot, speech, time)`。

## Risk
medium — 要独立重算的关键数字：N 块平板同时 `POST /tablet/say` 时 `202` 的个数恰好 1，`gateway.CompletionCount` 恰好 1。

## Parameters
- 接口返回（`TabletSpeechService`）：`202` + `code:"ACCEPTED"`（接受）、`202` + `code:"PROPOSED"`、`409` + `code:"BUSY"` + `message:"主持正在回应"`、`403` + `code:"NO_ROLE"`。请求体 JSON `{"text":"…"}`；认领 `POST /tablet/claim?roleId=<id>`；公共状态 `GET /tablet/public` 的 JSON 里 `host.busy` / `host.speaking`；停止 `POST /tablet/say/stop` → 200 `{"stopped":true|false}`。
- 6 个角色：`role-1` … `role-6`，显示名 `甲`…`己`（测试里自己造的 `ITabletContent`，见 Steps 第 1 步）。
- 不同平板用不同 `HttpClient`（各带 `CookieContainer`），因此服务端 2 秒间隔按会话分开，不会干扰。

## Files
may change: `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 2)
1. 新建 `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs`，命名空间 `LocalDm.UiTests`，用以下**现成写法**（已查过，不要再去读别的文件探索）：
   - usings：`System.IO`、`System.Net`、`System.Net.Http`、`System.Net.Http.Json`、`System.Net.Sockets`、`System.Text.Json`、`System.Windows.Threading`、`LocalDm.App`、`LocalDm.App.ViewModels`、`LocalDm.Core.Domain`、`LocalDm.Core.Game`、`LocalDm.Core.Hosting`、`LocalDm.Core.Ports`、`LocalDm.Core.Tablet`、`LocalDm.Infrastructure.Storage`、`LocalDm.Tablet`、`Microsoft.Data.Sqlite`。
   - 主持台搭法与 `tests/LocalDm.UiTests/PublicSyncBannerTests.cs` 第 66–77 行 `NewHost` 完全一致（`ToolSettings.Default with { ModelId = "ui-test-model", AutoRead = false }`；`new HostViewModel(new HostDirector(gateway, tts), () => settings, new HostIntentRouter(), new CommandDispatcher(store), store, tts)`），存储是 `new SessionStore(Path.Combine(root, "sessions.db"))` + `await store.InitializeAsync(CancellationToken.None)`，然后 `await host.LoadDemoAsync()`；临时目录自己建、`finally` 里 `SqliteConnection.ClearAllPools()` 后 `Directory.Delete(root, true)`（参照同文件第 137–160 行 `TempRoot`，在本文件里写一个最小私有版）。
   - 假网关 `GatedGateway : IMagpieGateway`（本文件私有）：`ListModelsAsync` 返回 `["ui-test-model"]`；`CompleteAsync` 里 `Interlocked.Increment(ref _count)`，然后 `await Gate.Task`（`Gate` 是公开的 `TaskCompletionSource`，`TaskCreationOptions.RunContinuationsAsynchronously`，可替换为已完成的），返回 `"这是端到端测试的主持回复。"`；`public int CompletionCount => Volatile.Read(ref _count);`。假朗读 `QuietTts : ITtsService` 与 `PublicSyncBannerTests.cs` 第 119–135 行的 `SilentTts` 相同形状，再加一个可选的朗读门：`SpeakAsync` 里 `await SpeakGate.Task`（默认已完成）。
   - 平板内容 `FakeContent : ITabletContent`（本文件私有，成员见 `src/LocalDm.Tablet/ITabletContent.cs` 的接口）：`StoryTitle = "端到端"`，`Roles` = 6 个 `new TabletRole($"role-{i}", name)`（`甲乙丙丁戊己`），`Cards = []`，`RoleBooks = new Dictionary<string, RoleBookText>()`，`TryGetCardImage` 返回 false（`bytes = []`、`contentType = ""`、`failure = TabletQueryFailure.NotFound`），`TryGetRoleSourcePage` 返回 false。
   - 起服务：`var port = FreePort();`（`new TcpListener(IPAddress.Loopback, 0)` 取端口后停掉，返回端口）；`var app = TabletWebHost.Create(new FakeContent(), new EmptyPublicFeedSource(), ["--urls", $"http://127.0.0.1:{port}"], root, new TabletSpeechBridge(host, Dispatcher.CurrentDispatcher));` `await app.StartAsync();` 结束时 `await app.StopAsync(); await app.DisposeAsync();`。这些全部在 `await WpfRunner.RunAsync(async () => { … })` 里（`Dispatcher.CurrentDispatcher` 在里面就是 UI 线程调度器）。
   - 辅助 `async Task<HttpClient> NewTablet(int port, string roleId)`：`new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") }`；先 `POST tablet/claim?roleId=…`，断言成功（`EnsureSuccessStatusCode`）；返回该客户端。辅助 `Task<HttpResponseMessage> Say(HttpClient c, string text) => c.PostAsJsonAsync("tablet/say", new { text });`。
   - 注意：`POST` 要在测试线程上 `await`，而 UI 线程要能处理被调度来的请求——因为整个测试体在 UI 线程的异步方法里，`await` 会让出 UI 线程，所以不会死锁；不要在 UI 线程上用 `.Result` / `.Wait()`。
   - 用例（每个包在 `WpfRunner.RunAsync` 里，结束前放开所有门并等 `host.IsBusy` 变 false）：
     - `Two_tablets_post_at_once_only_one_is_accepted`：`gateway.Gate` 未完成；平板甲、乙各 `NewTablet`；`await Task.WhenAll(Say(a,"一"), Say(b,"二"))`；两个状态码里恰好一个是 202、另一个是 409，409 的 JSON `code == "BUSY"` 且 `message == "主持正在回应"`；放开 `Gate`；`await WaitUntilAsync(() => !host.IsBusy)`；`gateway.CompletionCount == 1`；`host.Messages` 里 `IsPlayer` 的消息恰好 1 条，文本以 `【` 开头。
     - `Desktop_send_and_tablet_post_at_once_only_one_is_accepted`：`host.InputText = "电脑的话"; var desktop = host.SendAsync();`（不 await）；平板 `Say` → 409 `BUSY`；放开 `Gate`；`await desktop`；`CompletionCount == 1`；`host.Messages` 里 `IsPlayer` 的消息恰好 1 条且文本为 `电脑的话`。
     - `Tablet_post_while_host_is_speaking_is_busy_and_can_be_stopped`：`gateway.Gate` 已完成（`Gate.SetResult()`）、`tts.SpeakGate` 未完成并把 `AutoRead = true`（本用例单独用 `ToolSettings.Default with { ModelId = "ui-test-model", AutoRead = true }` 的主持台）；平板甲 `Say` → 202；`await WaitUntilAsync(() => host.IsSpeaking)`；平板乙 `Say` → 409；甲的 `GET tablet/public` 的 JSON `host.speaking == true` 且 `host.busy == true`；甲 `POST tablet/say/stop` → 200 且 `stopped == true`；`await WaitUntilAsync(() => !host.IsBusy)`。
     - `Many_tablets_post_at_once_exactly_one_reaches_the_model`：6 块平板各认领一个角色，`await Task.WhenAll(...)` 同时 `Say`；`202` 的个数恰好 1，其余 5 个全是 409；放开 `Gate` 后 `CompletionCount == 1`。
     - `Tablet_advance_phrase_is_a_proposal_and_does_not_advance`：平板 `Say("进入下一环节")` → 202 且 `code == "PROPOSED"`；`CompletionCount == 0`；`host.Session.Revision` 与发送前相等。
   - `WaitUntilAsync` 本文件私有：最多 100 次、每次 `await Task.Delay(30)`，条件成立即返回（参照 `PublicSyncBannerTests.cs` 第 126–132 行）。
2. 运行 Check。若某个用例因宿主环境起不来而做不到，保留其余用例，在回复里写明原因；不要改任何产品代码，也不要改别的测试文件。

## Check
`dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletSpeechE2ETests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不改 `src/` 下任何文件；发现产品代码有问题就停下来在回复里写明，不要自己改。
- 不删除、不跳过已有测试。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

## Acceptance
- verdict: accepted
- files: tests/LocalDm.UiTests/TabletSpeechE2ETests.cs
- check: `dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletSpeechE2ETests"` → exit 0 (recheck)
- tokens: exec agy gemini-3.8-flash: fresh 102k, cached 1129k, out 8k (agy-tablet-speech-wiring-v2.jsonl) · review gpt-6.1-sol 1 round
- review findings: 3 real / 0 false positive / 0 missed (medium, gpt-6.1-sol, 2 rounds；scope override 因 PRODUCT.md/user-guide.md 是 Claude 自己改的)
- scope override: PRODUCT.md 和 docs/user-guide.md 是 Claude 在 agy 执行期间同步文档时自己改的，不是 agy 改的（agy 只写了 TabletSpeechE2ETests.cs）。
- claude fix: sol 两条 P1 属实且都是测试写弱了——停止用例不该自己放开朗读门（改为只靠 stop 恢复空闲并断言 !IsSpeaking）；WaitUntilAsync 超时必须断言失败。recheck exit=0；全量 LocalDm.Tests 428（422 过/6 跳过/0 败）、UiTests 53 过。独立复核关键数字：5 个并发用例断言 202 恰好 1、CompletionCount 恰好 1，通过。
- unreviewed-fix: tests/LocalDm.UiTests/TabletSpeechE2ETests.cs 第二轮 sol 的 P1（等待超时的断言会跳过 StopAsync/DisposeAsync）已修为 5 处 try/finally，并把只调一次的 TCS 辅助方法改名为 Opened；两轮审查用完，未再审，合并前请用户看这一处。recheck exit=0。
- deferred: 无
