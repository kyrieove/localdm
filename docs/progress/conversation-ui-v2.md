# 对话式界面重塑：实施进度

日期 2026-10-20。目标是把「本局主持」桌面端按 `docs/preview/conversation-ui-20261020/` 的第二版原型
重塑为真实 WPF 对话界面，并把流程改成剧本数据驱动、确认改为「点击即确认」。

本文件是本任务的进度锚点：换会话先读这里。

## 基线（本轮实测，2026-10-20）

| 项目 | 命令 | 结果 |
| --- | --- | --- |
| .NET SDK | `dotnet --version` | 10.0.402 |
| 构建（改前） | `dotnet build LocalDm.slnx` | 成功，0 警告 0 错误 |
| 测试（改前） | `dotnet test LocalDm.slnx` | 154 通过 / 0 失败 / 0 跳过 |
| git | `git status --short` | 33 个已修改文件 + 30 个未跟踪文件，全部保留 |

未提交的既有改动（Edge-TTS、HostActivity、TruthDocument、MonospaceGrid、审查文档、
HTML 原型等）一个字都没回滚。本轮只动与本任务相关的文件。

## 已确认的问题（读码核实）

- `src/LocalDm.Core/Import/RealScriptPackageBuilder.cs` 的 `BuildPhases` 固定返回七个阶段，
  标题、条件、允许动作全部硬编码，与剧本内容无关。
- `GameEngine` 的 `Start`/`Advance` 要求 `command.GroupReady`，`SubmitVoteSummary`/`Reveal`
  要求 `command.Confirmed`。只删勾选框会让命令持续被拒。
- `ScriptPhase` 只有 `Conditions`，没有「当前提示」和「下一步动作名」。
- `SessionViewModel` 的动作名是固定中文映射（「下一环节」「揭秘」）。
- `HostView.xaml` 固定 392 px 右栏，对话区被挤压。
- 界面文案泄漏内部细节：`revision`、「第 N 次提交」出现在公共界面。

## 已完成

### 1. 交付契约：点击即确认（Core）

- `GameCommand` 删掉 `GroupReady` 与 `Confirmed`，`PayloadFingerprint()` 同步缩成
  `类型|revision|材料|票数`。
- `GameEngine` 删掉两处人工确认检查，保留全部机器可验证条件：
  剧本包 Ready、阶段允许该动作、投票阶段需合法汇总、票数总数等于人数、
  材料属于本阶段、`RequiresAllMaterials` 时材料必须发完、revision 校验、包指纹绑定。
- `DomainErrorCode` 删掉 `GROUP_NOT_READY` / `CONFIRMATION_REQUIRED`。
- 存兼容：`OperationReceipt.PayloadFingerprint` 格式变化只影响「同一 operationId 重放」的比对；
  operationId 每次点击新生成，旧存档里的 receipt 不会被重新比对，进度与存档照常可读。

### 2. 流程由剧本包驱动（Core）

- 新增 `LocalDm.Core.Domain.ScriptFlow` / `ScriptFlowStep` / `ScriptStepKind`：
  环节数量、顺序、标题、提示、条件、材料、动作名、是否投票/揭秘，全部是声明数据，
  每个环节必须带 `SourceRef`。
- `RealScriptPackageBuilder.Build(..., ScriptFlow? flow = null)`：
  - 有声明 → 按声明逐环节生成。
  - 无声明 → 只生成一个「流程待确认」阶段（不含 Start），整包 `NeedsReview`，
    就绪报告里给出 `FLOW_NOT_DECLARED`。**不再编造默认七步。**
  - 声明里某环节缺 `SourceRef` → `FLOW_STEP_NEEDS_REVIEW`，整包不能开局。
  - 环节动作按 `ScriptStepKind` 推导（Search 才有发牌、Vote 才有提交票数），不按中文标题猜。
  - 动作名：`AdvanceLabel` 显式优先，否则「进入<下一环节标题>」；最后一环不给 Advance。
  - 公开段落：优先声明里写死的段 ID，其次按来源分组名（`PublicGroups`）解析，都没有就留空。
- `tools/LocalDm.Probe` 新增 `--flow <json>`；示例见 `docs/prepare/script-flow.example.json`。
- 协议层：`PublicPhaseView` 增 `Hint`/`AdvanceLabel`/`RevealLabel`/`RequiresAllMaterials`/`Index`/`Count`，
  `PublicSessionProjection` 增 `PhaseTrack`。界面只读投影即可渲染进度与动作。
- 删掉 `ReadinessProjection.ToPublicSession`：它是无调用者的重复投影，且跳过了可见性过滤。

### 3. 界面（LocalDm.App）

- `MainWindow`：278 px 浅色左轨（剧目 → 进度轨道 → 当前提示 → 可用动作 → 声音/设置/连接），
  其余全部给主区；可用动作固定在下方，窗口变矮只让进度列表滚动。
- `HostView`：一条连续对话（玩家消息、主持回复、阶段事件同一列表），底部固定输入区；
  「进行中/失败」行排在对话末尾，只在真忙或出错时出现，没有常驻「就绪」块；
  公开材料与已解锁真相合到宽幅主区（`ShowMaterials`）。
- 逐条重播：每条主持回复旁「重播」只用它已保存的文字，走 `HostDirector.ReplayItemAsync`，
  不请求模型、不提交状态。
- 停止：忙碌时输入区右侧出现方形停止键，取消模型请求与播音，晚到结果丢弃。
- 自动朗读：`ToolSettings.AutoRead`（默认开，旧设置文件缺项按开处理），关掉只显示文字。
- 设置页：音色/试听/自动朗读/声音输出/可用性/模型/读取列表/高级连接；
  **caller key 输入与保存控件已移除**（网关鉴权与 `SettingsStore.LoadCallerKey` 仍在）；
  MinerU 路径收进「高级：剧本准备」折叠区。
- 剧本库：演示剧本入口回到库页；上一局未走完的入口保留。
- 去掉公共界面里的 `revision` / 「第 N 次提交」。

## 返修（2026-10-20 独立验收后）

独立验收给出 7 项必修 + 2 处需求缺口。逐条修复如下，每条都补了正式回归测试。

| 发现 | 修法 | 回归测试 |
| --- | --- | --- |
| P1 改流程会覆盖旧冻结包，旧存档仍接受新流程 | 新增 `ScriptPackage.ContentHash`（来源 + 玩家数 + schema + 规范化阶段），`PackageId` 由它生成；不同流程得到不同包标识与文件名，旧文件保留。旧存档绑定原包，拿到新包时 `PACKAGE_MISMATCH` 并给出「流程被改写过」的说明。存档还在、包不在时，剧本库显式提示不能恢复（新增 `ISessionStore.ListSessionsAsync`）。 | `Changing_the_flow_changes_the_package_identity`、`Old_session_cannot_be_carried_into_a_rewritten_flow` |
| P2 错误材料/段落引用被静默删除，包仍可开局 | 未知材料编号、段落 ID、来源分组一律变成阻塞问题并点名具体 ID（`UNKNOWN_MATERIAL_REFERENCE:<环节>:<ID>` 等），整包 `NeedsReview`。 | `Unknown_material_reference_blocks_the_package_instead_of_being_dropped`、`Unknown_public_segment_and_group_references_block_the_package` |
| P2 重复环节 ID 生成 Ready 包，推进会原地打转 | `ScriptFlow.ValidationIssues` 校验空/重复 ID 与「要求材料却没有材料」；准备器把这些并进就绪报告。 | `Duplicate_step_ids_are_rejected_before_the_package_can_be_played`、`Empty_step_id_is_rejected`、`Search_step_that_requires_materials_must_declare_some` |
| P2 第二轮投票沿用第一轮票数 | 汇总绑定环节：`Session.VoteSummaryPhaseId` + `VoteHistory`；推进与揭秘都按环节校验。存档列 `vote_summary` 换成按环节的记录列表，旧裸数组格式仍可读。 | `Second_vote_round_needs_its_own_summary`、`Vote_history_survives_a_save_and_reload`、`Vote_history_round_trips_through_the_database`、`Legacy_single_summary_still_loads_after_the_format_change` |
| P2 重播期间仍可推进，下一段播报被丢弃 | 重播开始/结束/异常都占住会话播报闸；播报因忙碌被跳过时写进对话，不再静默。 | `Replay_holds_the_session_busy_gate_so_the_stage_cannot_move` |
| P2 重播失败仍显示「已重播」 | `HostDirector.ReplayItemAsync` 失败返回 `Item = null`；界面按「拿到 Item 且状态 Idle」才算成功，失败显示「重播失败：<原因>」并保留文字。 | `Failed_replay_reports_failure_and_keeps_the_text` |
| P2 揭秘自动切页后停止入口不可见 | 停止按钮移到左侧轨道（所有页面都在），生成/播报期间可见可点；输入区那个保留。 | `Stop_stays_reachable_after_reveal_switches_pages`（真实窗口 + 真实控件） |
| 需求缺口：开场按钮按固定环节序号 | 流程声明新增 `offersOpeningSpeech`，经投影到 `PublicPhaseView.CanOpenSession`，界面不再看 `Index == 1`。 | `Opening_speech_action_comes_from_the_declaration_not_the_phase_number`、`Opening_speech_button_follows_the_flow_declaration` |
| 需求缺口：`HostNarration` 不用 `Hint` | 流程提示（本来就原样展示在公共侧栏）作为公开依据进入两条提示词构造路径。 | `Phase_hint_reaches_the_narration_context` |

另外把 `scripts/verify-live-window.ps1` 收紧了两处：找不到按钮/输入框直接中止（不再 false 继续跑），
截图改用 `PrintWindow`（不依赖 z 序），因为屏幕拷贝曾经在抢前台失败时拍到过桌面上另一个程序。

### 返修后的实测

- `dotnet build LocalDm.slnx`：0 警告 0 错误。
- `dotnet test LocalDm.slnx`：**188 通过 / 0 失败 / 0 跳过**（新增 18 项）。
- 复跑独立验收自带的两个反例项目：
  - `.appdata/diagnostics/review-v2-app`（原样运行）：
    `HELD_REPLAY session.Busy=True; CanAdvance=False` /
    `DURING_REPLAY rev=1; advanced=False` /
    `FAILED_REPLAY speechState=Failed; note=重播失败：…; activityError=True`。
  - `.appdata/diagnostics/review-v2-core` 原样运行会在第一条就停住（那个错引用的包已不能开局，
    这正是修复）；可读版副本在 `review-v2-core-fixed`，输出：
    `MISSING readiness=NeedsReview issues=UNKNOWN_MATERIAL_REFERENCE:…,UNKNOWN_SEGMENT_REFERENCE:… canStart=False` /
    `IDENTITY sameId=False sameContentHash=False bindings=PACKAGE_MISMATCH` /
    `DUPLICATE readiness=NeedsReview issues=FLOW_DUPLICATE_STEP_ID:same canStart=False` /
    `VOTE advancedPastSecondVoteWithoutSubmission=False rejection=PHASE_CONDITION_UNMET`。
- 复跑独立验收自带的隔离真实窗口脚本 `.appdata/diagnostics/run-v2-live-review.ps1`：
  全流程通过，独立数据库断言 `state=3 / d-revealp / revision=9 / 材料 2 份 / 事件=回执=9`，
  `vote_summary` 已按新格式落盘（`{"Records":[{"PhaseId":"d-votep",…}]}`）。

## 验证（本轮实测）

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 构建 | `dotnet build LocalDm.slnx` | 0 警告 0 错误 |
| 全部测试 | `dotnet test LocalDm.slnx` | 188 通过 / 0 失败 / 0 跳过（LocalDm.Tests 172 + UiTests 16，含返修后新增 18 项） |
| 真实窗口（UI Automation） | `pwsh -File scripts/verify-live-window.ps1` | 见下 |
| 截图（真实窗口，100%） | `artifacts/ui/desktop-100-*.png` | 8 张 |
| 截图（真实窗口，125% 副屏） | `artifacts/ui/desktop-125-*.png` | 1 张 |
| 截图（真实 WPF 渲染，含 150% DIP 空间） | `artifacts/ui/hostdesk-*.png` | 10 张 |

真实窗口那条脚本做的事：启动本机 exe（数据根指向临时目录，不碰真实存档）→
UI Automation 依次点「体验演示 → 开始这一局 → 进入搜证 → 发放材料 线索包一 →
发放材料 线索包二 → 偏好设置 → 回到本局 → 进入讨论 → 进入投票 → 录入 A=3, B=2 →
确认提交汇总 → 进入真相 → 公开真相」，每一步截图。
过程中确认：发完两份材料后「发放材料」消失、「进入讨论」可用；提交票数后进入真相；
「公开真相」点一下就到揭密态并自动切到宽幅复盘页。

## 未执行 / 未验证

- **真实语音可听性**：本轮只确认了音色被正确枚举（真实窗口里显示「云希 · 微软神经男声」）
  与语音调用链路（单元测试用假 TTS）。`待真实人耳确认`。
- 真实剧本（`stories/`）仍未成为 Ready 包：本机 MinerU 缺解析引擎，且流程声明需要准备者写。
- 150% 缩放没有改系统 DPI（那是系统设置）：用同一套界面的 DIP 空间模拟，见
  `hostdesk-scale-150.png`。
- 打包版 / 便携版 / 安装器未重跑。
