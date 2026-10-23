# playtest-probe — 探针新增 `--playtest`：软件里的 DM 主持，4 个 wb 模型扮演玩家，自动跑完一整局

## Goal
- 运行 `dotnet run --project tools/LocalDm.Probe -- --playtest --data <数据目录> --model gpt-6.1-sol`，会自动跑完一整局：
  - 读取 `<数据目录>/packages` 下最新的剧本包，并复制一份存档，原存档不动。
  - 软件里的 DM 用 `--model` 指定的模型，通过 `HostViewModel` 主持，走的是和软件完全相同的路径。
  - 每个角色由一个 CodeBuddy（`deepseek-v4.1-flash`）玩家扮演。
  - 探针自己扮演主持台操作员：发材料、录票数、推进、揭秘。
- 跑完后在 `<repo>/.local/playtest/<时间戳>/` 下留下三个文件：
  - `transcript.md`：完整对局记录。
  - `metrics.json`：统计数字。
  - `players/<roleId>.log`：每位玩家每次收到的提示和原始回复。
- 新增的纯函数（解析玩家回复、汇总票数）有单元测试。

## Risk
medium — 只新增探针工具和测试，不动软件本体。

## Parameters
- 命令行参数：
  - `--playtest`：在 `Program.cs` 里分派。
  - `--data`：默认 `%LOCALAPPDATA%\LocalDm`。
  - `--settings`：默认 `<data>/settings.json`。
  - `--model`：DM 模型，默认用设置里的值。
  - `--player-model`：默认 `deepseek-v4.1-flash`。
  - `--rounds`：每个环节最多几轮，默认 `2`。
  - `--max-steps`：操作员最多推进 / 揭秘几次，默认 `40`。
  - `--out`：默认 `<repo>/.local/playtest`。
- 玩家调用：
  - 进程：`codebuddy -p <提示> --model <player-model> --effort high -y --output-format stream-json`，stdin 关闭。
  - 工作目录：`<runDir>/players/<roleId>/`，这个空目录事先建好。
  - 回复：取 stdout 里最后一行 `{"type":"result"}` 的 `result` 字段。
  - 单次超时 180 秒；超时或失败记为 `{"to":"pass"}`，并计入 `metrics.playerErrors`。
- 玩家回复格式（提示里要求只输出一个 JSON）：`{"to":"dm"|"table"|"pass","text":"…","ready":true|false,"vote":"<roleId>"|null,"confused":true|false}`
  - `to=dm`：发给软件 DM。
  - `to=table`：只给其他玩家看。
  - `confused`：上一次 DM 的回答没帮到自己，或者答非所问。
- 发给 DM 的文字格式：`$"{roleId}：{text}"`，走 `host.InputText = …; await host.SendAsync();`。
- `metrics.json` 的字段：
  - `dmTurns`、`dmLatencyMs`（数组）、`dmLatencyP50`、`dmLatencyMax`
  - `tableTurns`、`passTurns`、`playerErrors`、`confusedCount`
  - `stuck`：数组，每次推进失败时的描述
  - `cardLeaks`：数组，复用现有的 `CardLeaks`
  - `phasesVisited`：数组，记环节标题
  - `revealed`：bool
  - `finalState`：字符串
  - `minutes`：耗时

## Files
may change: `tools/LocalDm.Probe/PlaytestProbe.cs`, `tools/LocalDm.Probe/Program.cs`, `tools/LocalDm.Probe/DmAcceptanceProbe.cs`, `tests/LocalDm.Tests/PlaytestProbeTests.cs`
read-only: `stories/`, `.local/`

## Steps (N = 6)
1. `tools/LocalDm.Probe/DmAcceptanceProbe.cs`：只改可见性，函数体一字不动。把下列方法的 `private static` 改成 `internal static`：
   - `NewHost`（第 1014 行）、`LatestPackage`（第 1075 行）、`LoadPackage`（第 1087 行）、`NewRunDirectory`（第 1095 行）、`FindRepoRoot`（第 1160 行）、`CardLeaks`（第 445 行）、`StepAsync`、`WaitIdleAsync`。
   - 另外两个用 `grep -n "static .* CopyDatabase\|static .* ArgumentValue"` 定位，同样改成 `internal static`。
2. `tools/LocalDm.Probe/Program.cs`：照现有 `--dm-acceptance` 分支的写法，加一个 `--playtest` 分支，调用 `PlaytestProbe.RunAsync(args)`。
3. 新建 `tools/LocalDm.Probe/PlaytestProbe.cs`，写成 `public static class PlaytestProbe`，内容如下。
   a. 两个纯函数，标成 `internal static`，供测试调用：
      - `PlayerMove ParseMove(string raw)`：
        - 先找第一个 `{` 到最后一个 `}` 之间的文本，用 `JsonDocument` 解析。这样能兼容 ```json 代码块。
        - 解析失败时，返回 `new PlayerMove("table", raw.Trim(), false, null, false)`；如果 raw 是空白，返回 `("pass", "", false, null, false)`。
        - `to` 只认 `dm`、`table`、`pass`，其他值一律当 `table`。
        - `PlayerMove` 写成 `internal sealed record PlayerMove(string To, string Text, bool Ready, string? Vote, bool Confused)`。
      - `string TallyVotes(IEnumerable<string?> votes, IReadOnlyList<string> roleIds)`：
        - 只统计在 `roleIds` 里的票。
        - 按 `roleIds` 的顺序输出 `"r1=2, r2=1"`，票数为 0 的角色省略。
        - 一票都没有时返回 `$"{roleIds[0]}=1"`，保证能录进去。
   b. `RunAsync(string[] args)`：
      - 先按 Parameters 读参数，用 `NewRunDirectory(out)` 新建运行目录，用 `CopyDatabase` 复制存档，再用 `NewHost(settingsPath, dataRoot, workDb, model)` 建主持台，然后 `await host.LoadPackageAsync(package)`。
      - 角色本：
        - 用 `var content = new PackageTabletContent(package, TabletAssetRoot.Resolve(packagePath, package.Tablet!))` 构建内容，每个角色的正文是 `RoleBookPlainText.Build(content.RoleBooks[roleId])`。
        - `package.Tablet` 为 null，或者任何一个角色本少于 200 字时，打印 `"! 角色本文字不足：<roleId> <字数>"`，返回 3，不开局。
      - 已发放材料的正文：取 `host.Session.Projection!.ReleasedMaterialIds` 对应 `Material.SourceSpanIds` 的 `package.Spans` 原文，用 `CardFrontTexts` 同样的过滤规则。
      - 开局：`await StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.Start, null, null));`。
   c. 主循环，`step` 从 0 开始，满足以下任一条件就退出：`step >= maxSteps`；`host.Session.SessionStateText` 是 `"Completed"`；推进失败（记入 `stuck`）。每次循环做下面几件事：
      - 先把当前环节标题加入 `phasesVisited`。
      - 进行最多 `rounds` 轮。每一轮按 `package.Roles` 的顺序，让每个玩家行动一次：
        - 组装提示：
          - 一段固定说明：你是剧本杀真人玩家，扮演 `<roleId>`；只能根据自己的角色本和公开信息行动；想问主持人就 `to=dm`，想和其他玩家讨论就 `to=table`；本环节该做的都做完了就设 `ready=true`；投票环节在 `vote` 里填一个角色编号；只输出一个 JSON。
          - 角色列表。
          - 当前环节标题，以及 `host.Session.Hint`。
          - 自己的角色本全文。
          - 已发放材料正文。
          - 公共记录的最后 60 行。
        - 调用玩家，用 `ParseMove` 解析回复，把提示和原始回复追加写入 `players/<roleId>.log`。
        - `to=dm`：用 `Stopwatch` 计时，发给 DM，然后 `await WaitIdleAsync(host)`。耗时写入 `dmLatencyMs`。取这次新增消息里第一条 `IsModelReply` 的文本作为回复。公共记录追加 `"{roleId}→DM：{text}"` 和 `"DM：{回复}"`。
        - `to=table`：公共记录追加 `"{roleId}（对大家）：{text}"`。
        - `to=pass`：计入 `passTurns`。
        - 每一步都写进 `transcript.md`，格式：`### <环节> · 第 r 轮 · <roleId>`，下面是内容。
        - 一轮结束后，如果四个玩家最近一次都是 `ready=true`，就跳出轮次循环。
      - 操作员动作：
        1. 依次发放 `host.Session.PendingMaterials`，写法同 `AdvanceOneStepAsync`。
        2. 如果 `host.Session.IsVotePhase && !host.Session.HasVoteSummary`：用 `TallyVotes(各玩家最近一次的 vote, roleIds)` 汇总票数，经 `SessionViewModel.TryParseVotes` 解析后提交 `SubmitVoteSummary`。
        3. 如果当前环节的 `AllowedActions` 包含 `PublicAction.Reveal`，并且 `host.Session.Projection!.RevealedTexts` 为空：执行 `Reveal`，设 `revealed=true`，让四个玩家各说一次揭秘后的感想（同一套流程，不计入轮数），然后 `step++` 并继续。
        4. 否则，`host.Session.CanAdvance` 为真就执行 `Advance`；为假则把 `$"{PhaseTitle}: {host.Session.Status}"` 加入 `stuck`，退出循环。
        5. 每次操作后 `step++`。
   d. 收尾：
      - `cardLeaks = CardLeaks(host.Messages, package)`，`finalState = SessionStateText`。
      - 写入 `metrics.json`，用 `JsonSerializer`，设置 `WriteIndented = true` 和 `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`。
      - 控制台打印运行目录和 metrics 摘要。
      - `finalState` 为 `Completed` 或 `revealed` 为真时返回 0，否则返回 1。
4. 每完成一位玩家的一次行动，就在控制台打印一行进度：`[<环节> r<轮> <roleId>] <to> <text 前 40 字>`。
5. 新建 `tests/LocalDm.Tests/PlaytestProbeTests.cs`（`namespace LocalDm.Tests`，`using LocalDm.Probe;`；如果 Probe 的命名空间不同，用 `grep -n namespace tools/LocalDm.Probe/DmAcceptanceProbe.cs` 查到后照用），写以下测试：
   - `ParseMove`：
     a. 标准 JSON → 字段全对。
     b. 用 ```json 代码块包着的 JSON → 能解析。
     c. 普通文字 `"我觉得是他"` → `To == "table"`，Text 原样。
     d. `to` 为 `"shout"` → `To == "table"`。
     e. 空串 → `To == "pass"`。
   - `TallyVotes`：
     a. `["r1","r2","r1",null,"x"]` 加上角色 `["r1","r2","r3"]` → `"r1=2, r2=1"`。
     b. 全是 null → `"r1=1"`。
   - `ParseMove`、`TallyVotes`、`PlayerMove` 一律写成 `public`（测试项目要访问，不改 csproj）。
6. 只运行 Check，不要真跑 `--playtest`（会调用模型）。

## Check
```
dotnet build LocalDm.slnx -nologo -v q -warnaserror && dotnet test tests/LocalDm.Tests -nologo -v q --filter-class "*PlaytestProbeTests"
```

## Don't
- No commits, no other files, no refactors beyond the steps.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- Reuse existing functions; don't copy existing logic.
- No rm, no git push, no git checkout . / restore . / reset --hard.
- 不真跑 `--playtest`、不调用 codebuddy / 模型；不读 `stories/`、`.local/`、`%LOCALAPPDATA%\LocalDm` 下的剧本包。
- 不为某一本剧本写专属规则或提示词。
