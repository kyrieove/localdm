# tablet-ui-projection — 平板界面投影（只含公开内容）加防泄露测试

## Goal
- 新增 `TabletUiProjector.Project(package, session, feed, hostBusy, hostSpeaking)`，返回只含公开内容的 `TabletUiProjection`：当前环节、环节列表（已过/当前/未到）、七个侧栏按钮的显示与可用状态、公开对话、揭秘文本（仅解锁后）、票数汇总。数据来源是 `PublicSessionProjector.Project` 的结果、`PublicFeedSnapshot` 和阶段的流程标记，不碰段落正文、角色本、手册、真相原文、备忘、上下文占用。
- 防泄露测试通过：在 Prepared、各环节 Playing、Revealed、揭秘后 Paused 五类状态下，把投影序列化成 JSON，真相、角色本、线索卡正文、主持专用页正文、段落 ID、阶段原本条件、备忘与上下文占用的标记词一个都不能出现（揭秘解锁后真相只能出现在 `RevealedTexts` 里）；投影类型图里不能有带这些名字的属性，也不能引用 `ScriptPackage`、`Session`、`CanonicalSegment`、`TruthSection` 类型。

## Risk
medium — 本应是 high（秘密隔离），计划审查两轮用完，用户裁决降为 medium 派发，汇报标 unreviewed-fix；Claude 仍独立重算关键数字：Revealed 状态下投影 JSON 里 `PublicTruth` 出现次数恰好 1（在 `RevealedTexts`），`RoleBookText`/`ClueCardText`/`HostOnlyText`/`原本条件` 出现次数都是 0；Playing 状态下 `PublicTruth` 出现次数 0。

## Parameters
- 命名空间与位置：记录和枚举放 `src/LocalDm.Core/Tablet/TabletUiProjection.cs`（命名空间 `LocalDm.Core.Tablet`）；投影器放 `src/LocalDm.Core/Game/TabletUiProjector.cs`（命名空间 `LocalDm.Core.Game`）。
- 常量文案：`TabletUiProjector.ControlsNote = "请在电脑上操作"`。
- 七个按钮（`TabletUiControlKind`，按此顺序放进 `Controls`）：`Start`、`Advance`、`ReleaseMaterials`、`SubmitVote`、`Reveal`、`Complete`、`PauseResume`。
- 按钮规则（与 `src/LocalDm.App/ViewModels/SessionViewModel.cs` 第 203–320 行一致；`p` 是 `PublicSessionProjector.Project` 的结果，`phase` 是 `package.Phases` 里 `PhaseId == p.CurrentPhase.PhaseId` 的那个，`playing` 指 `p.State == nameof(SessionState.Playing)`，`pending` 指 `phase.MaterialIds` 里不在 `p.ReleasedMaterialIds` 中的编号（保持原顺序，按 `StringComparer.Ordinal` 比较）；`hostBusy` 为入参）：
  - Start：Visible = `p.State == nameof(SessionState.Prepared)`；Label `"开始这一局"`；Enabled = Visible && !hostBusy。
  - Advance：Visible = `playing && p.CurrentPhase.AllowedActions.Contains(PublicAction.Advance) && !(phase.IsVotePhase && p.VoteSummary is null) && !phase.IsRevealPhase`；Label = `p.CurrentPhase.AdvanceLabel` 非空白则用它，否则 `"结束本局"`；Enabled = Visible && !hostBusy && !(p.CurrentPhase.RequiresAllMaterials && pending.Count > 0)。
  - ReleaseMaterials：Visible = `playing && AllowedActions.Contains(PublicAction.ReleaseMaterials) && pending.Count > 0`；Label = pending 非空则 `$"发放材料 {pending[0]}"` 否则 `"发放材料"`；Enabled = Visible && !hostBusy。
  - SubmitVote：Visible = `playing && phase.IsVotePhase && p.VoteSummary is null`；Label `"确认提交汇总"`；Enabled 恒为 false（要在电脑上录入票数，平板上没有输入）。
  - Reveal：Visible = `playing && phase.IsRevealPhase`；Label = `p.CurrentPhase.RevealLabel` 非空白则用它，否则 `"公开真相"`；Enabled = Visible && !hostBusy。
  - Complete：Visible = `p.State == nameof(SessionState.Revealed) || (p.State == nameof(SessionState.Paused) && p.RevealedTexts is { Count: > 0 })`；Label `"进入复盘"`；Enabled = Visible && !hostBusy。
  - PauseResume：Visible = `p.State` 是 Playing、Paused 或 Revealed；Label = Paused 时 `"继续本局"`，否则 `"暂停本局"`；Enabled = Visible && !hostBusy。
- 环节列表：来自 `p.PhaseTrack`，当前下标 = `PhaseId == p.CurrentPhase.PhaseId` 的那项；下标小于当前为 `Done`，等于为 `Current`，大于为 `Upcoming`。
- 消息与序号：`Messages` 和 `NextSequence` 原样取自入参 `feed`；`feed.SessionId` 必须等于 `session.SessionId`，否则抛 `DomainRuleException(DomainErrorCode.INVALID_INPUT, "公开消息不属于本局。")`。
- 不放进投影的东西（防泄露测试里按这些名字查）：真相、备忘、上下文占用、角色本、手册、段落正文与段落 ID、阶段原本条件（`ScriptPhase.Conditions`）。

## Files
may change: `src/LocalDm.Core/Tablet/TabletUiProjection.cs`, `src/LocalDm.Core/Game/TabletUiProjector.cs`, `tests/LocalDm.Tests/TabletUiProjectionTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`, `src/LocalDm.Core/Game/PublicSessionProjector.cs`, `src/LocalDm.Core/Domain/`, `src/LocalDm.App/ViewModels/`, `tests/LocalDm.Tests/SyntheticPackage.cs`, `tests/LocalDm.Tests/PhaseFlowTests.cs`

## Steps (N = 3)
1. 新建 `src/LocalDm.Core/Tablet/TabletUiProjection.cs`（文件级命名空间 `LocalDm.Core.Tablet`，`using LocalDm.Core.Domain;`），内容照下面写，不加别的成员：
   ```csharp
   public enum TabletUiPhaseStatus { Done = 0, Current = 1, Upcoming = 2 }

   public enum TabletUiControlKind { Start = 0, Advance = 1, ReleaseMaterials = 2, SubmitVote = 3, Reveal = 4, Complete = 5, PauseResume = 6 }

   public sealed record TabletUiPhaseItem
   {
       public required string PhaseId { get; init; }
       public required string Title { get; init; }
       public required TabletUiPhaseStatus Status { get; init; }
   }

   public sealed record TabletUiControl
   {
       public required TabletUiControlKind Kind { get; init; }
       public required string Label { get; init; }
       public required bool Visible { get; init; }
       public required bool Enabled { get; init; }
   }

   public sealed record TabletUiProjection
   {
       public required string SessionId { get; init; }
       public required string Title { get; init; }
       public required string State { get; init; }
       public required int Revision { get; init; }
       public required string PhaseId { get; init; }
       public required string PhaseTitle { get; init; }
       public required int PhaseIndex { get; init; }
       public required int PhaseCount { get; init; }
       public string? Hint { get; init; }
       public required IReadOnlyList<TabletUiPhaseItem> Phases { get; init; }
       public required IReadOnlyList<TabletUiControl> Controls { get; init; }
       public required string ControlsNote { get; init; }
       public required int PendingMaterialCount { get; init; }
       public required int ReleasedMaterialCount { get; init; }
       public PublicVoteSummaryView? VoteSummary { get; init; }
       public IReadOnlyList<string>? RevealedTexts { get; init; }
       public required IReadOnlyList<PublicMessage> Messages { get; init; }
       public required long NextSequence { get; init; }
       public required bool HostBusy { get; init; }
       public required bool HostSpeaking { get; init; }
   }
   ```
2. 新建 `src/LocalDm.Core/Game/TabletUiProjector.cs`：`public static class TabletUiProjector`，常量 `public const string ControlsNote = "请在电脑上操作";`，方法 `public static TabletUiProjection Project(ScriptPackage package, Session session, PublicFeedSnapshot feed, bool hostBusy, bool hostSpeaking)`。写法：
   - 先检查 `feed.SessionId == session.SessionId`（`StringComparison.Ordinal`），不等就抛 Parameters 里写的异常；这一检查必须在其它所有工作之前（任何调用路径都先过它，没有缓存、提前返回或回退路径绕开它）。
   - `var p = PublicSessionProjector.Project(package, session);`（不要自己再算阶段、揭秘、段落筛选）。
   - `var phase = package.Phases.First(x => x.PhaseId == p.CurrentPhase.PhaseId);`，只读 `phase.IsVotePhase`、`phase.IsRevealPhase`、`phase.MaterialIds`；**不要读** `phase.Conditions`、`package.Segments`、`package.Truth`、`package.Roles`、`package.Spans`、`package.SourceFiles`。
   - 按 Parameters 的规则算出 `pending` 和七个按钮；`Phases` 按 Parameters 的规则从 `p.PhaseTrack` 生成；`PhaseId/PhaseTitle/PhaseIndex/PhaseCount/Hint` 取自 `p.CurrentPhase`（`Title`、`Index`、`Count`、`Hint`）；`Title = p.PublicTitle`，`State = p.State`，`Revision = p.Revision`，`ControlsNote = ControlsNote`，`PendingMaterialCount = pending.Count`，`ReleasedMaterialCount = p.ReleasedMaterialIds.Count`，`VoteSummary = p.VoteSummary`，`RevealedTexts = p.RevealedTexts`，`Messages = feed.Messages`，`NextSequence = feed.NextSequence`，`HostBusy = hostBusy`，`HostSpeaking = hostSpeaking`。
3. 新建 `tests/LocalDm.Tests/TabletUiProjectionTests.cs`（`namespace LocalDm.Tests;`，`public sealed class TabletUiProjectionTests`），用以下**现成写法**（已查过，不要再去读别的文件探索）：
   - usings：`System.Text.Json`、`System.Reflection`、`LocalDm.Core.Domain`、`LocalDm.Core.Game`、`LocalDm.Core.Tablet`。
   - `private static readonly ScriptPackage Package = SyntheticPackage.Create();`
   - 现成会话（来自 `PhaseFlowTests` 的 `internal static` 辅助，可直接调用）：`PhaseFlowTests.Started()`（Playing，环节 phase-read）；`PhaseFlowTests.AdvancedTo(2)`（搜证，有材料 M1、M2）；`PhaseFlowTests.AdvancedTo(4)`（投票，未汇总）；`PhaseFlowTests.AdvancedTo(5)`（真相环节，Playing）；`PhaseFlowTests.RevealPhase()`（Revealed）；Prepared 用 `SyntheticPackage.NewSession()`；揭秘后暂停：`GameEngine.Apply(r, Package, SyntheticPackage.Command(GameCommandType.Pause, r.Revision, "op-pause"))`，`r = PhaseFlowTests.RevealPhase()`。
   - 现成公开消息：
     ```csharp
     private static PublicFeedSnapshot Feed(string sessionId = "session-1") => new()
     {
         SessionId = sessionId,
         Messages =
         [
             new PublicMessage { SessionId = sessionId, MessageId = "m1", Author = PublicMessageAuthor.Player, Text = "【甲】大家好", CreatedAt = DateTimeOffset.UnixEpoch, Kind = PublicMessageKind.PlayerSpeech, Sequence = 1 },
             new PublicMessage { SessionId = sessionId, MessageId = "m2", Author = PublicMessageAuthor.Host, Text = "欢迎各位", CreatedAt = DateTimeOffset.UnixEpoch, Kind = PublicMessageKind.HostReply, Sequence = 2 },
         ],
         NextSequence = 2,
     };
     private static TabletUiProjection Proj(Session s, bool busy = false, bool speaking = false) => TabletUiProjector.Project(Package, s, Feed(s.SessionId), busy, speaking);
     private static TabletUiControl Ctl(TabletUiProjection p, TabletUiControlKind k) => p.Controls.Single(c => c.Kind == k);
     ```
   - 用例（xunit `[Fact]`）：
     - `Leak_guard_json_has_no_secret_markers_in_any_state`：对 Prepared、`Started()`、`AdvancedTo(2)`、`AdvancedTo(3)`、`AdvancedTo(4)`、`AdvancedTo(5)`、`RevealPhase()`、揭秘后暂停这 8 个会话各投影并序列化。**序列化必须用 `new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }`**（默认会把中文转义成 \u 序列，中文标记词的 `Contains`/`Split` 会恒为 0、负向断言形同虚设）。每个 JSON 都不含 `SyntheticPackage.RoleBookText`、`ClueCardText`、`HostOnlyText`、`"原本条件"`、`Package.Segments` 里**每一个** `SegmentId`（夹具里的 `seg-intro`、`seg-search`、`seg-discuss`、`seg-vote`、`seg-truth`、`seg-secret`、`seg-clue`、`seg-host` 全部要查，用 `foreach (var seg in Package.Segments)` 循环）、`"备忘"`、`"上下文占用"`、`"privateStory"`（忽略大小写，用 `StringComparison.OrdinalIgnoreCase`）；其中前 6 个（未解锁）也不含 `SyntheticPackage.PublicTruth`，且 `RevealedTexts` 为 null；后 2 个（已解锁）的 JSON 里 `PublicTruth` 出现次数恰好 1，`RevealedTexts` 含 `PublicTruth`。出现次数用 `json.Split(SyntheticPackage.PublicTruth).Length - 1` 数。已解锁的两个会话再用 `JsonDocument.Parse(json)` 取 `RootElement.GetProperty("RevealedTexts")`，断言它是数组且恰有一个元素等于 `PublicTruth`；`Messages`、`Phases`、`Controls`、`Hint`、`PhaseTitle` 各属性里不含 `PublicTruth`（确认真相只在 `RevealedTexts` 路径下出现）。
     - `Projection_type_graph_has_no_secret_shaped_members`：从 `typeof(TabletUiProjection)` 出发，递归（用 `HashSet<Type>` 防重复）走所有属性类型（展开 `IReadOnlyList<T>`/泛型参数/可空），只展开命名空间以 `LocalDm` 开头的类型；断言：访问到的类型里没有 `ScriptPackage`、`Session`、`CanonicalSegment`、`TruthSection`、`RoleIndex`、`ScriptPhase`；所有属性名（忽略大小写）都不包含 `truth`、`memo`、`context`、`budget`、`rolebook`、`handbook`、`manual`、`segment`、`secret`、`condition`；属性名 `RevealedTexts` 例外不会触发（它不含上面任何词），不要写特例。
     - `Controls_follow_session_state`：Prepared → 只有 Start 的 Visible 为真，Label `"开始这一局"`；`Started()` → Advance 可见且 Enabled、PauseResume 可见 Label `"暂停本局"`、Start/ReleaseMaterials/SubmitVote/Reveal/Complete 都不可见；`AdvancedTo(2)` → ReleaseMaterials 可见 Label `"发放材料 M1"` 且 `PendingMaterialCount == 2`；`AdvancedTo(4)` → SubmitVote 可见且 Enabled 为 false、Advance 不可见；`AdvancedTo(5)` → Reveal 可见 Label `"公开真相"`、Advance 不可见；`RevealPhase()` → Complete 可见 Enabled、PauseResume 可见；揭秘后暂停 → PauseResume Label `"继续本局"`、Complete 可见。
     - `Busy_host_disables_but_does_not_hide_controls`：`Proj(AdvancedTo(2), busy: true)` → Advance、ReleaseMaterials、PauseResume 仍 Visible 但 Enabled 全为 false，`HostBusy` 为真；`speaking: true` 时 `HostSpeaking` 为真。
     - `Phase_list_marks_done_current_upcoming`：`AdvancedTo(3)` → `Phases` 共 5 项，状态依次 Done、Done、Current、Upcoming、Upcoming；`PhaseIndex == 3`、`PhaseCount == 5`、`PhaseTitle == "讨论"`、`Hint == "讨论环节当前提示"`。
     - `Public_messages_pass_through_unchanged`：`var feed = Feed(); var p = TabletUiProjector.Project(Package, Started(), feed, false, false);` 断言 `Assert.Same(feed.Messages, p.Messages)`（同一实例，正文、作者、类型、序号都不可能被改），`p.NextSequence == feed.NextSequence`，`ControlsNote == "请在电脑上操作"`。
     - `Feed_from_another_session_is_rejected`：`Assert.Throws<DomainRuleException>(() => TabletUiProjector.Project(Package, Started(), Feed("other-session"), false, false))`，`Code == DomainErrorCode.INVALID_INPUT`。
   - 只在这个新测试文件里加测试；`PhaseFlowTests`、`SyntheticPackage` 不要改（它们是只读的，只调用它们已有的 `internal static` 成员）。
   - 运行 Check；任何一条断言红，先看是自己的实现还是自己的测试错，改实现或测试，不要改 `PublicSessionProjector.cs` 或别的只读文件。

## Check
`dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletUiProjectionTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不改 `PublicSessionProjector.cs`、`SessionViewModel.cs`、`src/LocalDm.Tablet/`、`src/LocalDm.App/` 下任何文件；本卡只加投影，不接线、不加接口、不加网页。
- 不读 `stories/`、`.appdata/`、`.local/`；不删除、不跳过已有测试。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

## Acceptance
- verdict: accepted
- files: src/LocalDm.Core/Game/TabletUiProjector.cs, src/LocalDm.Core/Tablet/TabletUiProjection.cs, tests/LocalDm.Tests/TabletUiProjectionTests.cs
- check: `dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.TabletUiProjectionTests"` → exit 0 (run by run-card)
- tokens:
  - round 1: exec custom-local:antigravity/gemini-3.8-flash: fresh 60k, cached 384k, out 12k  (agy-tablet-ui-projection.jsonl) · review gpt-6.1-sol: fresh 66k, cached 213k, out 1k  (codex-review-tablet-ui-projection.jsonl)
- review findings: 0 real / 0 false positive / 0 missed (medium, gpt-6.1-sol)
- unreviewed-fix: 本卡应为 high（秘密隔离）。计划审查两轮 REVISE（第二轮因我的改卡脚本报错审了旧卡，白用），按用户裁决降为 medium 派发；三条计划审查意见（JSON 中文转义、全部段落 ID、消息 Assert.Same）已改进卡并被实现采纳，但改后的卡没有再过计划审查。合并前请用户看 `tests/LocalDm.Tests/TabletUiProjectionTests.cs` 的防泄露用例。
- recompute (Claude, independent, high-tier key number): 另写临时测试递归遍历投影 JSON 的所有字符串值（已删）：Playing 环节 `PublicTruth`=0；Revealed `PublicTruth`=1 且路径 `.RevealedTexts[0]`；两种状态 `RoleBookText`/`ClueCardText`/`HostOnlyText`/`原本条件` 均为 0。与卡里预期一致。
- full suite: 构建 0 警告 0 错误；`LocalDm.Tests` 435 项（429 过 / 6 项人工授权跳过 / 0 败）；`LocalDm.UiTests` 53 过。
- deferred: 防泄露用例的输入夹具里没有真正会泄露的路径，它守的是“类型上构不出秘密 + 序列化后无标记词”；真实包（含备忘、上下文占用）的端到端泄露检查留给接线卡。
