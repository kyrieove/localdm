# 本局主持：Windows 单电脑 MVP 实施计划

> 本文件保留需求演进与预览背景。正式 Spec Kit 设计现位于 [spec.md](../../specs/001-local-dm-mvp/spec.md)、[plan.md](../../specs/001-local-dm-mvp/plan.md) 和 [tasks.md](../../specs/001-local-dm-mvp/tasks.md)。存档已由早期文件快照方案收敛为单个本地 SQLite 事务，以正式设计为准。

版本 v0.3-review · 2026-10-19 · 计划与 HTML 设计预览，应用尚未实现。

## 一、目标与已确认范围

从零自建一款 Windows x64 桌面软件，暂称「本局主持」。
4–6 名真人线下围坐，所有人当玩家，AI 根据原剧本承担公共主持。
首版仅适配一套完整固定剧本，不承诺任意商业本自动可玩。
个人角色本和私密线索在软件外使用独立 PDF、纸本或编号材料包查看。
不复用 SoloPlay，不做平板连接、用户账号、房间、私聊和跨设备同步。

核心链路为 OpenLess 转写 → 输入框发送 → Windows Magpie 普通文字模型 → TTS 朗读。
按钮可以直接触发开场、当前规则、重播、下一环节、暂停和复盘。
不使用语音大模型，不自建录音、ASR 或 OpenLess API。
文档解析和 OCR 优先调用本机已有 MinerU 或经验证的 pdf-inspector，不重建识别引擎。
准备阶段生成可审计的冻结剧本包，正式游玩不再依赖 OCR 服务运行。

本计划替代旧方案的 SoloPlay、平板联网、逐设备准备和投票范围。
旧宪章 2.0.0 与研究文档保留历史记录；实施前同步为评审通过的单电脑范围。
本轮只交计划、设计预览及合成探针证据，不搭建应用或修改 Magpie 配置。

| 编号 | 已确认需求 | 产品行为 |
|---|---|---|
| R1 | 所有人都是玩家 | 操作电脑的人不读真相，不替 AI 裁决 |
| R2 | 公共语音控场为核心 | 口播开场、规则、搜证、投票主持、结果和复盘 |
| R3 | 按需交互 | 玩家用 OpenLess 说话或点击按钮，AI 再回应 |
| R4 | 不主动打扰 | 不监听、不催进度、不主动提示、不自动推进 |
| R5 | 忠于原本 | 不编凶手、线索、规则或结局；没写清就说明 |
| R6 | 单电脑 | 不做联网玩家端、私密问答或多人问答队列 |
| R7 | 私人材料在软件外 | 软件只按阶段提示打开哪个材料包 |
| R8 | 全员确认推进 | 大家口头确认，操作者勾选后，程序另检查原本条件 |
| R9 | 普通问题直接回应 | 回车发送后不再弹转写确认；状态操作仍须明确确认 |
| R10 | 成型软件 | 安装后双击启动，有窗口、设置、存档、恢复和错误诊断 |
| R11 | Windows Magpie | 普通文字模型调用经本机 Windows 版，不使用 WSL |
| R12 | 完整 PDF 和图片 | 逐页提取、OCR、来源映射及无剧透校验 |
| R13 | 文字模型加 TTS | 模型只生成文字，独立 TTS 朗读，不要求音频模型 |
| R14 | 使用现有 OpenLess | 聚焦输入框后用其快捷键转写，再回车或点击发送 |
| R15 | 复用现有文档工具 | 先验证 MinerU 或 pdf-inspector 的程序入口，解析结果经过来源和保真校验 |

## 二、需要评审的最小默认

纸笔投票后由操作者录入汇总并二次确认，程序按原本结算。
这个默认尚未由用户单独确认，不假称单公共屏幕能提供电子秘密投票。
Windows 系统中文音色先作为基础播音方案，页面提供本机实际样音供试听。
若原本依赖复杂私密数字行动或真人裁判，标记首版不支持，不擅自改规则。
普通回答只使用当前阶段可公开材料，揭秘前不将完整真相交给公共回答模型。
Windows Magpie 及 OpenLess 均为用户已有独立软件，不重新打包或读取它们的供应商密钥。
本机入口不等于本地推理；真实剧本文字发给 Magpie 下游之前须明确允许。

## 三、实际使用流程与界面

### 1. 首页和首次设置

桌面快捷方式打开独立窗口，不要求玩家运行终端或浏览器。
首页提供本地剧本库、导入资料、继续上次一局和设置。
设置包括 Magpie 地址、可用模型、中文音色及 OpenLess 输入测试。
显示模型连接的真实结果，不能用 Mock 样例伪装成功。

### 2. 无剧透准备

选择 PDF 或图片文件夹，登记文件指纹、文件清单、页数和顺序。
调用已验证的本机文档工具提取或 OCR，保存原文和 SourceSpan，再整理角色、主持环节、线索和真相。
先做一个可用的工具入口，不搭通用插件平台；同时支持导入已有的完整解析结果及来源索引。
保留工具版本、原文件指纹、页码和块定位；长文档必须完整续读，不能把首段输出当成全文。
模型不得补缺失事实；低置信度、关键缺漏和矛盾必须显式保留并阻止开局。
报告只显示覆盖情况、人数、页数、异常类别和是否准备完成，不透出秘密。
用户可以整理顺序、文件夹名称和材料包编号，不需要读真相或校对秘密。
必须读真相才能解决的问题交给独立授权校验者，或更换首版样本。
检查未发现错误不等于证明全部事实正确，仍需来源核验和完整一局验证。

### 3. 公共主持台

只展示公开标题、当前环节、已公开主持记录及材料包编号。
底部为文字输入框、OpenLess 提示、发送、停止播报、重播本段、下一环节和暂停保存。
玩家聚焦输入框，用 OpenLess 已有快捷键转写，回车或点击发送。
这只是正常发送动作，不再要求逐次确认转写文字。
按钮触发的开场和规则说明可直接使用原文口播，不必为每一句请求模型。
大家讨论时软件静默等待，不访问麦克风。

### 4. 状态操作与恢复

语音转写中的「下一阶段」「进入复盘」等只打开确认界面，不直接改状态。
必须满足剧本条件且勾选大家已准备好，程序才能提交下一步。
纸笔投票后录入汇总，检查票数及规则并二次确认，再结算。
每次提交自动保存；关闭或强退后恢复当前阶段及已提交材料记录。
恢复后不自动播音；重播只读已生成内容，不重做线索发放或阶段操作。
模型超时保留阶段，TTS 失败保留公开文字并提供重试。

## 四、本轮已验证的事实

| 项目 | 实测证据 | 不能据此声称的能力 |
|---|---|---|
| Windows Magpie | D:/2/download/google/magpie-windows-amd64.exe，版本 0.1.1291 | 不采用 WSL 版本结论 |
| 本机模型列表 | 复核监听 127.0.0.1:3425，GET /v1/models 返回 200 与 57 个条目 | 列表存在不保证每个模型都可生成 |
| DeepSeek 文本请求 | group/auto-deepseek-v4-1-flash 请求返回 deepseek-v4.1-flash 和「连接成功」 | 只证明非流式合成文字调用，未测工具与结构化输出 |
| Sol 模型 | 列表有 codex/gpt-6.1-sol | 本轮没有对 Sol 发生成请求 |
| 认证 | 本轮请求未携带认证头成功 | 不代表所有网关模式都无需 caller key |
| OpenLess | D:/2/software/OpenLess/openless.exe 正运行，安装项也存在 | 尚未证明它能向打包后窗口正确注入完整文本 |
| MinerU | C:/Users/ASUS/.local/bin/mineru.exe，version 返回 4.0.1 | CLI 存在；解析服务未运行，实际引擎、质量和隐私边界未验证 |
| pdf-inspector | 本轮未在当前 PATH 和已检查的技能目录定位到入口 | 不宣称其支持 OCR，后续定位后再检查实际能力 |
| 中文 TTS | Microsoft Huihui Desktop 已生成本地 WAV 样音 | 未测真实会场扬声器、音量和自然度 |
| 真实资料 | stories 有 180 个 JPG 和 1 个 TXT | 未解析，不代表完整、正确或已可玩 |
| 开发环境 | dotnet --list-sdks 无版本输出 | 实施前需检查并准备选定 SDK |

先前端口快照没有监听，随后复核已见上述端口，以最新实际请求为准。
/health 返回 404，不能用它判断 Magpie 离线；以已验证的模型列表和生成请求诊断。
本轮仅向 Magpie 发送「请只回复四个字：连接成功。」这一合成输入。
TTS 样音也为虚构口播，没有向模型提交任何真实剧本，没有访问麦克风。
文字调用和 TTS 分别验证，尚未形成应用内完整链路。
早期 Windows 内置 ASR 探针出现误识别；因采用 OpenLess，已从首版移除该自建方案。

## 五、推荐架构与替代方案

| 方案 | 取舍 | 决定 |
|---|---|---|
| WPF、C#、.NET Windows | 一个 Windows 目标，原生文件与系统语音支持，独立桌面窗口 | 推荐，P0 验证 OpenLess 输入和播音 |
| Electron、TypeScript | 界面设计方便，但增加 Chromium 和原生桥接 | 原生探针不适用时再评估 |
| 本地网页 | 原型快，但还要处理浏览器、启动器及软件交付 | 不作为本次成品形态 |

应用不启动局域网服务器，不提供账户和多设备功能。
本地事件编号、唯一操作 ID、单写入者及原子快照即可支持首版存档，不设数据库服务器。

| 模块 | 职责 | 边界 |
|---|---|---|
| DesktopUi | 首页、准备、主持台、设置和确认 | 公共窗口不绑定整个真相对象 |
| TextInput | 普通文字框及 OpenLess 输入落点 | 不采音，不猜其 API，不模拟全局快捷键 |
| DocumentToolRunner | 调用已验证的 MinerU 等已有程序，归一化结果 | 不重建 OCR，不自动升级、不改配置、不静默上传 |
| ImportPipeline | 排序、原文与页块索引、解析产物和冻结剧本包 | 不静默截断，不替换样例，不补造事实 |
| ScriptValidator | 缺漏、冲突、字段来源和准备状态 | 有关键问题禁止 Ready |
| GameEngine | 阶段、材料包提示、投票汇总和结算 | 模型没有状态写入权限 |
| HostDirector | 固定口播与当前公共回答上下文 | 未解锁真相不进入普通问答 |
| MagpieGateway | 已验证的本机普通文本请求 | 地址和 model ID 可配置，凭据不进日志 |
| TtsService | 中文朗读、停止和重播 | 一次一段，重播不重复业务操作 |
| SessionStore | 事件、快照、恢复及版本冻结 | 数据不写安装目录，故障不跳阶段 |

主要数据形状如下。

- ScriptPackage 保存版本、文件清单、SourceSpan、公开主持段落、材料包索引、阶段与隔离真相。
- ImportResult 区分 Ready、NeedsReview、Failed；只有 Ready 能创建新局。
- GameSession 保存剧本版本、currentPhase、revision、已公开材料、票数汇总和状态。
- GameCommand 含 operationId、expectedRevision 和明确动作；重复 ID 返回原结果。
- SpeechState 区分 Idle、GeneratingText、Speaking、Cancelled、Error，不存在应用录音态。
- HostReply 持有允许公开的口播及来源，不能携带执行下一步的自由代码。

固定口播优先用原文或经过来源核对的模板。
动态回答先裁剪公开来源；无法确认安全时使用固定不确定或拒绝透露回应。
不能将一次 LLM 自审或提示词当作防剧透证明。
OpenLess 转写结果和剧本文字都是数据，不获得系统权限。

## 六、六个可独立验收的里程碑

采用本地里程碑，不引入 PR 舰队、后台定时审计或云 VM。
每段先做必要单元检查，再跑真实界面和故障场景，并记录输入规模与实际耗时。
无旧软件基线，不编造性能提升比例。
下述命令和文件是实施规划，目前尚无解决方案或安装包。
估算为工程人力，P0 后重估，不是 AI 自动执行时限。

### P0 验证既有软件连接与选型

依赖用户当前 Windows Magpie、OpenLess；估算 2–4 工时。

- [x] 发现正确 Windows 实例及 loopback 监听，没有使用 WSL。
- [x] 获取实际模型列表，对 DeepSeek 发合成文字请求并收到响应。
- [x] 独立生成 Windows 中文 TTS 样音。
- [ ] 在一个原生文字框验证 OpenLess 输入、标点、连续转写、长句和焦点保持。
- [ ] 验证 TTS 实际扬声器播放、停止、重播和释放设备。
- [ ] 验证 Magpie 超时、取消及需要的结构化输出；适配可选 caller key，不读取上游密钥。
- [x] 核实 MinerU CLI 4.0.1 存在；状态显示解析服务未运行，本轮未启动。
- [ ] 定位用户已有文档工具的可调用入口，核实本地或远程引擎及输出格式。
- [ ] 经允许的运行方式，用合成图片和 PDF 验证解析、OCR、页块定位及完整续读。
- [ ] 保存 docs/evidence/p0 中的脱敏请求、输入试验、音频及 OCR 记录。

通过条件为 Windows 原生输入框可接收 OpenLess，模型能回复，TTS 可播并取消，OCR 可追溯。
目前只通过部分独立探针，不能标记 P0 全部完成。

### P1 交付可打开的桌面外壳

依赖 P0；估算 6–10 工时。

文件为 src/LocalDm.App 下的 App.xaml、MainWindow.xaml、Views/SetupView.xaml、Views/LibraryView.xaml；
以及 src/LocalDm.Infrastructure 下的 Magpie/MagpieGateway.cs、Storage/SettingsStore.cs。
测试为 tests/LocalDm.Tests/SettingsTests.cs 和 MagpieGatewayTests.cs。

- [ ] 固定 .NET Windows SDK、win-x64 发布配置及解决方案。
- [ ] 实现首页、设置、模型列表、真实连接诊断和音色试听。
- [ ] 调用 key 使用 Windows 保护存储，界面与日志脱敏。
- [ ] 自包含发布，在无开发 SDK 的 Windows 用户环境启动。
- [ ] 验证 125% 与 150% 缩放、键盘操作及设置恢复。

验收为双击可开、偏好保留、模型离线时准确报错。
规划命令为 dotnet build LocalDm.sln 与 dotnet test LocalDm.sln；这些命令当前不可运行。

### P2 导入一套完整剧本且不剧透

依赖 P0 的现有文档工具验证、P1；估算 12–20 工时，P0 后校准。

文件为 src/LocalDm.Core 下的 Domain/ScriptPackage.cs、Import/ScriptValidator.cs；
src/LocalDm.Infrastructure 下的 Documents/MineruRunner.cs、Import/ImportPipeline.cs、Import/SourceIndex.cs；
src/LocalDm.App 下的 Views/ImportView.xaml、Views/ReadinessView.xaml。
测试为 ImportCoverageTests.cs、ScriptValidationTests.cs、PublicReadinessTests.cs。

- [ ] 建立已知真值的合成 PDF、图片、缺页、乱序及冲突 fixtures。
- [ ] 登记文件指纹、页数、顺序及提取状态，支持失败页单独重试。
- [ ] 使用已验证工具逐页提取或 OCR，保留原文，关键字段都引用 SourceSpan。
- [ ] 接入 MinerU JSON 和 locator；遇到 next_request 必须继续读取到目标材料完整结束。
- [ ] 对版本不兼容、无引擎、服务不可用、需要远程授权分别显示准确状态，不假装成功。
- [ ] 外部模型结构化结果经过类型校验，未知字段不补造。
- [ ] 脱敏报告、日志与错误不得显示凶手、私人秘密或完整真相。
- [ ] 数据外发获准后，才经 Magpie 处理真实剧本文字；否则只用合成数据。
- [ ] 以选定真实本逐项验证页、角色、阶段、线索和真相来源，不要求本局玩家校对秘密。

验收为所有页都有处理记录、关键事实可追溯，错误输入确实不能开局。
OCR 优先使用已实测可用的既有工具，不先测试或实现新的 OCR 引擎。
MinerU 已安装不代表已配置离线 OCR；本地、远程、模型下载和配置变更必须分别核实。
pdf-inspector 尚未定位，暂为候选，不作为交付所依赖的已验证能力。
远程解析真实文件必须另有明确允许，不能因工具装在本机就自动上传。
工具解析完成仍需剧本保真和开局校验；正式游玩只读取已冻结的剧本包。
PDF 文本层与扫描页分支均需通过；不能拿 JPG 成功替代 PDF 验收。
180 张本地图片只作可选真实样本输入，不等于本轮已经完成导入。

### P3 实现完整玩法与存档

依赖 P2 的固定剧本包；估算 8–14 工时。

文件为 src/LocalDm.Core 下的 Domain/GameSession.cs、Game/GameEngine.cs、Game/CommandDispatcher.cs；
src/LocalDm.Infrastructure 下的 Storage/SessionStore.cs；
src/LocalDm.App 下的 Views/HostView.xaml、Views/AdvanceConfirmView.xaml。
测试为 PhaseFlowTests.cs、IdempotencyTests.cs、RecoveryTests.cs、RevealLockTests.cs。

- [ ] 按原本实现开场、阅本、搜证、讨论、纸笔投票汇总、结算与复盘。
- [ ] 材料按编号提示，个人内容不出现在公共屏幕。
- [ ] 推进检查原本条件、预期 revision 与全员口头确认勾选。
- [ ] 投票汇总验证票数和规则，二次确认后提交。
- [ ] 同一 operationId 不重复推进、发材料或结算。
- [ ] 原子保存事件与快照，强退后恢复正确阶段，未确认操作不算提交。

验收为不靠模型修改状态也能走完固定本，暂停、重复点击和恢复均不破坏流程。

### P4 接入 OpenLess 输入、文字模型和 TTS

依赖 P1、P3；估算 4–8 工时。

文件为 src/LocalDm.App 下的 Input/HostInputController.cs、Views/HostView.xaml；
src/LocalDm.Core 下的 Hosting/HostDirector.cs、Hosting/PublicContextBuilder.cs；
src/LocalDm.Infrastructure 下的 Voice/TtsService.cs。
测试为 TextSubmissionTests.cs、PublicContextTests.cs、SpeechLifecycleTests.cs、HostUncertaintyTests.cs。

- [ ] 聚焦输入框后用实际 OpenLess 转写，回车或点击发送；不额外确认普通转写。
- [ ] 长句、标点和组合输入不被 Enter 提交逻辑截断，不靠计时器自动发送。
- [ ] 公共按钮触发固定口播；动态问题经 Magpie 普通文字模型返回后由 TTS 朗读。
- [ ] 状态意图只打开确认，不能从文本直接改阶段或提前揭密。
- [ ] 停止与重播仅影响播音，取消文本生成后不得播放晚到结果。
- [ ] 模型或 TTS 故障保留阶段并准确提示，普通文字仍可手动输入。
- [ ] 对越权揭密、提示注入和原本未写清的问题采用安全回应。

验收为真实 OpenLess 在打包后输入框连续操作，文本不丢、不重复发送，播音不重叠。
应用不得请求麦克风权限；不宣称 OpenLess 的下游完全离线。

### P5 交付可安装且能玩一局的软件

依赖 P1–P4；估算 6–10 工时。

文件为 packaging、docs/user-guide.md、docs/release-checklist.md 和 tests/LocalDm.UiTests。
交付 LocalDm-Setup-x64.exe 与 LocalDm-win-x64.zip。

- [ ] 自包含安装包及便携包均能双击启动，不要求 Node、Python 或开发 SDK。
- [ ] Magpie、OpenLess 保持用户已有独立软件；MinerU 等文档工具只在准备时使用，不强行重新打包。
- [ ] 安装、升级、关闭和卸载流程明确，运行数据不存安装目录。
- [ ] 4–6 名真人以固定真实本走完从准备到复盘，没人提前读 DM 真相。
- [ ] 验证断网、模型失败、音箱故障、输入工具退出、强退与重启恢复。
- [ ] 形成版本、截图、脱敏日志及已知限制记录，不把真实剧本打包公开分发。

完成定义为实际安装包可运行、真实一局完成、关键故障可恢复。
HTML、Mock 模型、编译或单元测试通过不能代替成品验收。
初步合计 38–66 工时；OCR、真实样本规则和软件打包差异可能改变估算。
不承诺安装包体积或响应延迟，实测后再给数字。

## 七、文件归属与交付

所有拟建应用文件根目录为 D:/2/dev/dm，当前尚未创建。

- src/LocalDm.App 保存 WPF 界面、设置入口和输入动作。
- src/LocalDm.Core 保存 Domain、Game、Hosting 与来源校验，不依赖 UI 或供应商。
- src/LocalDm.Infrastructure 保存 Magpie、现有文档工具、TTS 和本地存储适配。
- tests/LocalDm.Tests 与 tests/LocalDm.UiTests 保存核心验证和安装后的真实操作。
- fixtures/synthetic、packaging、docs/evidence 保存合成输入、发布配置及脱敏证据。

每阶段列出的测试均归入 tests/LocalDm.Tests，除非明确写为 UI 验证。
原文、真相、录音及运行数据不进入发布包或 HTML；日志默认不包含完整提示或响应。
公共界面不以隐藏样式代替真相隔离，也不把本机管理员权限当作普通公共屏幕权限。

## 八、评审与执行边界

独立材料包、Windows Magpie、普通文字模型加 TTS、现有 OpenLess 输入已确认。
文档解析优先复用本机既有工具，具体可调用能力以实测为准。
纸笔投票汇总、WPF 方案与系统音色为推荐默认，供本次 HTML 评审。
先展示计划与预期界面，再开始 P0 剩余验证和应用实施；本轮没有开始开发。
实施前同步 spec-kit 规格和宪章，避免旧平板要求继续约束新单机方案。
远程处理真实剧本文字需要数据授权；未允许前仅用合成文本做接口验证。

## 九、资料与证据

- docs/evidence/p0/magpie-text-probe.json 保存本轮合成模型调用事实。
- docs/preview/windows-dm-voice-sample.wav 为本机合成中文样音。
- docs/preview/preview-checks.json 只记录 HTML 设计预览验证，不代表应用功能通过。
- [WPF 桌面 SDK](https://github.com/dotnet/docs/blob/main/docs/core/project-sdk/msbuild-props-desktop.md)。
- [.NET 自包含发布](https://github.com/dotnet/docs/blob/main/docs/core/deploying/index.md)。
- [Windows 语音合成 API](https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer)。
