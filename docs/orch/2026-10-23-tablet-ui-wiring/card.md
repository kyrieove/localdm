# tablet-ui-wiring — 把平板界面投影接进平板服务：`GET /tablet/ui`

## Goal
- 平板服务多一个接口 `GET /tablet/ui?after=N`：要浏览器会话（没有就 401），返回 `TabletUiProjection` 的 JSON（驼峰、枚举写成字符串、中文不转义）；桌面还没装载本局时返回 404 `{ok:false, code:"NOT_READY", message:"等待主持人开始这一局"}`；`after` 的校验与 `/tablet/public` 完全一致（没带 = 0，不是数字或为负 = 400）。
- 软件里的真实来源 `CurrentSessionTabletUiSource` 接上：读公开消息 → 回到 WPF 调度器上用 `host.Session` 的 `Package` 与 `Session` 一起投影（一次读取，包与会话取自同一个线程上的同一时刻）→ 返回投影；本局还没装载、或消息所属局与当前局不一致，一律返回 null（不抛异常、不返回半截数据）。
- 端到端测试通过：真实 `HostViewModel` + 真实平板服务，`/tablet/ui` 返回的 JSON 里不含演示剧本里任何非公开段落的原文、不含上下文占用文字，且主持忙碌时 `hostBusy` 为真。

## Risk
medium — 秘密边界（投影）已在卡 `tablet-ui-projection` 里按高风险验过，本卡只做传输与接线；Claude 仍独立重算：演示剧本的非公开段落（`Visibility != Public`）原文在 `/tablet/ui` 响应体里出现的次数 = 0，且这样的段落至少 1 个（防空转）。

## Parameters
- 路由：`GET /tablet/ui`，查询参数 `after`（长整数，默认 0）。
- 状态码：无会话 Cookie → `401`（与 `/tablet/public` 同一个判断 `TabletSessions.KeyFor(context) is null`）；`after` 非法 → `400`；`ITabletUiSource.ReadAsync` 返回 null → `404` + `{ok:false, code:"NOT_READY", message:"等待主持人开始这一局"}`；其余 `200`。
- 序列化选项：`new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }`，用 `Results.Json(projection, options)`。字段名即投影属性名的驼峰：`sessionId, title, state, revision, phaseId, phaseTitle, phaseIndex, phaseCount, hint, phases[{phaseId,title,status}], controls[{kind,label,visible,enabled}], controlsNote, pendingMaterialCount, releasedMaterialCount, voteSummary, revealedTexts, messages[{sessionId,messageId,author,text,createdAt,kind,sequence}], nextSequence, hostBusy, hostSpeaking`。
- `hostBusy`/`hostSpeaking` 取 `TabletSpeechService.HostBusy` / `HostSpeaking`（与 `/tablet/public` 一样），传给 `ITabletUiSource.ReadAsync`。
- 新接口（`src/LocalDm.Core/Tablet/ITabletUiSource.cs`，命名空间 `LocalDm.Core.Tablet`）：
  ```csharp
  public interface ITabletUiSource
  {
      /// <summary>取平板界面投影；本局还没装载（或消息所属局已换）时返回 null。</summary>
      Task<TabletUiProjection?> ReadAsync(long after, bool hostBusy, bool hostSpeaking, CancellationToken cancellationToken);
  }
  ```
- `TabletWebHost.Create` 新增末位可选参数 `ITabletUiSource? ui = null`，注册为 `builder.Services.AddSingleton<ITabletUiSource>(ui ?? new EmptyTabletUiSource());`；`EmptyTabletUiSource.ReadAsync` 恒返回 `Task.FromResult<TabletUiProjection?>(null)`。已有的所有 `Create(...)` 调用不改。
- 演示剧本：`LocalDm.App.Demo.DemoPackage.Create()`；非公开段落 = `Segments` 里 `Visibility != SegmentVisibility.Public`（含真相段 `d-truth`）。

## Files
may change: `src/LocalDm.Core/Tablet/ITabletUiSource.cs`, `src/LocalDm.Tablet/EmptyTabletUiSource.cs`, `src/LocalDm.Tablet/TabletWebHost.cs`, `src/LocalDm.App/CurrentSessionTabletUiSource.cs`, `src/LocalDm.App/ViewModels/TabletJoinViewModel.cs`, `src/LocalDm.App/AppComposition.cs`, `tests/LocalDm.UiTests/TabletUiE2ETests.cs`, `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs` / read-only: `stories/`, `.appdata/`, `.local/`, `src/LocalDm.Core/Game/`, `src/LocalDm.Core/Domain/`, `src/LocalDm.App/ViewModels/SessionViewModel.cs`, `src/LocalDm.App/ViewModels/HostViewModel.cs`, `src/LocalDm.App/CurrentSessionPublicFeedSource.cs`, `src/LocalDm.App/TabletSpeechBridge.cs`, `src/LocalDm.Tablet/TabletSpeechService.cs`, `src/LocalDm.Tablet/EmptyPublicFeedSource.cs`

## Steps (N = 6)
1. 新建 `src/LocalDm.Core/Tablet/ITabletUiSource.cs`：Parameters 里的接口，文件级命名空间 `LocalDm.Core.Tablet`。新建 `src/LocalDm.Tablet/EmptyTabletUiSource.cs`（命名空间 `LocalDm.Tablet`，`public sealed class EmptyTabletUiSource : ITabletUiSource`，参照同目录 `EmptyPublicFeedSource.cs` 的写法，一句话注释说明“独立运行的平板服务看不到桌面那一局，所以没有界面可给”）。
2. 改 `src/LocalDm.Tablet/TabletWebHost.cs`：
   - `Create` 签名末尾加 `ITabletUiSource? ui = null`；注册 `builder.Services.AddSingleton<ITabletUiSource>(ui ?? new EmptyTabletUiSource());`（放在 `AddSingleton(time ?? ...)` 附近）。
   - 加私有静态方法（放在类里 `Create` 之后），并让**两个**接口共用它，`/tablet/public` 的行为不变：
     ```csharp
     private static bool TryParseAfter(HttpContext context, out long after)
     {
         after = 0;
         if (!context.Request.Query.ContainsKey("after")) return true;
         return long.TryParse(context.Request.Query["after"], out after) && after >= 0;
     }
     ```
     `/tablet/public` 里原来那段“没带 after 就当 0；带错了或为负返回 `Results.BadRequest()`”替换成 `if (!TryParseAfter(context, out var after)) { return Results.BadRequest(); }`（`using System.Text.Json` 等按需加）。
   - 在 `/tablet/public` 之后加 `app.MapGet("/tablet/ui", async (ITabletUiSource ui, TabletSpeechService speechService, HttpContext context) => { … })`：先 `TabletSessions.KeyFor(context) is null` → `Results.Unauthorized()`；再 `TryParseAfter` 失败 → `Results.BadRequest()`；再 `var projection = await ui.ReadAsync(after, speechService.HostBusy, speechService.HostSpeaking, context.RequestAborted);`；为 null → `Results.Json(new { ok = false, code = "NOT_READY", message = "等待主持人开始这一局" }, statusCode: 404)`；否则 `Results.Json(projection, UiJson)`，其中 `UiJson` 是类里一个 `private static readonly JsonSerializerOptions`，按 Parameters 的序列化选项构造。
   - 旁路检查（新增的校验是 401/400/404 三条）：三条都在调用 `ui.ReadAsync` 之前或之后立即返回，没有缓存、回退路径能绕开 401 与 400。
3. 新建 `src/LocalDm.App/CurrentSessionTabletUiSource.cs`（命名空间 `LocalDm.App`，`using System.Windows.Threading; using LocalDm.App.ViewModels; using LocalDm.Core.Game; using LocalDm.Core.Tablet;`）：
   ```csharp
   public sealed class CurrentSessionTabletUiSource(IPublicFeedSource feed, Func<SessionViewModel> session, Dispatcher dispatcher) : ITabletUiSource
   {
       public async Task<TabletUiProjection?> ReadAsync(long after, bool hostBusy, bool hostSpeaking, CancellationToken cancellationToken)
       {
           var snapshot = await feed.ReadAsync(after, cancellationToken);
           if (string.IsNullOrEmpty(snapshot.SessionId)) return null;

           // 包与会话要在 UI 线程上一起读：换局发生在 UI 线程，网页线程上分开读会读到新会话配旧包。
           return await dispatcher.InvokeAsync(() =>
           {
               var view = session();
               if (view.Package is not { } package || view.Session is not { } current
                   || !string.Equals(current.SessionId, snapshot.SessionId, StringComparison.Ordinal))
               {
                   return null;
               }

               return TabletUiProjector.Project(package, current, snapshot, hostBusy, hostSpeaking);
           }).Task;
       }
   }
   ```
   类上加一句 `<summary>`：说明“网页线程读公开消息、UI 线程做投影；消息和会话对不上就当还没装载，平板下一次轮询会拿到新的”。委托返回类型要写成 `TabletUiProjection?`（必要时 `InvokeAsync<TabletUiProjection?>(…)`）。
4. 改 `src/LocalDm.App/ViewModels/TabletJoinViewModel.cs`：构造函数加末位可选参数 `ITabletUiSource? ui = null`，存到私有只读字段 `_ui`（与 `_speech` 并列），第 160 行附近的 `TabletWebHost.Create(_content, _feed, ["--urls", …], _webRoot, _speech)` 改成 `TabletWebHost.Create(_content, _feed, ["--urls", …], _webRoot, _speech, ui: _ui)`。改 `src/LocalDm.App/AppComposition.cs` 第 134–136 行：
   ```csharp
   var feedSource = new CurrentSessionPublicFeedSource((LocalDm.Core.Ports.IPublicMessageFeed)_context.Store, () => host.Session);
   var tablet = new TabletJoinViewModel(
       feedSource,
       speech: new TabletSpeechBridge(host, System.Windows.Application.Current.Dispatcher),
       ui: new CurrentSessionTabletUiSource(feedSource, () => host.Session, System.Windows.Application.Current.Dispatcher));
   ```
   （`feedSource` 这个局部变量名先在 `AppComposition.cs` 里 grep 确认没有重名再用。）
5. 把 `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs` 里这些成员的可见性从 `private` 改成 `internal`，**别的一个字都不改**：`CreateHostAsync`、`FreePort`、`NewTablet`、`WaitUntilAsync` 四个 `private static` 方法，以及 `GatedGateway`、`QuietTts`、`FakeContent`、`TempRoot` 四个 `private sealed class`（`Say` 不用改）。改完这个文件的测试行为不变。
6. 新建 `tests/LocalDm.UiTests/TabletUiE2ETests.cs`（命名空间 `LocalDm.UiTests`，`public sealed class TabletUiE2ETests`），用以下**现成写法**（已查过，不要再去读别的文件探索；usings 与 `TabletSpeechE2ETests.cs` 第 1–16 行相同，再加 `System.Collections.Generic` 若需要）：
   - 起服务的辅助（本文件私有）：
     ```csharp
     private static async Task<(WebApplication App, int Port, HostViewModel Host, TabletSpeechE2ETests.GatedGateway Gateway, TabletSpeechE2ETests.QuietTts Tts)> StartAsync(string root)
     {
         var (host, gateway, tts) = await TabletSpeechE2ETests.CreateHostAsync(root);
         var dispatcher = Dispatcher.CurrentDispatcher;
         var feed = new CurrentSessionPublicFeedSource(new StaticMessageFeed(), () => host.Session);
         var port = TabletSpeechE2ETests.FreePort();
         var app = TabletWebHost.Create(
             new TabletSpeechE2ETests.FakeContent(),
             feed,
             ["--urls", $"http://127.0.0.1:{port}"],
             root,
             new TabletSpeechBridge(host, dispatcher),
             ui: new CurrentSessionTabletUiSource(feed, () => host.Session, dispatcher));
         await app.StartAsync();
         return (app, port, host, gateway, tts);
     }
     ```
   - 假消息来源 `StaticMessageFeed : IPublicMessageFeed`（本文件私有；接口在 `LocalDm.Core.Ports`，`AppendPublicMessageAsync(PublicMessage, CancellationToken)` 抛 `NotSupportedException`，`ReadPublicMessagesAsync(string sessionId, long after, int limit, CancellationToken ct)` 返回两条固定消息：`Sequence` 1 和 2，`SessionId = sessionId`，作者 `PublicMessageAuthor.Player`/`Host`，正文 `"【甲】大家好"`/`"欢迎各位"`，`Kind` 对应 `PlayerSpeech`/`HostReply`，`CreatedAt = DateTimeOffset.UnixEpoch`，只返回序号大于 `after` 的、最多 `limit` 条；若接口还有别的成员，先看 `src/LocalDm.Core/Ports/ExternalPorts.cs` 第 217 行起，全部照样实现）。
   - 每个用例都包在 `await WpfRunner.RunAsync(async () => { using var root = new TabletSpeechE2ETests.TempRoot(); … })` 里，`try/finally` 结尾 `await app.StopAsync(); await app.DisposeAsync();`（先放开 `gateway.Gate` 与 `tts.SpeakGate` 并 `await TabletSpeechE2ETests.WaitUntilAsync(() => !host.IsBusy)`），与 `TabletSpeechE2ETests` 里 `Two_tablets_post_at_once_only_one_is_accepted` 的收尾写法一致。
   - 用例：
     - `Ui_requires_a_session_cookie_and_valid_after`：不带 Cookie 的 `new HttpClient { BaseAddress = … }` `GetAsync("tablet/ui")` → 401；`NewTablet(port, "role-1")` 的客户端 `GetAsync("tablet/ui?after=abc")` → 400，`?after=-1` → 400。
     - `Ui_returns_projection_of_the_loaded_session`：认领后 `GetAsync("tablet/ui?after=0")` → 200；`ReadFromJsonAsync<JsonElement>()`：`sessionId == host.Session.Session!.SessionId`，`phaseTitle == host.Session.PhaseTitle`，`phases` 数组长度 `== host.Session.Projection!.PhaseTrack.Count`，`controls` 长度 7 且 `controlsNote == "请在电脑上操作"`，`messages` 长度 2 且第一条 `text == "【甲】大家好"`，`nextSequence == 2`，`hostBusy == false`；再 `GetAsync("tablet/ui?after=2")` → `messages` 长度 0、`nextSequence == 2`。
     - `Ui_shows_host_busy_while_model_is_answering`：`gateway.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);` 认领后 `PostAsJsonAsync("tablet/say", new { text = "你好" })` → 202；`GetAsync("tablet/ui")` 的 `hostBusy == true`；放开 `gateway.Gate.TrySetResult()` 后 `WaitUntilAsync(() => !host.IsBusy)`，再取一次 `hostBusy == false`。
     - `Ui_body_contains_no_private_segment_text_or_internal_state`（Claude 重算的就是这个）：`var package = DemoPackage.Create(); var secret = package.Segments.Where(s => s.Visibility != SegmentVisibility.Public).ToList(); Assert.NotEmpty(secret);` 取 `GetStringAsync("tablet/ui")` 的整个响应体 `body`；对 `secret` 里每一段断言 `body` 不含它的 `OriginalText` 和 `SegmentId`；断言 `body` 不含 `host.ContextUsageText`、不含 `"上下文"`、不含 `"备忘"`；若 `host.HasMemo` 则 `body` 不含 `host.Memo`。
     - `Ui_is_not_ready_without_a_ui_source`：用 `TabletWebHost.Create(new TabletSpeechE2ETests.FakeContent(), new EmptyPublicFeedSource(), ["--urls", …], root.Path)`（不传 `ui`）起服务，认领后 `GetAsync("tablet/ui")` → 404，JSON `code == "NOT_READY"`、`message == "等待主持人开始这一局"`。
     - `Source_returns_null_when_feed_belongs_to_another_session`：`new CurrentSessionTabletUiSource(new FixedFeed("other-session"), () => host.Session, Dispatcher.CurrentDispatcher).ReadAsync(0, false, false, CancellationToken.None)` → `Assert.Null`；`FixedFeed : IPublicFeedSource` 本文件私有，`ReadAsync` 返回 `new PublicFeedSnapshot { SessionId = id, Messages = [], NextSequence = after }`；再用 `FixedFeed(string.Empty)` → 也是 null。
   - 运行 Check；任何红先判断是实现错还是测试错，不要改只读文件。

## Check
```
dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletUiE2ETests" --filter-class "LocalDm.UiTests.TabletSpeechE2ETests" && dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletSpeechTests"
```

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不改 `PublicSessionProjector.cs`、`TabletUiProjector.cs`、`SessionViewModel.cs`、`HostViewModel.cs`、`CurrentSessionPublicFeedSource.cs`；本卡不做网页页面，不改 `src/LocalDm.Tablet/Pages/` 与 `Layout/`。
- 不读 `stories/`、`.appdata/`、`.local/`；不删除、不跳过已有测试。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

## Acceptance
- verdict: accepted
- files: src/LocalDm.App/AppComposition.cs, src/LocalDm.App/CurrentSessionTabletUiSource.cs, src/LocalDm.App/ViewModels/TabletJoinViewModel.cs, src/LocalDm.Core/Tablet/ITabletUiSource.cs, src/LocalDm.Tablet/EmptyTabletUiSource.cs, src/LocalDm.Tablet/TabletWebHost.cs, tests/LocalDm.UiTests/TabletSpeechE2ETests.cs, tests/LocalDm.UiTests/TabletUiE2ETests.cs
- check: `dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletUiE2ETests" --filter-class "LocalDm.UiTests.TabletSpeechE2ETests" && dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletSpeechTests"` → exit 0 (run by run-card)
- tokens:
  - round 1: exec custom-local:antigravity/gemini-3.8-flash: fresh 141k, cached 2000k, out 16k  (agy-tablet-ui-wiring.jsonl) · review gpt-6.1-sol: fresh 67k, cached 284k, out 1k  (codex-review-tablet-ui-wiring.jsonl)
- review findings: 0 real / 0 false positive / 0 missed (medium, gpt-6.1-sol)
- baseline: 基线 Check 本来就绿（新测试类不存在时只跑了旧类），Claude 另单独跑 `TabletUiE2ETests` 6/6 确认新用例真的执行。
- recompute (Claude, independent): 临时测试（已删）起真实 HostViewModel + 真实平板服务，取 `/tablet/ui` 响应体：演示剧本非公开段落 1 个，原文在响应体出现 0 次。响应体为 Prepared 态投影（7 个按钮、5 个环节、`controlsNote` 正确、无备忘/上下文占用字段）。
- full suite: 构建 0 警告 0 错误；`LocalDm.Tests` 435（429 过 / 6 跳过 / 0 败）；`LocalDm.UiTests` 59 过。
- deferred: 无。
