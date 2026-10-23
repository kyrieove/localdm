# tablet-speech-gate — 平板发言的统一入口：HostViewModel.SubmitTabletSpeech 与电脑输入框共用提问逻辑和 IsBusy

## Goal
- `LocalDm.Core.Tablet` 里有端口 `ITabletSpeech`（给平板服务调用）及其结果类型；`HostViewModel.SubmitTabletSpeech(roleName, text)` 实现「同步接单」：在第一个 `await` 之前就判定接受或拒绝。
- 电脑输入框的 `SendAsync` 与平板发言共用同一段提问逻辑和同一个 `IsBusy` 闸门。现有代码里，从 `IsBusy` 检查到 `IsGenerating = true` 之间没有任何 `await`（`PrepareTurnAsync` 在需要整理上下文时先同步置 `IsGenerating = true`，不需要时同步返回），所以只要调用都在 UI 线程上，就不会有两个请求同时被接受。**这个性质要被测试钉住，不要再加第二个标志位。**
- 平板发言在电脑对话里是署名玩家消息「【角色名】原话」，进模型记忆、进公开消息；电脑输入框的话不署名。
- 新增测试覆盖：并发只接受一个、AI 回答中途被拒、朗读中被拒、署名且进记忆、提案、`/compact` 与「压缩上下文」当普通话。

## Risk
medium — 用户 2026-10-23 决定：计划审查已改两轮，降为 medium；Claude 仍独立重算。要独立重算的关键数字：N 个并发 `SubmitTabletSpeech`（含与 `SendAsync` 同时）里被接受的恰好 1 个，`gateway.CompletionCount` 恰好 1。

## Parameters
- 结果枚举 `TabletSpeechOutcome`（原样名字）：`Accepted`、`Proposed`、`Busy`、`Empty`、`NoSession`、`NoModel`。
- 署名格式（原样）：`【` + roleName + `】` + 去掉首尾空白的原话。例：`【张三】我想去书房`。
- 提案提示（本机提示，不进公开消息，原样）：`$"{roleName} 提议：进入下一环节。请在电脑上确认后再推进。"` 对应 `PublicAction.Advance`；`PublicAction.Reveal` 用 `$"{roleName} 提议：揭秘。请在电脑上确认后再操作。"`。
- 不做长度/频率限制（那是平板服务的事，下一张卡）。
- 来源：设计 `docs/plans/2026-10-23-tablet-live-chat.md` 第 3、6、7 条；`docs/decisions/0007-tablet-live-chat.md`。

## Files
may change: `src/LocalDm.Core/Tablet/ITabletSpeech.cs`, `src/LocalDm.App/ViewModels/HostViewModel.cs`, `tests/LocalDm.Tests/HostConversationTests.cs`, `tests/LocalDm.Tests/DmConversationTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 5)
1. 新建 `src/LocalDm.Core/Tablet/ITabletSpeech.cs`（命名空间 `LocalDm.Core.Tablet`，文件风格参照同目录 `PublicFeedSnapshot.cs`：中文 XML 注释）：
   ```csharp
   public enum TabletSpeechOutcome { Accepted, Proposed, Busy, Empty, NoSession, NoModel }
   public sealed record TabletSpeechAck(TabletSpeechOutcome Outcome, Task Completion)
   {
       public static TabletSpeechAck Rejected(TabletSpeechOutcome outcome) => new(outcome, Task.CompletedTask);
   }
   public interface ITabletSpeech
   {
       bool IsBusy { get; }
       bool IsSpeaking { get; }
       Task<TabletSpeechAck> SubmitAsync(string roleName, string text);
       bool StopSpeaking();
   }
   ```
   `Completion` 在这一轮（模型回答加朗读）全部结束时完成；平板服务不等它，测试等它。
2. `src/LocalDm.App/ViewModels/HostViewModel.cs`：把 `SendAsync`（`grep -n "public async Task SendAsync"`，约 818–868 行）拆成两段，不新增标志位、不改 `IsBusy`：
   - `SendAsync()` 保持签名与行为：`IsBusy` 返回、`InputText` 去空白后为空返回、`Compact` 意图走原来的 `ManualCompactAsync` 分支；这些之后写 `InputText = string.Empty; await AskCoreAsync(text);`。原来的 `AppendPlayer(text)` 移到 `AskCoreAsync` 的第一行（`InputText` 清空和 `AppendPlayer` 在同一个同步段里，先后对外没有差别）。
   - 新增 `private async Task AskCoreAsync(string text)`：函数体是原 `SendAsync` 中 `AppendPlayer(text)` 起到末尾 `await RunTurnAsync(...)` 的全部内容（模型 ID 检查、装载检查、`++_generation`、`PrepareTurnAsync`、`RunTurnAsync`），**唯独不含原来紧跟在 `AppendPlayer(text)` 后面的那句 `InputText = string.Empty;`**——它只留在 `SendAsync` 里。平板发言绝不能清掉电脑输入框里还没发的草稿。其余一个字不改。
3. 同一文件，在 `SendAsync` 之后加平板入口（只写成公开方法，由下一阶段的 App 层桥接类在 UI 线程调用）：
   ```csharp
   /// 平板发言：同步接单，不等整轮结束。必须在 UI 线程调用。
   public TabletSpeechAck SubmitTabletSpeech(string roleName, string text)
   ```
   逻辑按顺序：
   - `text` 去首尾空白后为空 → `TabletSpeechAck.Rejected(Empty)`。
   - `IsBusy` → `Rejected(Busy)`（第一道门）。
   - `string.IsNullOrWhiteSpace(_settingsProvider().ModelId)` → `Rejected(NoModel)`；`_session.Package is null || _session.Session is null` → `Rejected(NoSession)`（这两项只拒绝，不 `AppendPlayer`、不 `AppendHost`）。
   - `var signed = $"【{roleName}】{trimmed}";`
   - `_router.Route(trimmed).Kind == HostIntentKind.ProposeCommand`：`AppendPlayer(signed)`；按 `ProposedAction` 选 Parameters 里的提示文案 `AppendLocalHint(...)`；返回 `new TabletSpeechAck(Proposed, Task.CompletedTask)`。不请求模型、不写入模型记忆、不占闸门。
   - 其余（含整句 `压缩上下文`、`/compact`：在平板上当普通话，不要调用 `ManualCompactAsync`）：`return new TabletSpeechAck(Accepted, AskCoreAsync(signed));` —— `AskCoreAsync` 在第一个真正的 `await` 之前同步执行，所以返回时 `IsBusy` 已为真。
   - 另加 `public bool StopSpeakingForTablet()`：`IsSpeaking` 为真时 `StopCommand.Execute(null)` 并返回 true，否则返回 false（生成阶段不打断）。
4. `tests/LocalDm.Tests/HostConversationTests.cs`：用文件里现成的 `NewHostAsync`、`CountingGateway`（`Gate`/`Gates`/`CompletionCount`/`Requests`）、`CountingTts`、`WaitUntilAsync`；先 `grep -n "Gates\|Gate " tests/LocalDm.Tests/HostConversationTests.cs` 看它们怎么用，`grep -n "MagpieMessage" src/LocalDm.Core/Ports/IMagpieGateway.cs` 看请求里消息的字段。新增用例：
   - `Tablet_speech_is_signed_in_the_timeline_and_in_the_memory`：`LoadDemoAsync` 后 `host.SubmitTabletSpeech("张三", " 我想去书房 ")`，`await ack.Completion`；`Outcome == Accepted`；`host.Messages` 里新增的玩家消息文本为 `【张三】我想去书房`；然后 `host.InputText = "接着说"; await host.SendAsync();`，断言第二次请求（`gateway.Requests[^1]`）的历史里 user 消息中恰好有一条等于 `【张三】我想去书房`（在该请求的消息列表里数）；同时电脑输入框发的那句 `接着说` 不带署名。另外：在 `SubmitTabletSpeech` 之前先把 `host.InputText = "电脑草稿"`，提交后 `host.InputText` 仍等于 `电脑草稿`（平板发言不清电脑草稿）；之后再用它做那次 `SendAsync` 的输入前先改成 `接着说`。
   - `Two_tablet_submits_at_once_accept_exactly_one`：`gateway.Gate` 挂住；在同一个同步段里连续调用 `SubmitTabletSpeech("甲","一")`、`("乙","二")`、`("丙","三")`（先不 await）→ 第一个 `Accepted` 且此刻 `host.IsBusy` 为 true（钉住「返回时已忙」这个性质），其余 `Busy`；放开 `Gate`；`await` 第一个的 `Completion`；`gateway.CompletionCount == 1`；玩家消息恰好 1 条。
   - `Desktop_send_and_tablet_submit_at_once_accept_exactly_one`：两种顺序各一个用例：电脑先（`host.InputText = "电脑的话"; var desktop = host.SendAsync();` 不 await，再 `SubmitTabletSpeech` → `Busy`）；平板先（`SubmitTabletSpeech` 成功后再 `host.InputText = "电脑的话"; await host.SendAsync();` 不产生新消息：`host.Messages` 里没有"电脑的话"，且 `CompletionCount` 放开后为 1）。
   - `Tablet_submit_while_generating_or_speaking_is_busy`：`Gate` 挂住时 `Busy`；放开后朗读阶段（`CountingTts` 的可挂住的 speak 门——`grep -n "SpeakGate\|ReplayGate" tests/LocalDm.Tests/HostConversationTests.cs`；没有 `SpeakGate` 就给 `CountingTts` 加一个同样写法的）`host.IsSpeaking` 时再提交为 `Busy`；`StopSpeakingForTablet()` 在朗读时返回 true、生成中返回 false。
   - `Tablet_advance_phrase_is_only_a_proposal`：`SubmitTabletSpeech("甲", "进入下一环节")` → `Proposed`；`gateway.CompletionCount == 0`；`host.Session.Revision` 不变；`host.IsBusy` 为 false；`host.Messages` 里有玩家消息 `【甲】进入下一环节` 和一条 `IsLocalOnly` 的提示（含 `提议`）。
   - `Tablet_compact_phrases_are_plain_speech`（`[Theory]`，数据 `"压缩上下文"`、`"/compact"`）：`SubmitTabletSpeech("甲", phrase)` → `Accepted`，`await Completion`，`gateway.CompletionCount == 1`，且 `gateway.Requests[^1]` 里出现完整的 `【甲】` + phrase（说明走了普通问答而不是整理）。
   - `Tablet_submit_rejects_empty_no_session_and_no_model`：未 `LoadDemoAsync` 时 `NoSession`；`ModelId` 空时 `NoModel`；纯空白 `Empty`；三种情况下 `host.Messages` 都不新增。

5. `tests/LocalDm.Tests/DmConversationTests.cs`（它有自己的 `ScriptedGateway`：`CompactionGates` 按整理请求的序号挂门、`CompactionRequests(gateway)`、`TalkUntilAsync`、`LongQuestion`、`NewHostAsync(gateway)` 窗口 10_000；先读 `ScriptedGateway.CompleteWithUsageAsync` 看 `CompactionGates` 的用法）新增 `Tablet_and_desktop_submits_are_busy_while_automatic_compaction_is_pending`，钉住「整理上下文期间已经忙」这条性质：
   - `gateway.CompactionGates[1] = new TaskCompletionSource(...)`（第一次整理请求挂住）。
   - 循环最多 40 轮：`host.InputText = LongQuestion(i); var pending = host.SendAsync();` 然后 `if (CompactionRequests(gateway).Count == 1) break; await pending;`（整理请求是同步发到网关的；若实测要让出一次再出现，用 `WaitUntilAsync(() => CompactionRequests(gateway).Count == 1)` 短等）。此时那一次 `SendAsync` 停在「等整理结果」，整理调用恰好 1 次，回答调用（非整理）还没发。
   - 断言：`host.IsBusy` 为 true；`host.SubmitTabletSpeech("甲", "整理期间的话")` → `Busy`；再设 `host.InputText = "电脑的话"; await host.SendAsync();` 立刻返回且 `host.Messages` 里没有 `电脑的话`，也没有 `整理期间的话`；整理调用数仍为 1。
   - 放开 `CompactionGates[1]`，`await pending`；断言整理调用恰好 1 次、本轮回答调用恰好 1 次（非整理请求的计数比放开前多 1），`host.IsBusy` 为 false；随后 `SubmitTabletSpeech("甲", "整理之后的话")` → `Accepted`，`await Completion`。
   - **做不出这个用例就停下来，在回复里写明卡在哪里，不要删用例、不要改产品代码。**

## Check
`dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.HostConversationTests" --filter-class "LocalDm.Tests.DmConversationTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不动朗读/生成的状态切换顺序（`RunTurnAsync` 里 `IsGenerating`/`IsSpeaking` 的先后）、不动 `HostIntentRouter`、`PublicMessagePublisher`、`CommandDispatcher`；不动电脑端 UI（xaml）。
- 不在 `HostViewModel` 里加 `Dispatcher`、线程切换、锁或新的忙碌标志位：它仍然只在 UI 线程被调用，`IsBusy` 的定义不变。
- 不删除、不跳过已有测试。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

## Acceptance
- verdict: accepted
- files: src/LocalDm.App/ViewModels/HostViewModel.cs, src/LocalDm.Core/Tablet/ITabletSpeech.cs, tests/LocalDm.Tests/DmConversationTests.cs, tests/LocalDm.Tests/HostConversationTests.cs
- check: `dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.HostConversationTests" --filter-class "LocalDm.Tests.DmConversationTests"` → exit 2 (run by run-card)
- tokens:
  - round 1: exec custom-local:antigravity/gemini-3.8-flash: fresh 269k, cached 5426k, out 12k  (agy-tablet-speech-gate.jsonl)
- claude fix: 测试断言——请求里历史与当前问题合并在同一条 user 消息，卡里假设的「单独一条」不成立，改为数署名原话在整个请求中恰好出现一次。recheck exit=0；全量 LocalDm.Tests 416（410 过/6 跳过/0 败）、UiTests 48 过。
- deferred: 无
