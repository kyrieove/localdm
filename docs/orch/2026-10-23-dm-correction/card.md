# dm-correction — 点错了能改：模型听懂「点错了」后提出改正，电脑上点确认才生效

## Goal
1. 规则核心新增命令 `RevertPhase`：游戏中（`Playing`）退回上一环节。第一个环节、暂停中、已揭秘以后都拒绝。退回时已发放的材料和票数记录不动。
2. 在投票环节里重新提交票数时，替换这一轮原来的记录，不再追加一条。
3. 模型听到玩家或操作者明确说「刚才推进早了 / 票数录错了」时，先在回复里确认听懂了，再在回复最后单独写一行提议标记。软件把这一行从显示、记忆和朗读里去掉，在电脑上变成一条「DM 提议…［确认］［不用］」的确认条。点「确认」才执行：退回环节就走 `RevertPhase`；改票数就重新打开投票录入框，并预填当前票数。模型从头到尾不写状态。
4. 新增 ADR 0010，宪章升到 10.0.0，记下这次修订。
5. 构建 0 警告，`LocalDm.Tests` 与 `LocalDm.UiTests` 全部通过，含下面新增的测试。

## Risk
high — 改的是规则核心的状态迁移（`GameEngine`），后面每一局都依赖它。Claude 验收时独立核对：用 `SyntheticPackage` 从 `AdvancedTo(3)` 退回，`CurrentPhaseId` 必须是 `phase-search`、Revision 加 1、`ReleasedMaterialIds` 不变；还要核对 `phase-reveal` 揭秘后退回被拒绝。

## Parameters
- 新枚举值：`GameCommandType.RevertPhase = 8`（`src/LocalDm.Core/Domain/GameModels.cs:168` 的枚举末尾）。
- 提议标记（全角六角括号，必须单独占一行，整行去掉首尾空白后完全相等才算）：
  - 退回：`〔提议：回到上一环节〕`
  - 改票：`〔提议：重新录入票数〕`
- 退回的拒绝错误码都是 `DomainErrorCode.PHASE_CONDITION_UNMET`，文案：
  - 不在 `Playing`：`当前不在游戏中，不能退回。`
  - 已在第一个环节：`已经是第一个环节，不能再退回。`
- 状态栏文案：`CommandStatus` 里 `RevertPhase` → `$"已退回「{phase}」。"`
- 确认条文案：
  - 退回：`$"DM 提议：回到上一环节「{上一环节标题}」。"`
  - 改票：`"DM 提议：重新录入这一轮票数。"`
  - 按钮：`确认` / `不用`
- 提议此刻不能执行时（比如模型提议退回，但已在第一个环节，或已揭秘），不出确认条，改成追加一条本地提示：`"DM 提议的改正现在不能做，请直接在左侧操作。"`
- 系统提示词新增第 10 条（只在第 9 条后面追加，前 9 条一个字都不改），原文：
  `10. 玩家或操作者明确说刚才按钮点错了（例如推进得太早、票数录错了）时，先简短说明你听懂了，再在回复最后单独一行写〔提议：回到上一环节〕或〔提议：重新录入票数〕。这一行软件会藏起来，变成电脑上的确认按钮；你不能自己改流程，也不要说已经改好了。对方没说清是哪一种就先问，不要写。真相已揭秘后不提议回到上一环节。`

## Files
may change: `src/LocalDm.Core/Domain/GameModels.cs`, `src/LocalDm.Core/Game/GameEngine.cs`, `src/LocalDm.Core/Hosting/CorrectionProposal.cs`, `src/LocalDm.Core/Hosting/HostNarration.cs`, `src/LocalDm.App/ViewModels/SessionViewModel.cs`, `src/LocalDm.App/ViewModels/HostViewModel.cs`, `src/LocalDm.App/Views/HostView.xaml`, `tests/LocalDm.Tests/PhaseFlowTests.cs`, `tests/LocalDm.Tests/CorrectionProposalTests.cs`, `tests/LocalDm.Tests/DmConversationTests.cs`, `docs/decisions/0010-model-proposed-corrections.md`, `.specify/memory/constitution.md`
read-only: `stories/`, `.local/`

## Steps (N = 9)
1. `src/LocalDm.Core/Domain/GameModels.cs` → `enum GameCommandType`（第 168 行）末尾加 `RevertPhase = 8,`，上面加一行注释：`/// <summary>退回上一环节：只由主持人点确认触发（ADR 0010）。</summary>`。

2. `src/LocalDm.Core/Game/GameEngine.cs`：
   - `Apply` 里的 `switch` 加一行 `GameCommandType.RevertPhase => RevertPhase(current, package),`（放在 `Complete` 那行后面）。
   - 在 `Advance` 方法后面新增：
     ```csharp
     /// <summary>
     /// 退回上一环节（ADR 0010）：只在游戏中；已揭秘（Revealed/Completed）与暂停都不行。
     /// 已发放的材料和票数记录不动：实体卡已经发到桌上了，票数改正另走重新提交。
     /// </summary>
     private static Session RevertPhase(Session current, ScriptPackage package)
     {
         if (current.State != SessionState.Playing)
         {
             throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "当前不在游戏中，不能退回。");
         }

         var index = IndexOfPhase(package, CurrentPhase(current, package).PhaseId);
         if (index <= 0)
         {
             throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "已经是第一个环节，不能再退回。");
         }

         return current with { CurrentPhaseId = package.Phases[index - 1].PhaseId };
     }
     ```
   - `SubmitVoteSummary`（第 180–189 行的 `return current with {...}`）：`VoteHistory` 改成「这一轮已有记录就原位替换，没有才追加」：
     ```csharp
     var record = new PhaseVoteRecord { PhaseId = phase.PhaseId, Summary = votes };
     var replaced = current.VoteHistory.Any(r => r.PhaseId == phase.PhaseId);
     // 同一轮重新提交是改正：原位替换，历史里每一轮只留一条。
     ...
     VoteHistory = replaced
         ? [.. current.VoteHistory.Select(r => r.PhaseId == phase.PhaseId ? record : r)]
         : [.. current.VoteHistory, record],
     ```

3. 新文件 `src/LocalDm.Core/Hosting/CorrectionProposal.cs`（namespace `LocalDm.Core.Hosting`）：
   ```csharp
   /// <summary>模型能提议的改正。提议只到确认条，执行要主持人点（ADR 0010）。</summary>
   public enum CorrectionKind { RevertPhase, CorrectVote }

   /// <summary>
   /// 从模型回复里取出改正提议：只认单独一行、整行等于标记的写法，句子中间出现不算。
   /// 取出后这一行从文字里去掉，不上屏、不进记忆、不朗读。
   /// </summary>
   public static class CorrectionProposal
   {
       public const string RevertTag = "〔提议：回到上一环节〕";
       public const string CorrectVoteTag = "〔提议：重新录入票数〕";

       public static (string Text, CorrectionKind? Kind) Extract(string reply)
   }
   ```
   `Extract` 的写法：把 `reply` 按 `\n` 拆行（先把 `\r\n` 换成 `\n`）。逐行看：`line.Trim()` 等于 `RevertTag` 时 kind = `RevertPhase`，等于 `CorrectVoteTag` 时 kind = `CorrectVote`，这一行丢掉；同一条回复里出现多个标记，以最后一个为准。其余行用 `\n` 拼回，再 `Trim()`。没有标记就原样返回 `(reply, null)`，连空白也不改。

4. `src/LocalDm.Core/Hosting/HostNarration.cs` → `SystemPromptTemplate`：在第 9 条（`9. 被问到你是什么模型时…`）那一行后面追加 Parameters 里的第 10 条原文，缩进和前面几条一致。别的字不改。

5. `src/LocalDm.App/ViewModels/SessionViewModel.cs`：
   - 新增字段 `private bool _correctingVote;`。
   - `IsVoteVisible` 改成 `IsPlaying && IsCurrentPhaseVote && (!HasVoteSummary || _correctingVote)`。
   - 新增方法：
     ```csharp
     /// <summary>改票（ADR 0010）：重新打开投票录入，预填当前生效的票数。</summary>
     public void BeginVoteCorrection()
     {
         if (_projection?.VoteSummary is not { Counts.Count: > 0 } vs) return;
         _correctingVote = true;
         VoteInput = string.Join(", ", vs.Counts.Select(c => $"{c.Option}={c.Count}"));
         OnPropertyChanged(nameof(IsVoteVisible));
         OnPropertyChanged(nameof(CanSubmitVote));
     }

     /// <summary>这一轮是改票：提交后不再请 DM 重新组织投票。</summary>
     public bool LastVoteWasCorrection { get; private set; }

     public Task RevertPhaseAsync() => ExecuteAsync(GameCommandType.RevertPhase);

     /// <summary>上一环节的标题；第一个环节或没装载时为 null。</summary>
     public string? PreviousPhaseTitle
     ```
     `PreviousPhaseTitle` 的写法：`_package` 为 null 或 `_projection` 为 null 就返回 null；`var i = _package.Phases.ToList().FindIndex(p => p.PhaseId == _projection.CurrentPhase.PhaseId);`，下标 ≤ 0 返回 null，否则返回 `_package.Phases[i - 1].Title`。
   - `SubmitVoteAsync`（第 546–555 行）：调用 `ExecuteAsync` 之前写 `LastVoteWasCorrection = _correctingVote; var before = Revision;`；调用之后**只在 `Revision != before`（本次真的提交成功）时**才 `_correctingVote = false; OnPropertyChanged(nameof(IsVoteVisible));`。被拒绝时 Revision 不变：保留 `VoteInput` 和改票状态，让操作者改了再交。不要用 `_projection?.VoteSummary != null` 判断（拒绝路径保留旧投影，它恒为真）。
   - `ExecuteAsync` 的 `case DispatchOutcome.Committed` 分支里、`RefreshView` 之前加：`if (type != GameCommandType.SubmitVoteSummary) { _correctingVote = false; }`——推进、退回等任何别的状态变化都结束改票，免得改票框带进下一个投票环节。
   - `Attach` 里加 `_correctingVote = false;`，换局或重开时清掉。
   - `CommandStatus`（第 529–543 行）加 `GameCommandType.RevertPhase => $"已退回「{phase}」。",`。

6. `src/LocalDm.App/ViewModels/HostViewModel.cs`：
   - `NarrateAfterCommandAsync`（约第 971 行起）：`var intent = type switch` 之前加
     `if (type == GameCommandType.SubmitVoteSummary && _session.LastVoteWasCorrection) { return; }`。
     switch 里显式加 `GameCommandType.RevertPhase => HostNarrationIntent.AnnouncePhase,`。
   - `RunTurnAsync`（约第 1251 行起）：在 `if (reply is null) { … return; }` 之后、`var message = AppendHost(reply);` 之前插入：
     ```csharp
     var (shown, correction) = CorrectionProposal.Extract(reply);
     reply = string.IsNullOrWhiteSpace(shown) ? "好的，请在电脑上确认。" : shown;
     OfferCorrection(correction);
     ```
     后面的显示、记忆（`_conversation.AppendAssistant(reply, …)`）和朗读（`SpeakApprovedAsync(reply, …)`）都用这个去掉标记后的 `reply`。这是唯一一处拿到模型回复的地方，开场、环节播报、玩家提问、平板发言都经过它，所以不会有绕过的路径。
   - 新增成员：
     ```csharp
     private CorrectionKind? _pendingCorrection;
     public bool HasPendingCorrection => _pendingCorrection is not null;
     public string PendingCorrectionText => _pendingCorrection switch
     {
         CorrectionKind.RevertPhase => $"DM 提议：回到上一环节「{_session.PreviousPhaseTitle}」。",
         CorrectionKind.CorrectVote => "DM 提议：重新录入这一轮票数。",
         _ => string.Empty,
     };
     public RelayCommand ConfirmCorrectionCommand { get; }
     public RelayCommand DismissCorrectionCommand { get; }
     ```
     `OfferCorrection(CorrectionKind? kind)`：kind 为 null 就直接 return。判断能不能做：`RevertPhase` 要求 `_session.SessionStateText == "Playing"` 且 `_session.PreviousPhaseTitle is not null`；`CorrectVote` 要求 `_session.IsVotePhase && _session.HasVoteSummary && _session.SessionStateText == "Playing"`。能做就设 `_pendingCorrection = kind`，并记下 `_pendingRevision = _session.Revision;`（新字段 `private int _pendingRevision;`：提议绑定提出时的状态版本）；不能做就 `AppendLocalHint("DM 提议的改正现在不能做，请直接在左侧操作。")`。两种情况都要 `OnPropertyChanged(nameof(HasPendingCorrection))` 和 `OnPropertyChanged(nameof(PendingCorrectionText))`。
     构造函数里（第 151–190 行那一组 `new RelayCommand`）：
     - `ConfirmCorrectionCommand = new RelayCommand(ConfirmCorrectionAsync, () => HasPendingCorrection && !_session.Busy);`
     - `DismissCorrectionCommand = new RelayCommand(() => { SetPendingCorrection(null); return Task.CompletedTask; });`

     `ConfirmCorrectionAsync`：
     1. `if (_session.Busy || _pendingCorrection is null) return;`——回复和朗读期间 `_session.Busy` 为真（`RunTurnAsync` 里 `SetNarrationBusy(true)`），此时**不清提议**，按钮本来也是灰的；空闲后再点。
     2. `if (_session.Revision != _pendingRevision) { SetPendingCorrection(null); AppendLocalHint("DM 提议的改正现在不能做，请直接在左侧操作。"); return; }`——状态已经变了，旧提议作废。
     3. 取出 kind，`SetPendingCorrection(null)`；`RevertPhase` → `await _session.RevertPhaseAsync();`；`CorrectVote` → `_session.BeginVoteCorrection();`。
     `SetPendingCorrection` 是私有小方法：赋值、发出上面两个通知、`ConfirmCorrectionCommand.RaiseCanExecuteChanged()`。
   - `OnSessionPropertyChanged`（约第 1703 行）末尾加：
     ```csharp
     if (e.PropertyName == nameof(SessionViewModel.Busy)) { ConfirmCorrectionCommand.RaiseCanExecuteChanged(); }
     if (e.PropertyName == nameof(SessionViewModel.Revision) && _pendingCorrection is not null && _session.Revision != _pendingRevision)
     {
         SetPendingCorrection(null); // 手动推进、改票等让状态变了：旧提议不能再作用到新环节
     }
     ```
   - `AnnounceLoadedSession` 和 `RestartPackageAsync` 的开头各加 `SetPendingCorrection(null);`：换局、重开都清掉旧提议。

7. `src/LocalDm.App/Views/HostView.xaml`：底边输入区（第 369 行 `Border Grid.Row="3"` 里的 `Grid`，第 373–377 行有 3 个 `RowDefinition`）加一行给确认条：
   - 在第 374 行前面再加一个 `<RowDefinition Height="Auto" />`，共 4 行。
   - 原有子元素行号各加 1：第 379 行快捷按钮 `StackPanel Grid.Row="0"` → `"1"`；第 389 行 `Border Grid.Row="1" Style="{StaticResource ComposerFrame}"` → `"2"`；第 477 行 `Grid Grid.Row="2" Margin="2,9,2,0"` → `"3"`。
   - 确认条放在第 379 行的 `StackPanel` 前面，`Grid.Row="0"`，`Margin="0,0,0,10"`：
   - 外层 `Border`：`Visibility="{Binding HasPendingCorrection, Converter={StaticResource BoolToVisibility}}"`，样式照第 50 行附近「本局已暂停」那条提示的 `Border` 写（同样的背景、边框、圆角、内边距）。
   - 里面一个 `Grid`，三列 `*`/`Auto`/`Auto`：
     - 第一列是 `TextBlock Text="{Binding PendingCorrectionText}"`，`TextWrapping="Wrap"`。
     - 第二列是 `Button x:Name="ConfirmCorrectionButton" Content="确认" Command="{Binding ConfirmCorrectionCommand}" Style="{StaticResource SealButton}" Padding="12,6" Margin="8,0,0,0"`。
     - 第三列是 `Button Content="不用" Command="{Binding DismissCorrectionCommand}" Style="{StaticResource QuietButton}" Padding="12,6" Margin="6,0,0,0"`。

8. 测试：
   - `tests/LocalDm.Tests/PhaseFlowTests.cs` 末尾加 5 个 `[Fact]`，都用现有的 `Started()` / `AdvancedTo(n)` / `RevealPhase()` / `SyntheticPackage.Command(...)` / `Votes(...)`：
     a. `Revert_moves_back_one_phase_and_keeps_released_materials`：`AdvancedTo(3)`（在 `phase-discuss`），Apply `RevertPhase` → `CurrentPhaseId == "phase-search"`，Revision 加 1，`ReleasedMaterialIds` 和退回前完全相同。
     b. `Revert_on_first_phase_is_rejected`：`Started()` 后 Apply `RevertPhase` → 抛 `DomainRuleException`，`Code == PHASE_CONDITION_UNMET`。
     c. `Revert_after_reveal_is_rejected`：`RevealPhase()` 后 Apply `RevertPhase` → 同上。
     d. `Revert_while_paused_is_rejected`：`AdvancedTo(2)` 后先 Apply `Pause`，再 Apply `RevertPhase` → 同上。
     e. `Resubmitting_votes_in_the_same_phase_replaces_that_round`：`AdvancedTo(4)`（在 `phase-vote`，此时还没提交），先提交 `Votes(("A", 3), ("B", 2))`，再提交 `Votes(("A", 2), ("B", 3))` → `VoteHistory.Count == 1`，其中那一条和 `VoteSummary` 都是 A=2、B=3。
   - 新文件 `tests/LocalDm.Tests/CorrectionProposalTests.cs`（namespace `LocalDm.Tests`），4 个 `[Fact]`：
     a. `"好的，刚才推进早了。\n〔提议：回到上一环节〕"` → Text 为 `"好的，刚才推进早了。"`，Kind 为 `RevertPhase`。
     b. `"票数我记下了。\r\n  〔提议：重新录入票数〕  "` → Kind 为 `CorrectVote`，Text 为 `"票数我记下了。"`。
     c. `"要不要〔提议：回到上一环节〕呢"`（标记在句子中间）→ Kind 为 null，Text 原样。
     d. `"普通回答"` → `("普通回答", null)`。
   - `tests/LocalDm.Tests/DmConversationTests.cs`：
     - `ScriptedGateway` 加 `public string? NextReply { get; set; }`。`ReplyFor`（第 987 行）改成：不是整理请求、且 `NextReply` 不为 null 时，返回它并把它置回 null；否则保持原样。
     - 新增 `[Fact] Model_proposed_revert_waits_for_confirmation_then_moves_back`：
       1. `NewHostAsync(gateway)`，`await host.LoadDemoAsync()`。
       2. `await host.Session.ExecuteAsync(GameCommandType.Start)`，再 `await host.Session.ExecuteAsync(GameCommandType.Advance)`。
       3. `await WaitUntilAsync(() => !host.Session.Busy)`，记下 `var first = host.Session.Package!.Phases[0].PhaseId;`。
       4. `gateway.NextReply = "好的，刚才推进早了。\n" + CorrectionProposal.RevertTag;`，然后 `await SendAsync(host, "刚才点错了，推进早了");`。
       5. 断言：最后一条 `Author == ChatAuthor.Host` 的消息 `Text` 不含 `"〔"`；`host.HasPendingCorrection` 为 true；`host.Session.Session!.CurrentPhaseId` 还不等于 `first`（确认前没动）。
       6. `await WaitUntilAsync(() => !host.Session.Busy);`（回复结束前确认按钮是灰的），再 `host.ConfirmCorrectionCommand.Execute(null);`（`RelayCommand.Execute` 是 async void），再 `await WaitUntilAsync(() => host.Session.Session!.CurrentPhaseId == first);`。
       7. 断言：`host.Session.Session!.CurrentPhaseId == first`；`host.HasPendingCorrection` 为 false。
     - 新增 `[Fact] Pending_correction_waits_while_busy_and_expires_when_state_changes`：
       1. 同上第 1–5 步，得到一个待确认的退回提议。
       2. `host.Session.SetNarrationBusy(true);` → 断言 `host.ConfirmCorrectionCommand.CanExecute(null)` 为 false、`host.HasPendingCorrection` 仍为 true；再 `host.Session.SetNarrationBusy(false);` → `CanExecute` 为 true。
       3. `await host.Session.ExecuteAsync(GameCommandType.Advance); await WaitUntilAsync(() => !host.Session.Busy);` → 断言 `host.HasPendingCorrection` 为 false（状态变了，旧提议作废）。

9. 文档：
   - 新文件 `docs/decisions/0010-model-proposed-corrections.md`，格式照 `docs/decisions/0009-tablet-handbook-tab.md`（标题、日期 2026-10-23、背景 / 决定 / 否掉的方案 / 后果），一页以内，内容：
     - 背景：投票提交后录错了改不了；推进点错了退不回去。用户要求不写死按钮，靠模型理解「点错了」。
     - 决定：
       - 规则核心加 `RevertPhase`，限制同 Goal 第 1 条。
       - 同一轮重新提交票数替换原记录。
       - 模型只能在回复末行写提议标记，软件把它变成电脑上的确认条，点确认才执行。
       - 系统提示词追加第 10 条。
     - 否掉的方案：左栏常驻「退回」「改票」按钮（用户不要写死）；模型直接执行改正（违反宪章原则 III「模型不得写游戏状态」）。
     - 后果：
       - 模型没听懂时，玩家要再说一次。
       - 退回不收回已发的材料。
       - 揭秘以后不能退回。
   - `.specify/memory/constitution.md`：把末尾 `**Version**: 9.0.0 | **Ratified**: 2026-10-19 | **Last Amended**: 2026-10-23` 这一行改成 `**Version**: 10.0.0 | …`（后两项不变）。在这一行前面、9.0.0 修订段之后，加一段（前后各空一行）：
     `本次 10.0.0 修订原则 III：新增「退回上一环节」命令，并允许投票环节内重新提交票数以改正。条件：模型只能在回复里提出改正提议，提议在电脑上显示为确认条，主持人点确认才执行；退回只在游戏中、非第一个环节、真相未揭秘时可用，已发放材料不收回。受影响原则：III。理由与否掉的方案见 docs/decisions/0010-model-proposed-corrections.md。重新验证要求：规则核心测试覆盖退回的允许与拒绝路径、改票替换本轮记录；模型提议到确认的主持台流程测试；实机让模型听一次「点错了」并确认。`

## Check
`pwsh -NoProfile -Command 'dotnet build LocalDm.slnx -nologo -v q -warnaserror; if ($LASTEXITCODE) { exit 1 }; dotnet test tests/LocalDm.Tests -nologo -v q; if ($LASTEXITCODE) { exit 1 }; dotnet test tests/LocalDm.UiTests -nologo -v q; exit $LASTEXITCODE'`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- Reuse existing functions; don't copy existing logic.
- No rm, no git push, no git checkout . / restore . / reset --hard.
- 不读取、不打印 `stories/`、`.appdata/`、`.local/` 下的任何文件。
- 不删除、不跳过任何已有测试。
- 系统提示词前 9 条一个字都不改；不为任何一本剧本写专属规则。
- 不在左栏加常驻的「退回」或「改票」按钮。
- 如果 `LocalDm.UiTests` 报 `address already in use` 端口 5188 被占用，不要改代码；在回复里写明是端口被占用。
