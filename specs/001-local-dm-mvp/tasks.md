# Tasks: 本局主持 Windows 单电脑 MVP

**Input**: `specs/001-local-dm-mvp/` 的 spec、plan、research、data-model 和 contracts。
**Prerequisites**: 设计文档已生成；本文件是实施清单，不授权在本轮启动实现。
**Tests**: spec 的 Required Verification 明确要求来源/隐私/状态/恢复/输入测试，因此列入对应故事。
**Organization**: 按用户故事分组，MVP 完整范围为 US1–US5。

## Format: `[ID] [P?] [Story] Description`

所有任务为未勾选清单。ID 连续；[P] 仅表示同一依赖检查点后的不同文件工作可并行。
故事阶段必须带 [USn]，Setup/Foundational/Polish 不带故事标记。
所有路径相对 D:/2/dev/dm，拟建路径不表示已存在。

## Path Conventions

`src/LocalDm.App` 负责 WPF，`src/LocalDm.Core` 负责领域，`src/LocalDm.Infrastructure` 负责外部工具和本地存储。
`tests/LocalDm.Tests` 与 `tests/LocalDm.UiTests` 为测试，`docs/evidence` 存脱敏结果。

## Phase 1: Setup (Shared Infrastructure)

**Goal**: 建立 Windows 构建与验证基础；本文件仅列任务，本轮不执行应用开发。

**Independent Test**: 实际 SDK、工程和依赖锁定后可构建空窗口，测试项目可发现测试。

- [ ] T001 核对 Windows 11 x64、.NET 10 SDK 与发布工具，准备缺少的开发依赖并记录实际版本到 docs/evidence/setup/environment.md。
- [ ] T002 建立 LocalDm.sln、global.json、Directory.Packages.props 及 src/LocalDm.App/LocalDm.App.csproj、src/LocalDm.Core/LocalDm.Core.csproj、src/LocalDm.Infrastructure/LocalDm.Infrastructure.csproj，锁定稳定 NuGet 版本并启用 packages.lock.json。
- [ ] T003 维护 .gitignore 并配置实际运行数据根目录，确保 stories/、凭据、数据库、构建及 artifacts/ 不进入提交。
- [ ] T004 [P] 创建 fixtures/synthetic/manifest.json、text-12pages.pdf、scan-3pages.pdf 与 images/ 的已知真值合成材料，答案只在测试私有 expected.json。
- [ ] T005 建立 tests/LocalDm.Tests/LocalDm.Tests.csproj 与 tests/LocalDm.UiTests/LocalDm.UiTests.csproj，核心用 xUnit、真实 WPF 使用 Windows UI Automation；不引入假通过的空测试。
- [ ] T006 [P] 在 docs/evidence/README.md 定义脱敏 scenario/commit/command/result/screenshot/limitations 证据格式，禁止真实秘密和 key。

**Checkpoint**: 上述结果有实际证据后才能进入依赖它的阶段；当前全部未执行。

## Phase 2: Foundational (Blocking Prerequisites)

**Goal**: 锁定共享数据、接口与隐私边界，完成后再实施故事。

**Independent Test**: 核心对象拒绝不合法输入，工具或外发边界未知时不能调用真实资料。

- [ ] T007 在 src/LocalDm.Core/Domain/SourceModels.cs 定义 SourceFile、SourceSpan、CanonicalSegment，落实「所有 ID 必须非空，SourceSpan 必须引用存在的文件与页块。」「每个输入文件的 hash 与页数必须记录，覆盖必须完整；关键字段必须具有非空 sourceSpanIds。」
- [ ] T008 在 src/LocalDm.Core/Domain/ToolSettings.cs 定义 loopback 配置、callerKeyRef 和 DataConsent，落实「未知外发边界视为禁止；没有数据授权不得发送真实文档或真实剧本文字。」
- [ ] T009 在 src/LocalDm.Core/Ports/ExternalPorts.cs 定义 IMagpieGateway、ITtsService、IDocumentTool、ISessionStore 的请求/结果及取消契约，对齐 contracts/integrations.md。
- [ ] T010 在 src/LocalDm.Core/Domain/PublicProjection.cs 定义不含 truth/privateStory 的公开 DTO，落实「公开投影不能包含秘密、未释放线索或未解锁真相。」
- [ ] T011 在 src/LocalDm.Infrastructure/Storage/SqliteSchema.sql 与 SessionStore.cs 建包、局、事件与唯一 receipt 表及单事务读写基础，落实「剧本 packageId、version、manifestHash 在一局内不可改变。」「revision 从 0 开始，每次提交恰加 1；expectedRevision 必须等于当前 revision。」「operationId 在同一 session 内唯一；相同 ID 和相同内容返回原结果，不同内容返回冲突。」
- [ ] T012 在 src/LocalDm.Core/Domain/HostModels.cs 定义 requestId/generationId/SpeechState，落实「每次请求有唯一 requestId；取消或被替代请求的输出不得显示或播音。」「同一时刻最多一段播音；重播不得生成事件、调用模型或重新提交操作。」
- [ ] T013 在 src/LocalDm.Core/Domain/ImportModels.cs 定义 ImportJob、ReadinessReport 和 PreparedEnvelope，落实「只有 Ready 可以开局；NeedsReview、Failed、Cancelled 不可以开局。」并将 Complete 提取与 Ready 业务校验分离。
- [ ] T014 在 src/LocalDm.App/App.xaml.cs 组合依赖，在 src/LocalDm.Infrastructure/Diagnostics/RedactedLog.cs 映射脱敏错误，不把原包或工具 stderr 直接绑定界面。

**Checkpoint**: 上述结果有实际证据后才能进入依赖它的阶段；当前全部未执行。

## Phase 3: User Story 1 - 文字模型公共口播 (Priority: P1)

**Goal**: 最先交付可演示的合成口播切片，不宣称完整剧本 MVP。

**Independent Test**: 实际窗口经本机 Magpie 获取合成公开文字并 TTS 播放；停止、取消、重播不会改状态。

- [ ] T015 [P] [US1] 在 tests/LocalDm.Tests/MagpieGatewayTests.cs 准备 loopback、未知 model、非空响应、取消与晚到结果用例，合成 HTTP 输入不含真实资料。
- [ ] T016 [P] [US1] 在 tests/LocalDm.Tests/SpeechLifecycleTests.cs 准备串行播放、取消队列、重播不生成状态事件的可观察结果用例。
- [ ] T017 [US1] 在 src/LocalDm.Infrastructure/Magpie/MagpieGateway.cs 实现 /v1/models 与非流式 /v1/chat/completions 最小契约；配置 caller key 仅来自本软件，不以 /health 404 判离线。
- [ ] T018 [US1] 在 src/LocalDm.Infrastructure/Storage/SettingsStore.cs 保存设置，caller key 用 Windows DPAPI，测试日志和普通 JSON 不出现明文 key。
- [ ] T019 [US1] 在 src/LocalDm.Infrastructure/Voice/TtsService.cs 枚举已安装中文声音并实现 Speak/Stop/Replay 单队列；无声音或设备失败保留文字和明确错误。
- [ ] T020 [US1] 在 src/LocalDm.Core/Hosting/HostDirector.cs 串联合成公开输入、模型文本和 TTS，requestId 失效后丢弃晚到结果。
- [ ] T021 [US1] 在 src/LocalDm.App/Views/SetupView.xaml 与 SetupViewModel.cs 实现真实连接诊断、model ID 列表、音色试听、外部工具状态与错误。
- [ ] T022 [US1] 在 src/LocalDm.App/Views/HostView.xaml 与 ViewModels/HostViewModel.cs 实现合成口播、发送、停止和重播；禁止绑定完整剧本私有段。
- [ ] T023 [US1] 在 src/LocalDm.App/Views/LibraryView.xaml 与 MainWindow.xaml 实现首页、空状态和设置/主持导航；默认不启动任何模型请求。
- [ ] T024 [US1] 在 docs/evidence/us1/desktop-speech.md 记录实际 Windows Magpie 合成请求、中文扬声器、取消/重播和 125%/150% 缩放截图，运行该故事测试并如实标未测项。

**Checkpoint**: 上述结果有实际证据后才能进入依赖它的阶段；当前全部未执行。

## Phase 4: User Story 2 - 完整资料与无剧透准备 (Priority: P1)

**Goal**: 既有工具提取完整资料，来源校验后才能注册冻结剧本包。

**Independent Test**: 超过 10 页 PDF、扫描 PDF、图片和完整预解析包通过；缺页/冲突/未续读/源失配被拦截。

- [ ] T025 [P] [US2] 在 tests/LocalDm.Tests/DocumentToolContractTests.cs 准备 MinerU JSON done/pending/parse_wait_timeout、parse/read 续读和 stdout/stderr 分流用例。
- [ ] T026 [P] [US2] 在 tests/LocalDm.Tests/ImportCoverageTests.cs 与 PublicReadinessTests.cs 准备全页/事实来源覆盖、仅 Markdown 拒绝、秘密不出报告及关键异常禁止开局用例。
- [ ] T027 [US2] 在 src/LocalDm.Infrastructure/Documents/MineruPrivacyPreflight.cs 核对版本严格等于 4.0.1、app-owned MINERU_HOME、规则/引擎/遥测状态；不复制用户全局规则/key，不明确本地边界则拒绝解析。
- [ ] T028 [US2] 在 src/LocalDm.App/Views/DocumentToolSetupView.xaml 仅经用户明确选择配置应用专属 profile 与服务，显示真实引擎/恢复选项，不修改全局服务或自动启用远程。
- [ ] T029 [US2] 在 src/LocalDm.Infrastructure/Documents/MineruRunner.cs 用 ProcessStartInfo.ArgumentList 异步消费 stdout/stderr；PDF 显式 --pages all，图片不带 --pages，不加 --remote/--force。
- [ ] T030 [US2] 在 src/LocalDm.Infrastructure/Documents/MineruContinuationReader.cs 按 parse 的 page_range/after 或 read 的 locator 续读至 next_request=null，保存 ranges 并检测重复循环；done+content对象非null+无error 才算提取完成。
- [ ] T031 [US2] 在 src/LocalDm.Infrastructure/Import/SourceIndex.cs 归一化文件 hash、真实页块与 locator；图片 page=1 且顺序由 manifest 指定，不猜剧情排序。
- [ ] T032 [US2] 在 src/LocalDm.Infrastructure/Import/PreparedPackageReader.cs 接受 formatVersion=1、Complete、源 hash 与页覆盖相符的应用包，拒绝 Markdown 伪装完整包或 MinerU 私有缓存。
- [ ] T033 [US2] 在 src/LocalDm.Infrastructure/Import/ImportPipeline.cs 处理取消、单页重试及应用包保存；在未获资料目的地授权前只允许合成输入或已验证本地处理。
- [ ] T034 [US2] 在 src/LocalDm.Core/Import/ScriptPackageBuilder.cs 与 ScriptValidator.cs 验证模型/工具边界输出、非空事实来源与阶段/材料引用；未知事实保留 NeedsReview，不生成默认凶手。
- [ ] T035 [US2] 在 src/LocalDm.Core/Import/ReadinessProjection.cs 以及 src/LocalDm.App/Views/ImportView.xaml、ReadinessView.xaml 实现脱敏准备过程和阻止开局；包完整校验后冻结注册。
- [ ] T036 [US2] 在 docs/evidence/us2/tool-parse.md 用合成超过10页 PDF、扫描 PDF、图片与失败输入实测所选 MinerU profile、全文续读、页块来源和隐私预检；服务/引擎不可用记为未通过。
- [ ] T037 [US2] 在 docs/evidence/us2/real-script-readiness.md 仅获准后验证固定真实本的页、角色、阶段、线索、真相来源与独立秘密校验；缺授权或校验者时保持阻塞，不能标 Ready。

**Checkpoint**: 上述结果有实际证据后才能进入依赖它的阶段；当前全部未执行。

## Phase 5: User Story 3 - 确定性完整局与恢复 (Priority: P1)

**Goal**: 按原本主持全流程，重复点击和强退均不破坏进度。

**Independent Test**: 用固定合成包独立走完完整局，检查未确认、过期、重复、投票、揭秘和事务故障。

- [ ] T038 [P] [US3] 在 tests/LocalDm.Tests/PhaseFlowTests.cs 与 RevealLockTests.cs 准备未确认/原本条件/提前揭秘/私密投影用例。
- [ ] T039 [P] [US3] 在 tests/LocalDm.Tests/IdempotencyTests.cs 与 RecoveryTests.cs 准备同 ID 十次重试、同 ID 不同 payload、过期 revision 及事务前后强退恢复用例。
- [ ] T040 [US3] 在 src/LocalDm.Core/Domain/GameModels.cs 定义 Session、Command、Event 与票数类型，落实「阶段推进必须满足原本条件及 groupReady=true；揭密和票数提交必须 confirmed=true。」「票数必须为非负整数，汇总总数符合当前剧本的参与者与投票规则。」
- [ ] T041 [US3] 在 src/LocalDm.Core/Game/GameEngine.cs 实现 contracts/game-commands.md 的 Start/Advance/ReleaseMaterials/Pause/Resume/Reveal/Complete；不从模型文本直接执行。
- [ ] T042 [US3] 在 src/LocalDm.Core/Game/CommandDispatcher.cs 与 src/LocalDm.Infrastructure/Storage/SessionStore.cs 事务内校验 revision、去重 receipt、插入 event 和更新投影；事务失败三者均不改变。
- [ ] T043 [US3] 在 src/LocalDm.Core/Game/PublicSessionProjector.cs 只投影允许公开的阶段、材料编号及揭秘内容，不把 truth 对象交给 HostView。
- [ ] T044 [US3] 在 src/LocalDm.App/Views/AdvanceConfirmView.xaml 与 AdvanceConfirmViewModel.cs 展示无剧透条件和大家准备勾选，取消对话框不提交。
- [ ] T045 [US3] 在 src/LocalDm.App/Views/VoteSummaryView.xaml 与 VoteSummaryViewModel.cs 实现纸笔汇总、票数校验与二次确认；开始前明确接受该方式且原本允许。
- [ ] T046 [US3] 在 src/LocalDm.Core/Hosting/PublicContextBuilder.cs 与 PublicReplyPolicy.cs 仅使用当前公开原文及批准过渡句；不确定或无有效来源返回安全说明，不靠 LLM 自审放行。
  - 2026-10-20 更正：`PublicReplyPolicy.cs` 从未实现。实际做法是秘密隔离放在拼提示词处（`HostNarration` 只取 Public 段与已解锁真相），输出侧按产品决定不审查模型输出。上下文构造部分由 `PublicContextBuilder` 覆盖。
- [ ] T047 [US3] 在 src/LocalDm.App/ViewModels/HostViewModel.cs 接入真实 session、固定口播、下一环节和材料包提示，只有显式有效操作触发口播。
- [ ] T048 [US3] 在 src/LocalDm.Infrastructure/Storage/SessionRecovery.cs 与 src/LocalDm.App/Views/LibraryView.xaml 实现包指纹检查及继续一局；恢复后 SpeechState=Idle，不补做未确认操作。
- [ ] T049 [US3] 在 docs/evidence/us3/flow-recovery.md 记录固定包完整局、MinerU 不运行时开局、重复10次操作、两个事务故障点及提前揭秘拒绝，运行核心安全/状态用例。

**Checkpoint**: 上述结果有实际证据后才能进入依赖它的阶段；当前全部未执行。

## Phase 6: User Story 4 - OpenLess 中文输入 (Priority: P2)

**Goal**: 利用外部语音转写，应用只接受文字并安全分流。

**Independent Test**: 目标 WPF 输入框实测 OpenLess/粘贴/IME，20次发送取消重播不丢字、不重复或越过确认。

- [ ] T050 [P] [US4] 在 tests/LocalDm.Tests/TextSubmissionTests.cs 准备 IME 候选 Enter、Shift+Enter、发送一次、状态意图只提案的用例。
- [ ] T051 [US4] 在 src/LocalDm.App/Input/HostInputController.cs 保留 WPF 组合输入语义，实现回车/按钮发送和 Shift+Enter 换行；不使用输入停顿计时器或模拟 OpenLess 快捷键。
- [ ] T052 [US4] 在 src/LocalDm.Core/Hosting/HostIntentRouter.cs 将文本分为普通公共请求和 ProposeCommand，后者只能产生待确认提案，不调用 CommandDispatcher。
- [ ] T053 [US4] 在 src/LocalDm.App/Views/HostView.xaml 与 ViewModels/HostViewModel.cs 增加 OpenLess 焦点提示、手动输入/粘贴 fallback、提案到确认的明确动作，不请求麦克风权限。
- [ ] T054 [P] [US4] 在 tests/LocalDm.Tests/HostUncertaintyTests.cs 验证原文不明、索取秘密、注入指令及过期请求只能安全回应或丢弃，不进入 TTS/状态提交。
- [ ] T055 [US4] 在 docs/evidence/us4/openless-input.md 记录实际 OpenLess 长句/标点/角色名、IME 与焦点测试，以及20次发送/取消/重播结果；进程存在和网页输入不能替代打包窗口验证。

**Checkpoint**: 上述结果有实际证据后才能进入依赖它的阶段；当前全部未执行。

## Phase 7: User Story 5 - 成型软件与真人一局 (Priority: P2)

**Goal**: 安装和便携发布物在无开发 SDK 环境使用，并完成真实整局。

**Independent Test**: 两种发布物实际启动，数据保留明确，目标设备与真实4–6人局通过。

- [ ] T056 [US5] 在 tests/LocalDm.UiTests/ReleaseSmokeTests.cs 建 Windows UI Automation 场景，覆盖首次设置、公共窗口、恢复、缩放及输入入口，不模拟已完成真人局。
- [ ] T057 [US5] 在 src/LocalDm.App/Properties/PublishProfiles/WinX64.pubxml 配置自包含发布，验证 System.Speech、SQLite 原生依赖及资源可加载，不假称系统声音随包附送。
- [ ] T058 [US5] 在 packaging/windows/LocalDm.iss 编写 per-user 安装器、快捷方式与明确数据保留策略，不静默删除私人资料；锁定实际构建工具版本。
- [ ] T059 [US5] 在 packaging/windows/Publish.ps1 将同一发布目录生成 LocalDm-Setup-x64.exe 与 LocalDm-win-x64.zip，排除 stories、真实剧本、key、日志和运行数据库。
- [ ] T060 [US5] 在 src/LocalDm.App/Views/SetupView.xaml 补外部 Magpie/OpenLess/中文声音诊断及无开发环境说明；MinerU 仅准备阶段检查，不作为已准备一局的运行前置。
- [ ] T061 [US5] 在 docs/evidence/us5/install-portable.md 记录实际安装/便携启动、无开发 SDK、升级数据保留与卸载选择；签名或系统信任问题如实报告，不要求绕过安全警告。
- [ ] T062 [US5] 在 docs/evidence/us5/real-tabletop.md 获准后以真实固定本和4–6名真人从准备玩到复盘，记录无提前读真相的操作流程；无真人时必须标未执行。
- [ ] T063 [US5] 在 docs/evidence/us5/fault-matrix.md 实测模型断线、TTS 故障、OpenLess 退出、强退与重启；资料和进度保留，恢复不自动播音。

**Checkpoint**: 上述结果有实际证据后才能进入依赖它的阶段；当前全部未执行。

## Phase 8: Polish & Cross-Cutting Concerns

**Goal**: 收敛可交付证据与文档，不扩大 MVP。

**Independent Test**: 全部成功标准有实际证据或明确阻塞，无未解释的跳过。

- [ ] T064 [P] 更新 docs/user-guide.md 的私人材料、纸笔汇总、OpenLess/模型/文档工具配置及恢复说明，不加入平板或采音流程。
- [ ] T065 更新 docs/release-checklist.md，映射 spec SC-001–SC-006 到各故事证据；缺真实局或未授权资料处理不能声称完整交付。
- [ ] T066 按 specs/001-local-dm-mvp/quickstart.md 复核目标提交的构建、自动化、实际UI、发布物与输入规模耗时，在 docs/evidence/final-validation.md 记录实际结果。
- [ ] T067 核对 .specify/memory/constitution.md、当前 spec/plan/tasks 与 docs/decisions/0001-single-pc-scope.md，更新 docs/project-state.md、获准记忆并提交通过验证的本地变更，不自动推送。

**Checkpoint**: 上述结果有实际证据后才能进入依赖它的阶段；当前全部未执行。

## Dependencies & Execution Order

Setup → Foundation → US1 → US2 → US3 → US4 → US5 → Polish。
US1 是首个合成口播演示切片，不等于完整 MVP。
基础契约锁定后，US1 的外部语音/模型适配与 US2 的文档提取可由不同所有者并行；同一 App 组合或 ViewModel 的接线由根实施者串行合并。
US3 的规则测试可使用固定合成包独立运行，产品接线仍需 US1/US2。US4 依赖 US1/US3。US5 整局依赖全部故事。
任何真实剧本远程调用、不可用引擎或纸笔默认不适用均为显式门禁，不以静默 fallback 继续。

## Parallel Example: User Story 1

同一检查点后，MagpieGatewayTests.cs 与 SpeechLifecycleTests.cs 可并行，不能同时编辑 HostViewModel.cs。

## Parallel Example: User Story 2

DocumentToolContractTests.cs 与 ImportCoverageTests.cs/PublicReadinessTests.cs 可并行；ImportPipeline 接线等待各适配契约通过。

## Parallel Example: User Story 3

PhaseFlowTests.cs/RevealLockTests.cs 与 IdempotencyTests.cs/RecoveryTests.cs 可并行；引擎与存档事务接线由单一所有者处理。

## Parallel Example: User Story 4

TextSubmissionTests.cs 与独立 HostUncertaintyTests.cs 可分别编写，HostInputController/HostViewModel 接线串行。

## Parallel Example: User Story 5

锁定发布目录后，打包者与独立干净用户环境验证者可并行；真实一局等待实际发布物可用。

## Implementation Strategy

先完成 US1 获得看得见、听得到的切片。再做完整来源与确定性玩法，不用自由聊天掩盖导入或流程缺口。
每段先准备有意义的边界用例，再实现并通过真实界面验证；不扩展通用 OCR、语音大模型、平板或账户。
纸笔汇总只在开局明确接受且原本允许时使用；正式游玩读取冻结包，不要求 MinerU 服务运行。
不把合成测试或自动化当作真人整局。最终交付必须满足所有成功标准。
选择一个故事实现前阅读其契约及 data-model 约束，任何契约变动先同步设计再改代码。

## Notes

当前任务总数 67，其中可并行标记 11。故事任务计数：US1=10，US2=13，US3=12，US4=6，US5=8。
本轮只生成清单，不将已存在的探针或规划文档当作应用实施完成。

## 实施状态（2026-10-19 本轮实施后）

`scripts/validate-planning.ps1` 强制每行保持 `- [ ] Tnnn ...` 字面格式，因此勾选框不在此改动；
实际完成状态以下表为准。状态取值：已完成 / 部分完成 / 受阻 / 未执行。

| 任务 | 状态 | 证据或差异 |
|---|---|---|
| T001 | 已完成 | docs/evidence/setup/environment.md |
| T002 | 已完成 | LocalDm.slnx, global.json, Directory.Packages.props, packages.lock.json |
| T003 | 已完成 | .gitignore（stories/密钥/数据库/构建产物/合成真值） |
| T004 | 已完成 | fixtures/synthetic + scripts/make-synthetic-fixtures.py |
| T005 | 已完成 | tests/LocalDm.Tests, tests/LocalDm.UiTests |
| T006 | 已完成 | docs/evidence/README.md |
| T007 | 已完成 | src/LocalDm.Core/Domain/SourceModels.cs |
| T008 | 已完成 | src/LocalDm.Core/Domain/ToolSettings.cs |
| T009 | 已完成 | src/LocalDm.Core/Ports/ExternalPorts.cs |
| T010 | 已完成 | src/LocalDm.Core/Domain/PublicProjection.cs |
| T011 | 已完成 | Storage/SessionStore.cs + SqliteSchemaSql.cs（SessionStoreTests 7 项） |
| T012 | 已完成 | src/LocalDm.Core/Domain/HostModels.cs |
| T013 | 已完成 | src/LocalDm.Core/Domain/ImportModels.cs |
| T014 | 已完成 | AppComposition.cs + Diagnostics/RedactedLog.cs |
| T015 | 已完成 | tests/LocalDm.Tests/MagpieGatewayTests.cs（8 项） |
| T016 | 已完成 | tests/LocalDm.Tests/SpeechLifecycleTests.cs（5 项） |
| T017 | 已完成 | src/LocalDm.Infrastructure/Magpie/MagpieGateway.cs |
| T018 | 已完成 | src/LocalDm.Infrastructure/Storage/SettingsStore.cs（DPAPI） |
| T019 | 已完成 | src/LocalDm.Infrastructure/Voice/TtsService.cs（TtsServiceTests 4 项） |
| T020 | 已完成 | src/LocalDm.Core/Hosting/HostDirector.cs |
| T021 | 已完成 | Views/SetupView.xaml + SetupViewModel.cs |
| T022 | 已完成 | Views/HostView.xaml + ViewModels/HostViewModel.cs |
| T023 | 已完成 | Views/LibraryView.xaml + MainWindow.xaml |
| T024 | 部分完成 | docs/evidence/us1/desktop-speech.md（125%/150% 缩放未截图） |
| T025 | 已完成 | tests/LocalDm.Tests/DocumentToolContractTests.cs（10 项） |
| T026 | 已完成 | tests/LocalDm.Tests/ImportCoverageTests.cs（11 项） |
| T027 | 已完成 | src/LocalDm.Infrastructure/Documents/MineruPrivacyPreflight.cs |
| T028 | 部分完成 | 设置页含 MinerU 路径字段；独立 profile 配置页未做 |
| T029 | 已完成 | src/LocalDm.Infrastructure/Documents/MineruRunner.cs |
| T030 | 已完成 | src/LocalDm.Infrastructure/Documents/MineruContinuationReader.cs |
| T031 | 已完成 | src/LocalDm.Infrastructure/Import/SourceIndex.cs |
| T032 | 已完成 | src/LocalDm.Core/Import/PreparedPackageReader.cs |
| T033 | 已完成 | src/LocalDm.Infrastructure/Import/ImportPipeline.cs |
| T034 | 已完成 | src/LocalDm.Core/Import/ScriptValidator.cs + ScriptPackageBuilder |
| T035 | 部分完成 | ReadinessProjection.cs 已完成；ImportView/ReadinessView 界面未做 |
| T036 | 受阻 | docs/evidence/us2/tool-parse.md（quality_tier_unavailable） |
| T037 | 受阻 | docs/evidence/us2/real-script-readiness.md |
| T038 | 已完成 | tests/LocalDm.Tests/PhaseFlowTests.cs（16 项） |
| T039 | 已完成 | tests/LocalDm.Tests/IdempotencyTests.cs + RecoveryTests.cs |
| T040 | 已完成 | src/LocalDm.Core/Domain/GameModels.cs |
| T041 | 已完成 | src/LocalDm.Core/Game/GameEngine.cs |
| T042 | 已完成 | src/LocalDm.Core/Game/CommandDispatcher.cs + SessionStore.cs |
| T043 | 已完成 | src/LocalDm.Core/Game/PublicSessionProjector.cs |
| T044 | 已完成 | HostView 阶段面板：无剧透条件 + 大家准备勾选 |
| T045 | 已完成 | HostView 纸笔汇总录入 + 二次确认（SessionViewModel.TryParseVotes） |
| T046 | 已完成（实现方式已变更） | src/LocalDm.Core/Hosting/PublicContextBuilder.cs（只取当前阶段允许公开的原文）。**没有** `PublicReplyPolicy.cs`：输出侧按产品决定不做审查（秘密隔离放在拼提示词处），原描述的「批准过渡句白名单」未实现。 |
| T047 | 已完成 | HostViewModel 接入真实 session 与阶段操作 |
| T048 | 已完成 | src/LocalDm.Infrastructure/Storage/SessionRecovery.cs + 继续一局 |
| T049 | 已完成 | docs/evidence/us3/flow-recovery.md + ui-session-steps.json |
| T050 | 已完成 | tests/LocalDm.Tests/TextSubmissionTests.cs（7 项） |
| T051 | 已完成 | src/LocalDm.App/Input/HostInputController.cs |
| T052 | 已完成 | src/LocalDm.Core/Hosting/HostIntentRouter.cs |
| T053 | 已完成 | HostView 输入提示 + 提案提示 |
| T054 | 已完成 | tests/LocalDm.Tests/HostUncertaintyTests.cs（10 项） |
| T055 | 部分完成 | docs/evidence/us4/openless-input.md（真实语音与 20 次压力未执行） |
| T056 | 已完成 | tests/LocalDm.UiTests/ReleaseSmokeTests.cs（2 项，真实 WPF 窗口） |
| T057 | 已完成 | src/LocalDm.App/Properties/PublishProfiles/WinX64.pubxml |
| T058 | 已完成 | packaging/windows/LocalDm.iss（per-user，卸载保留数据） |
| T059 | 已完成 | packaging/windows/Publish.ps1（Setup + ZIP） |
| T060 | 部分完成 | 设置页有 Magpie/音色/MinerU 诊断；OpenLess 无独立诊断项 |
| T061 | 已完成 | docs/evidence/us5/install-portable.md + portable-smoke.json |
| T062 | 未执行 | docs/evidence/us5/real-tabletop.md（需真人到场） |
| T063 | 已完成 | docs/evidence/us5/fault-matrix.md + FaultMatrixTests（4 项） |
| T064 | 已完成 | docs/user-guide.md |
| T065 | 已完成 | docs/release-checklist.md |
| T066 | 已完成 | docs/evidence/final-validation.md |
| T067 | 已完成 | docs/project-state.md 已更新并本地提交 |

统计：已完成 59 项，部分完成 5 项（T024/T028/T035/T055/T060），受阻 2 项（T036/T037），未执行 1 项（T062）。
部分完成与受阻项的差异均已写入对应 docs/evidence 文件，未以自动化替代真人验收。
