# tablet-speech-service — 平板服务：发言规则、POST /tablet/say、停止朗读、公共接口带「主持忙碌」

## Goal
- `LocalDm.Tablet` 里有 `TabletSpeechService`：把「会话 Cookie 键 + 文字」按规则（已认领角色、非空、≤300 字、同一平板两次发送间隔 ≥2 秒、主持不忙）转给 `ITabletSpeech.SubmitAsync`，返回带中文文案的结果。Razor 页面（下一张卡）和 HTTP 接口都只走这一个服务。
- HTTP：`POST /tablet/say`（JSON `{"text":"…"}`）、`POST /tablet/say/stop`；`GET /tablet/public` 的 JSON 多出 `host: { busy, speaking }`。
- `TabletWebHost.Create` 多两个可选参数 `ITabletSpeech? speech = null, TimeProvider? time = null`；不传 `speech` 时用一个永远 `NoSession` 的空实现（独立探针、合成模式用）。
- 有测试：每条规则、每种返回码、同一平板连点、两块平板同时发送只放行一个。

## Risk
medium

## Parameters
- 常量：`TabletSpeechService.MaxLength = 300`（按 `string.Length` 计，对去掉首尾空白后的文字；`// shortcut: 按 UTF-16 单元计数，表情符号算 2，真要精确再改 StringInfo`）；`TabletSpeechService.MinInterval = TimeSpan.FromSeconds(2)`。
- 返回码与文案（原样；HTTP 状态码、`code` 字符串、中文 `message`）：

  | 情况 | HTTP | code | message |
  |---|---|---|---|
  | 没有会话 Cookie | 401 | `NO_SESSION_KEY` | `请先从电脑上的二维码或网址重新打开本页。` |
  | 没认领角色 | 403 | `NO_ROLE` | `请先认领角色` |
  | 文字为空 | 400 | `EMPTY` | `说点什么再发送。` |
  | 超过 300 字 | 400 | `TOO_LONG` | `单条最多 300 字。` |
  | 主持忙（含服务返回 Busy） | 409 | `BUSY` | `主持正在回应` |
  | 间隔不足 2 秒 | 429 | `TOO_FAST` | `发得太快了，请稍等一下。` |
  | 电脑没装载本局 | 409 | `NOT_READY` | `电脑还没有开始本局。` |
  | 电脑没选主持模型 | 409 | `NOT_READY` | `电脑还没有选好主持模型。` |
  | 接受 | 202 | `ACCEPTED` | `已发送` |
  | 提案 | 202 | `PROPOSED` | `已提议，等电脑确认` |

- 检查顺序（原样）：会话键 → 已认领角色 → 空 → 过长 → `speech.IsBusy`（直接 `BUSY`，不占用 2 秒额度）→ 2 秒额度（在锁里「检查并预留」当前时间戳）→ `speech.SubmitAsync`；宿主返回 `Busy`/`Empty`/`NoSession`/`NoModel` 时把预留的时间戳还原成之前的值（被拒的不占额度），`Accepted`/`Proposed` 保留。
- 署名用的角色名：该会话认领的 `TabletRole.DisplayName`。
- `GET /tablet/public` 里 `host.busy` = `speech.IsBusy`，`host.speaking` = `speech.IsSpeaking`。
- 停止：`POST /tablet/say/stop` 要求有会话键和已认领角色（否则同上 401/403）；调用 `speech.StopSpeaking()`，返回 200 `{ "stopped": true|false }`。
- 前置：`ITabletSpeech`、`TabletSpeechOutcome`、`TabletSpeechAck` 已由上一张卡合入 `src/LocalDm.Core/Tablet/ITabletSpeech.cs`，以仓库里的文件为准。

## Files
may change: `src/LocalDm.Tablet/TabletSpeechService.cs`, `src/LocalDm.Tablet/TabletWebHost.cs`, `tests/LocalDm.Tests/TabletSpeechTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 4)
1. 新建 `src/LocalDm.Tablet/TabletSpeechService.cs`（命名空间 `LocalDm.Tablet`，中文注释风格同 `TabletSessions.cs`）：
   - `public enum TabletSayCode { NoSessionKey, NoRole, Empty, TooLong, Busy, TooFast, NotReady, Accepted, Proposed }`；`public sealed record TabletSayResult(TabletSayCode Code, int HttpStatus, string CodeText, string Message, Task Completion)`，`Task Completion` 在拒绝时为 `Task.CompletedTask`，其余为宿主返回的 `Completion`。
   - `public sealed class TabletSpeechService(TabletSpikeState state, ITabletSpeech speech, TimeProvider time)`，成员：`public const int MaxLength = 300; public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);`；`public bool HostBusy => speech.IsBusy; public bool HostSpeaking => speech.IsSpeaking;`
   - `public async Task<TabletSayResult> SayAsync(string? sessionKey, string? text)`：严格按 Parameters 里的检查顺序和表格返回；2 秒额度用私有 `Dictionary<string, DateTimeOffset> _last` 加 `lock`：锁内读出 `previous`，若存在且 `now - previous < MinInterval` 返回 `TooFast`，否则写入 `now` 并记下 `previous`（可能为空）；`speech.SubmitAsync` 返回后若 Outcome 不是 `Accepted`/`Proposed`，锁内把 `_last[key]` 还原成 `previous`（`previous` 为空则 `Remove`）。`now` 用 `time.GetUtcNow()`。
   - 拒绝的 `Outcome` 到返回码的映射：`Busy → Busy`、`Empty → Empty`、`NoSession → NotReady`（文案「电脑还没有开始本局。」）、`NoModel → NotReady`（文案「电脑还没有选好主持模型。」）。
   - `public bool Stop(string? sessionKey, out TabletSayCode failure)`：无键 → `NoSessionKey`；未认领 → `NoRole`；否则 `failure = Accepted` 并返回 `speech.StopSpeaking()`。
   - 同文件里写一个 `internal sealed class NoTabletSpeech : ITabletSpeech`：`IsBusy`、`IsSpeaking` 为 false，`SubmitAsync` 返回 `TabletSpeechAck.Rejected(TabletSpeechOutcome.NoSession)`，`StopSpeaking` 返回 false。
2. `src/LocalDm.Tablet/TabletWebHost.cs`：
   - `Create` 签名末尾加 `ITabletSpeech? speech = null, TimeProvider? time = null`；服务注册处（`builder.Services.AddSingleton(source);` 之后）加 `builder.Services.AddSingleton(speech ?? new NoTabletSpeech());`、`builder.Services.AddSingleton(time ?? TimeProvider.System);`、`builder.Services.AddSingleton<TabletSpeechService>();`。
   - 在 `app.MapGet("/tablet/public", …)` 之前加两个接口：
     - `app.MapPost("/tablet/say", async (TabletSpeechService service, HttpContext context) => …)`：读 JSON body（`System.Text.Json`，类型 `record SayRequest(string? Text)`，body 缺失或不是 JSON → 当作 `Text = null`，即 `EMPTY`），`var result = await service.SayAsync(TabletSessions.KeyFor(context), request?.Text);` 返回 `Results.Json(new { ok = result.Code is Accepted or Proposed, code = result.CodeText, message = result.Message }, statusCode: result.HttpStatus)`。
     - `app.MapPost("/tablet/say/stop", (TabletSpeechService service, HttpContext context) => …)`：失败时 401/403 同上表，成功返回 `Results.Json(new { stopped })`。
   - `/tablet/public` 的返回对象里加 `host = new { busy = service.HostBusy, speaking = service.HostSpeaking }`（给 lambda 加参数 `TabletSpeechService speechService`）。
3. 新建 `tests/LocalDm.Tests/TabletSpeechTests.cs`（命名空间 `LocalDm.Tests`），测试辅助（本文件内私有）：`FakeSpeech : ITabletSpeech`（`Outcome`、`Busy` 可设；记录 `Calls: List<(string role,string text)>`；`SubmitAsync` 在 `Outcome` 为 `Accepted`/`Proposed` 时记录并返回 `new TabletSpeechAck(Outcome, Task.CompletedTask)`，否则 `Rejected`；`StopCalls`）、`ManualTime : TimeProvider`（`GetUtcNow()` 可手动前进）、一个只含 2 个角色的最小 `ITabletContent` 实现（`grep -n "interface ITabletContent" -A40 src/LocalDm.Tablet/ITabletContent.cs` 看成员；`Cards` 空、`RoleBooks` 空字典、`TryGetCardImage`/`TryGetRoleSourcePage` 返回 false）。用 `TabletSpikeState` + `TabletSpeechService` 直接测规则：
   - `Unclaimed_session_cannot_speak`、`Missing_key_is_rejected`（`NO_ROLE` / `NO_SESSION_KEY`，`FakeSpeech.Calls` 为空）。
   - `Empty_and_too_long_are_rejected`：空白 → `EMPTY`；301 个字 → `TOO_LONG`；恰好 300 个字 → `ACCEPTED`。
   - `Second_send_within_two_seconds_is_too_fast`：第一次 `ACCEPTED`，同一键 1.9 秒后 → `TOO_FAST`，2.0 秒后 → `ACCEPTED`；另一块平板（另一个键、另一个角色）不受影响。
   - `Rejected_by_host_does_not_use_the_quota`：`Outcome = Busy` 时返回 `BUSY`；随后改成 `Accepted`，马上再发 → `ACCEPTED`（不是 `TOO_FAST`）。
   - `Host_busy_is_checked_before_quota`：`FakeSpeech.Busy = true` → `BUSY`，`FakeSpeech.Calls` 为空。
   - `Proposed_and_not_ready_map_to_the_right_codes`。
   - `Signature_role_name_comes_from_the_claimed_role`：`FakeSpeech.Calls[0].role` 等于认领角色的 `DisplayName`。
   - `Concurrent_double_tap_from_one_tablet_sends_once`：同一键用 `Task.WhenAll` 同时发 20 次、`ManualTime` 不动 → 只有 1 次 `ACCEPTED`，其余 `TOO_FAST`，`FakeSpeech.Calls` 恰好 1 条。
   - `Two_tablets_at_once_only_one_reaches_a_busy_host`：`FakeSpeech` 做成「第一个调用成功并把 `Busy` 置 true」的版本（原子：用 `Interlocked.CompareExchange`），两块平板用 `Task.WhenAll` 同时发 → 一个 `ACCEPTED`，一个 `BUSY`。
   - HTTP 层一个用例 `Say_endpoint_status_codes_and_public_host_state`：进程内 `TabletWebHost.Create(content, new EmptyPublicFeedSource(), ["--urls", $"http://127.0.0.1:{freePort}"], <临时目录>, fakeSpeech, manualTime)`，`await app.StartAsync()`，`HttpClient` 带 `CookieContainer`：先 `GET /` 拿 Cookie（`TabletBlazorHost`/`TabletPublicFeedTests` 里有相同的取 Cookie 写法，参照；若 `GET /` 因没有发布产物而渲染失败，改为直接 `POST /tablet/claim?roleId=…` 让服务发 Cookie）；`POST /tablet/say` 未认领 → 403 且 JSON `code == "NO_ROLE"`；认领后发 `{"text":"你好"}` → 202；`fakeSpeech.Busy = true` 后 `GET /tablet/public` 的 `host.busy == true`；`POST /tablet/say/stop` → 200。最后 `await app.StopAsync()`。若这个用例因宿主环境起不来，保留其余用例并在回复里写明原因，不要改产品代码绕过。
4. 跑 Check；若 `TabletWebHost.Create` 的其它调用点（`Program.cs`、`TabletJoinViewModel.cs`）因新增可选参数不需要改，就不要改它们。

## Check
`dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletSpeechTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不改 `TabletSpikeState` 的认领规则、不改 `/tablet/claim`、`/tablet/release`、角色本与卡片接口；不动 `.razor` 页面（下一张卡）。
- 平板服务里不写任何发送逻辑：忙碌判断、署名、意图识别、写记忆都在 `ITabletSpeech` 另一侧，这里只做平板侧规则并转发。
- 不要把角色本、手册、真相、模型备忘放进任何返回。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

## Acceptance
- verdict: accepted
- files: src/LocalDm.Tablet/TabletSpeechService.cs, src/LocalDm.Tablet/TabletWebHost.cs, tests/LocalDm.Tests/TabletSpeechTests.cs
- check: `dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletSpeechTests"` → exit 2 (run by run-card)
- tokens:
  - round 1: exec custom-local:antigravity/gemini-3.8-flash: fresh 191k, cached 3326k, out 24k  (agy-tablet-speech-service.jsonl)
- claude fix: 测试 Proposed_and_not_ready_map_to_the_right_codes 缺少推进时钟（第二次发送落在 2 秒间隔内），补两处 time.Advance(3 秒)；服务代码无改动。recheck exit=0；全量 LocalDm.Tests 427（421 过/6 跳过/0 败）、UiTests 48 过。
- deferred: 无
