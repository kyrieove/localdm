# 当前项目状态

2026-10-22 增量（公开消息同步与真实剧本上平板，分支 `agent/minimax`）：

- **公开消息记录已落地**：桌面上每落一条消息（玩家发言、主持回复、公开流程事件）
  就往存档 `public_messages` 写一条，按本局单调编号；本机提示与本机诊断不进公开消息。
  写失败显「同步异常」（状态行 + 带「重试同步」的横幅），不吞掉、不报告「已同步」，
  重试后编号仍连续。平板侧新增 `GET /tablet/public?after=N`：返回当前环节、
  公开消息与下一序号；没有浏览器会话 401，`after` 带错 400。
  **但链路还差最后一跳**：平板服务目前独立运行，看不到桌面那一局的存档，
  包模式下公开对话是空的（不拿合成对话冒充真实本局）。见
  `docs/plans/2026-10-21-tablet-distribution.md` 第 5.2、5.3 节。
- **真实剧本《孽岛疑云》已经在平板网站上跑通**：冻结包补了平板清单
  （54 张卡、10 组里 9 组可用、106 个资产；一组因正反面配不唯一而整组暂停，
  目录里看得到、点不开、取图返回 409），发布版服务真的起起来并实测：
  目录、卡面、卡背、角色本（12 章）、原稿页全部按真实剧本取到，浏览器里页面渲染
  出 5 个真实角色。证据见 `docs/evidence/tablet/2026-10-22-real-script-tablet.md`。
- **仍未做**：平板服务还没接进 WPF（开关、二维码、防火墙指引、服务读真实存档）；
  真实平板设备未验收；导入向导里的准备区未做（清单准备目前只有命令行 `--prepare-tablet`）。

2026-10-21 当前增量：文字阅读器已进入合成探针，可认领角色并阅读“文库纸页”文本，保存字号、行距、文本位置；源页按本人角色取图。独立验收与剩余项见 `docs/evidence/tablet/2026-10-21-codex-reader-acceptance.md`。真实剧本导入接入、WPF 内嵌服务和开关、正式二维码入口、真实公共对话同步、真实 iPad/安卓验收尚未完成。二维码与网址均保留。以下旧的单端范围和旧宪章版本属于历史状态，当前规划范围以宪章 5.0.0、ADR 0005 及最新方案为准。

2026-10-21 规划补充：局域网平板分发一期方案已写，见 `docs/plans/2026-10-21-tablet-distribution.md` 和 ADR 0005，宪章已升 5.0.0。卡片按完整名称列表点选，不做环节解锁；拆分不可靠的组暂停分发；Blazor Server 待真实平板浏览器验证（不限定 Safari，iPad 与安卓平板用各自可用的浏览器）。此项尚未实施，以下已有功能与历史验收不代表平板能力通过。

2026-10-21 实施补充（第一步技术探针）：`src/LocalDm.Tablet/` 已建并加入解决方案，五项审查发现已全部修正——交互渲染模式显式声明、合成图片改为真实 PNG（zlib 包装，浏览器可解码）、状态按 Cookie 会话隔离、测试改为打真实应用、`Windows.Data.Pdf` 实际接入并验证页数与逐页渲染。桌面真实浏览器（Chrome 156）已确认选角色、点卡、翻页、刷新保持、会话隔离与 409/404 契约；证据见 `docs/evidence/tablet/` 与 `artifacts/tablet/`。**真实 iPad 与安卓平板浏览器验收未执行**，未通过前不进入第二步建设；二维码库本机离线不可用，待第三步前锁定。

更新日期 2026-10-20。本轮执行 `speckit-implement`，按 tasks.md 实施 US1–US5 与收尾；
随后做了一轮代码审查并修完全部发现（见 `docs/reviews/2026-10-20-code-review.md`）；
再做五轮交互与界面自检（见 `docs/reviews/2026-10-20-ux-hardening.md`）；
最后按第二版原型把桌面端重塑为对话式界面，并把流程改成剧本数据驱动、
确认改成「点击即确认」（见 `docs/progress/conversation-ui-v2.md`）。

功能为 001-local-dm-mvp，活动规范是 specs/001-local-dm-mvp/spec.md。
宪章 4.0.0；范围为从零自建 Windows 单电脑软件。OpenLess 负责外部转写，Windows Magpie 普通文字模型与 System.Speech TTS 主持。

## 已实现

- **AI 主持改造（2026-10-20，宪章升 4.0.0）**：主持模型作为 DM，开局就读组织者手册（含真相、
  主持人专用页）与全部线索卡正反面，**任何时候都不读角色本**——隔离在上下文构造处按剧本包的
  角色/材料来源 span 判定，不靠目录名或段 ID 猜。固定提示词只替换剧本名与人数，system 整局逐字不变，
  当前局面、真相状态与 model ID 移到每轮最后一条 user 消息里，前缀缓存可复用。
  对话历史改由 `DmConversation` 管理（备忘 + 原话轮次 + 真实 token 用量），按模型上下文窗口计量，
  占用到 80% 先把较早对话整理成备忘（保留最近几轮原话），整理失败不丢内容，只有发不出去才丢最早的轮次
  并明说丢了几轮；操作者可点「压缩上下文」或整句输入 `/compact`。记忆存进 `dm_conversations` 表，
  重启后接得上且不重播。输入分流改成整句匹配，问句一律交给模型。
  详见 `docs/plans/2026-10-20-ai-dm-organizer-context.md`、`docs/decisions/0003-ai-dm-reads-organizer-and-clues.md`。
  人工试玩验收（方案第 5 节 13 条对话）**已执行三轮，第三轮 13 条全合格**，见
  `docs/evidence/us3/ai-dm-acceptance.md`。第一轮暴露的「整本规则复读」「拿公开信息做减法」
  靠提示词第 2、5 条的措辞修掉；「备忘写进未发放的卡面细节」除了收紧整理请求，
  还加了一道判断 `DmMemoCheck`（`src/LocalDm.Core/Hosting/DmMemoCheck.cs`）：
  备忘里有一句照着卡面原文写了 7 个字以上、桌上又没人念过这一段，就整份不采用，聊天区明说这一轮照常发送。
- **对话式界面（2026-10-20 重塑）**：278 px 左侧轨道（剧目 → 进度 → 当前提示 → 可用动作 → 声音/设置），
  其余全部给主区；主区是一条连续对话（玩家消息、主持回复、阶段事件同一列表），底部固定输入区；
  「进行中/失败」行排在对话末尾，空闲不占屏。逐条重播只复用已保存文字，停止随忙碌出现。
  公开材料与已解锁真相合到宽幅主区。设置页收进音色/自动朗读/模型，caller key 输入与
  MinerU 路径退出游玩界面（鉴权与密钥读取能力仍在）。
- **流程由剧本包驱动**：`ScriptFlow` 声明每个环节（标题、提示、条件、材料、动作名、是否投票/揭秘、
  来源依据），`RealScriptPackageBuilder` 按声明生成阶段；没有声明就只生成一个「流程待确认」阶段并
  标 NeedsReview，不再套固定七步。环节动作由 `ScriptStepKind` 决定，动作名由数据给出
  （例如「进入第二轮搜证」）。
- **点击即确认**：`GameCommand` 不再有 `GroupReady`/`Confirmed`，界面上也没有对应勾选框。
  保留的仍是机器可验证的条件：剧本包 Ready、阶段允许该动作、投票阶段需合法汇总、
  票数总数等于人数、材料属于本阶段、`RequiresAllMaterials` 时材料必须发完、revision 与包指纹校验。
- **冻结身份含流程**：`ScriptPackage.ContentHash` = 来源 + 玩家数 + schema + 规范化阶段，
  `PackageId` 由它生成。改流程会得到新的包标识与新文件名，旧文件保留；旧存档仍绑原包，
  拿到被改写的包会 `PACKAGE_MISMATCH` 并说明原因。存档还在、包不在时剧本库显式提示不能恢复
  （`ISessionStore.ListSessionsAsync`）。
- **流程声明必须自洽**：空/重复环节 ID、要求材料却没声明材料、以及指向不存在的材料编号 /
  段落 ID / 来源分组，都会变成点名具体 ID 的阻塞问题，整包 `NeedsReview`。
- **投票按环节归档**：`Session.VoteSummaryPhaseId` + `VoteHistory`，第二轮投票必须重新提交，
  揭秘要求每一轮都有记录；`vote_summary` 列换成按环节的记录列表，旧裸数组格式仍可读。
- **停止入口在轨道上**：生成/播报期间所有页面（对话、复盘、设置、剧本库）都能停；
  重播也占住会话播报闸，失败显示「重播失败：<原因>」而不是「已重播」。
- 确定性玩法：`GameEngine` 决定阶段/材料/票数/揭秘；`CommandDispatcher` 事务内去重与 revision 校验；
  单文件 SQLite，event/receipt/投影同一事务。
- 秘密隔离：`PublicSessionProjector` 只投影 Public 段；`PublicContextBuilder` 只取当前阶段允许公开的原文（私有与真相段即使被误传也不进上下文）。
  注：仓库里**没有** `PublicReplyPolicy` 这个文件 —— 早前的 `docs/project-state.md` 与 `specs/.../tasks.md`
  曾把它记成已完成，属文档漂移，已按实际实现更正（输出侧不做审查，产品决定是「回什么念什么」）。
- **真相解锁判定收口**：`TruthAccess.IsUnlocked` 是唯一判据（`Revealed`/`Completed`，
  或 `Paused` 且 `ResumeState=Revealed`），投影、就绪检查、主持口播三处共用；
  真相原文只从 `Visibility == Truth` 的段取。公共投影重建会话时会把「已解锁」带回去，
  否则暂停在揭秘态的一局会「屏幕显示真相、提示词里没有真相」。
- **「进行中」就地反馈**：`HostActivity` + `ActivityDots`（呼吸动效）回答「点了按钮到听见声音之间
  在干什么」——文字、流水线步骤、已等待秒数、失败与取消。它现在渲染在对话末尾，只在
  `IsDisplayed`（正在做或已失败）时出现；空闲不再有一块常驻的「就绪」。
- **真相按块渲染**：`TruthDocument` 把扫描件转出的 Markdown 拆成标题/等宽块/管道表/段落；
  `MonospaceGrid` 按视觉空间（汉字全角、西文半角）推导列参考线，列位不再漂。
- **动作按阶段折叠**：投票录入区只在投票环节且未汇总时出现、公开真相只在真相环节出现、
  发牌只在有本轮待发编号时出现；动作名来自剧本包。
- **存档 id 规则收口**：`SessionIds.For` 一处决定，演示剧本与真实剧本不再各算各的。
- 播音：`HostDirector` 串行单段、requestId 失效即丢弃晚到结果、重播不请求模型。
  微软 Edge-TTS 神经音色是**可选增强**（本机 python + edge-tts），不可用即降级到 Windows 本地语音；
  引擎契约 `IEdgeTtsEngine` 可注入，降级路径不依赖宿主机环境。
  详见 `docs/decisions/0002-edge-tts-and-group-classification.md`。
- **角色本/线索卡分流采信导入源头的事实**（`ExtractedPage.GroupKind`），目录名启发式仅作兜底。
  分错会让私有角色本的分组名进入公共材料编号，这是秘密隔离的红线。
- 文档准备：`MineruPrivacyPreflight`（版本/app-owned home/遥测）、`MineruRunner`（ArgumentList + 异步 stdout/stderr）、
  `MineruContinuationReader`（续读到 next_request=null + 循环检测）、`ImportPipeline`（无授权时在调用工具前拦截）。
- 发布物：自包含 win-x64、便携 ZIP、Inno Setup per-user 安装器。

## 验证结果（实测）

- 2026-10-22 三方分支合并并修完验收问题后重跑：`dotnet build LocalDm.slnx` 0 警告 0 错误；
  `LocalDm.Tests` 398 项（392 通过 / 6 项人工授权跳过的语音用例 / 0 失败）；
  `LocalDm.UiTests` 34 项通过 0 失败。界面测试窗口显示在虚拟屏幕外，不打扰使用者。
  逐项问题与复现方式见 `docs/reviews/2026-10-22-three-agent-acceptance.md`。
- 再合入 VoxCPM 音色那条线（`085ecc6`）后重跑：`LocalDm.Tests` 420 项
  （413 通过 / 7 跳过 / 0 失败），第 7 条跳过是新增的「Windows 内置音色不退回 Edge」
  用例——它会真的念一句中文，与 EdgeTtsTests 那几条同类，按同一人工授权跳过，
  同一断言另有一条不出声的用例覆盖。`LocalDm.UiTests` 34 项通过 0 失败。
  VoxCPM 的声音本身仍未在本机跑通，根因与修法见
  `docs/evidence/us3/voxcpm-tts-integration.md`。
- `LocalDm.Tests`：249 项（243 通过 / 6 项人工授权跳过 / 0 失败）；`LocalDm.UiTests`：16 项通过，
  合计 **259 通过 / 0 失败 / 6 跳过**（2026-10-20 对话式界面轮把「勾选确认」相关断言改成「点击即确认」，
  并新增流程数据驱动、双轮无投票、连点只提交一次、对话与自动朗读/停止/重播等覆盖；
  AI 主持收尾轮新增 `DmMemoCheckTests` 6 项）。
- 2026-10-20 全量重建：0 警告 0 错误。
- 界面快照产物在 `artifacts/ui/`：
  - `desktop-100-*.png`（8 张）：真实 exe + UI Automation 走完「体验演示 → 开局 → 搜证 → 逐个发牌 →
    设置 → 回到本局 → 投票 → 公开真相」，主屏 100% 抓客户区；
  - `desktop-125-*.png`（1 张）：同一窗口移到 125% 副屏，真实 DPI 缩放；
  - `hostdesk-*.png`（10 张）：真实 WPF 渲染，含 `hostdesk-scale-150.png`（150% 下的 DIP 空间）。
- 真实窗口脚本：`pwsh -File scripts/verify-live-window.ps1`（数据根指向临时目录，不碰真实存档）。
- 真实 Magpie 合成请求 + 真实中文播音通过（10.3 秒），重播 0 ms 不请求模型（上一轮记录）。
- 安装器静默安装 → 启动 → 卸载全部退出码 0，卸载后用户数据保留（上一轮记录）。
- 详细证据见 `docs/evidence/`，核对表见 `docs/release-checklist.md`。

## 受阻与未执行（不得当作已交付）

- **真实剧本准备受阻**：本机 MinerU 4.0.1 无 basic/standard/advanced 引擎（`quality_tier_unavailable`）。
  `stories/<剧本目录>` 全部为 JPG 页面，依赖 OCR，因此尚未成为 Ready 包；
  并且现在还需要准备者按原文写出 `--flow` 流程声明才可能 Ready。
- **真实语音可听性未由人耳确认**：只验证了音色枚举与语音调用链路（界面显示真实的
  「云希 · 微软神经男声」）。
- **真实 4–6 人整局未执行**（需真人到场）。
- OpenLess 真实语音转写落字、20 次发送/取消/重播压力未执行。
- 150% 缩放未改系统 DPI（属系统设置），改用同等 DIP 空间渲染验证。
- 发布物未做代码签名；打包版/便携版未在本轮重跑。

## 下一步

1. 按剧本原文为每个真实剧本写出 `--flow` 声明（示例 `docs/prepare/script-flow.example.json`），
   再跑 `tools/LocalDm.Probe`，让真实剧本真正成为 Ready 包。
2. 在目标机用真人耳朵确认 Edge-TTS 与本地 SAPI 两条朗读路径，以及 125%/150% 下的实际观感。
3. 组织 4–6 名真人完成一局，替换 `docs/evidence/us5/real-tabletop.md` 的「未执行」。
4. 重跑打包版与便携版验收（含崩溃日志落点 `%LOCALAPPDATA%\LocalDm\logs\crash.log`）。

tasks.md 的完成状态见其末尾「实施状态」表（59 已完成 / 5 部分完成 / 2 受阻 / 1 未执行）。

## 历史（实施前）

设计阶段曾生成 plan 的研究与 Phase 1 设计产物，tasks 共 67 项：US1=10、US2=13、US3=12、US4=6、US5=8，其余 18 项为 Setup、Foundation、Polish。
`.specify/extensions.yml` 不存在，因此 before/after implement 钩子无注册项可执行。
设计验证命令仍是 `scripts/validate-planning.ps1`（校验设计文件、任务格式/编号/故事、12 条约束及活动功能），本轮实际运行通过。
真实剧本 `stories/`、运行数据与密钥由 `.gitignore` 排除（含 `fixtures/synthetic/.private/` 的合成真值）。
