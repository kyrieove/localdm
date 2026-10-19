# AI 剧本杀 DM：开源项目调研与实施交接方案

> 历史调研与早期需求记录。用户现已放弃 SoloPlay 复用和平板联网，当前规范以 [单电脑功能规格](specs/001-local-dm-mvp/spec.md)、[实施设计](specs/001-local-dm-mvp/plan.md) 和 [任务清单](specs/001-local-dm-mvp/tasks.md) 为准。本文件下文保留历史，不代表当前实施范围。

> **交接对象：Codex / Antigravity / Claude Code**
> **整理日期：2026-10-19**
> **当前状态：2026-10-19 grilling 需求复核已由用户确认，以下第 1 节为当前基线；spec-kit 已初始化、宪章已更新；候选仓库调研保留为历史记录，尚未实现应用、部署或进行真实多人整局测试。**
> **本文目的：** 让后续编码 Agent 不必重新询问已确认需求，先核实基础仓库、授权及真实代码状态，再分阶段做出 4–6 名真人在线下无主持人条件下能玩现有推理本的 AI DM 系统。

---

## 0. Codex 接手后先做什么（优先阅读）

1. **先检查当前项目目录和 Git 状态，保护已有工作。** 运行 `pwd`、`git status --short`、列出根目录文件；若不是仓库，报告当前情况。**不得清空目录、覆盖未提交更改、强推或擅自迁移现有数据库。**
2. **先确认选型与许可，不要马上大规模写代码。** 检查 `pengcong2020520/soloplay` 最新版本是否已有许可证或取得作者明确授权。没有授权时，只能研究其公开思路与接口设计，**不要直接复制、改写分发其源码**；若授权不可得，优先使用 MIT 授权的 `EthanQC/Blood-on-the-Clocktower-auto-dm` 或自建精简基座。
3. **先做一个剧本导入 Smoke Test。** 用授权提供的测试剧本验证角色数量、主持阶段、线索、秘密和真相均能被保真提取，展示**非剧透的校验报告**，并要求每个关键字段可回指原文文件和位置。不能静默截断、猜凶手、改写线索。
4. **分阶段实施，每阶段实际运行测试。** 第一优先级不是 UI 美化，而是“完整剧本保真 + 信息隔离 + 正确推进阶段”。每阶段给出改动文件、可运行命令、通过/失败测试、下一步。
5. **首次目标只支持一个固定的真实推理本（4–6 人），** 完成一局从建房到复盘的 MVP；之后扩展通用格式和多剧本库。不要优先开发 AI 剧本生成、AI 玩家、3D 地图或全天候语音监听。

**在尚无源代码或授权材料时，先输出审计报告及具体选型决定，不要假称已有项目通过测试。**

---

## 1. 用户真实需求：已确认，不能擅自改变

### 1.1 当前确认需求（2026-10-19 grilling 复核）

核心目标是让所有朋友都能当玩家，由一位熟悉剧本、按需出声的专业 AI 主持人带完整局流程。
本节取代早期偏向个人文字问答的描述；主持流程为核心，临时问答仅作辅助。

| 维度 | 确认结论 | 工程含义 |
|---|---|---|
| 玩家 / 玩法 | **4–6 名真人朋友玩传统推理本**，无人担任 DM | 所有人都参与推理；AI 仅代替主持人；无 AI NPC 角色替代真人 |
| 剧本来源 | 可提供完整 **PDF 或图片版剧本**，接受必要的无剧透准备 | 多文件提取、OCR 和校验；玩家不读真相，不自行编排秘密；不是创作新剧本 |
| 场景 | **线下同一房间**，共用电脑、音箱；每人使用 iPad 或平板 | 一台本地服务器 + 电脑公共端 + 4–6 个平板网页端；优先局域网 |
| 交互 | **公共语音交流为主，个人材料用文字** | 大家按键说话，电脑音箱回答；平板阅读本人角色本和线索、个人操作、就绪确认及投票 |
| 控场 | **玩家触发，AI 按剧本带流程** | 不持续监听、不主动催促或提示；公共开始/下一步/重播集中电脑；推进须符合剧本及全员确认 |
| 问答边界 | 无私下问主持人；大家共同询问，不做多人问答排队 | 普通问题直接口头回应，状态操作明确确认；原本没写清就诚实说明 |
| 主持风格 | 熟悉本子的真人 DM 式表达，自然、简洁，符合剧本气氛 | 可以灵活表达和接住追问，不能加剧情、编事实或编规则 |
| 开发接受度 | **愿意用 Antigravity / Codex 等二次开发，尽量复用项目** | 优先可改造的现有系统，不要求完全开箱即用；不能为复用而保留无关复杂设计 |
| 模型 | 用户可以选择 GPT、Codex 等模型/工具，供应商不锁定 | 需要模型适配层；区分 API 模型与 CLI 编程代理，不能默认订阅账号就是 API 额度 |

### 1.2 必须实现的主要体验

- **开局前**：导入完整 PDF 或图片剧本，完成必要的无剧透准备、OCR 与保真校验，创建 4–6 人房间。
- **入场**：电脑展示二维码/短链接；iPad/平板加入，每人仅获取自己有权查看的角色本与线索。
- **开场**：玩家在共用电脑触发，AI 通过电脑音箱主持开场与公共背景，文字仅作辅助。
- **读本与搜证**：各自以文字读本和查看私密线索；AI 在玩家触发后介绍搜证规则，程序按原本发放线索。
- **共同呼叫**：大家按键控制录音；普通问题识别后直接口头回应，不要求每次确认识别文字。
- **面对面讨论**：AI 等待玩家触发，不持续监听、不主动提醒或给提示；临时问答仅辅助当前流程。
- **推进阶段**：公共开始、下一步和重复播报集中电脑；推进必须满足剧本条件及全员确认，再由 AI 语音带入下一环节；阻止重复推进与漏发线索。
- **投票与复盘**：每人平板单独投票并明确确认；根据原本结算，电脑口头主持结果与原版真相复盘。
- **规则不清**：AI 说明原文未写清及已有依据，不擅自编造临场裁决。
- **无固定真人 DM**：导入和操作不能要求其中一名玩家提前阅读真相；所有会剧透的维护入口应与正常玩家会话隔离。

### 1.3 不在 MVP 范围

- 根据主题自动写新剧本 / 自动编凶手；用 AI 扮演其他 4–6 名玩家。
- 实时持续麦克风监听、说话人分离、全程语音识别与连续打断式对话。
- 主动催进度、主动给提示、私下问主持人、多人问答排队，以及以文字聊天为核心的主持体验。
- 复杂 3D 场景、移动角色、战斗系统、D&D 骰子/属性等。
- 公网大规模部署、商用收费、剧本资源商店、多租户平台。
- 对“任意商业剧本一键百分百导入”的不现实承诺：先保证**一个已选剧本端到端正确**。

---

## 2. GitHub 项目调研：实查内容及决策

**信息口径：** 以下来自 2026-10-19 对公开仓库元数据、README、目录树和若干关键源文件的检查。**尚未本地克隆并对全部仓库做编译、UI/E2E 和真人测试。** “现有实现”意味着在公开代码中确认文件或逻辑存在，不保证线上生产质量。“预计工时”为工程估算，不是已实测进度。

### 2.1 最重要的五个候选

| 项目 | 仓库 | 实现亮点（公开代码） | 与本需求的缺口 | 许可证核查 | 建议 |
|---|---|---|---|---|---|
| **SoloPlay** | https://github.com/pengcong2020520/soloplay | 导入 PDF/DOCX/TXT/MD，DM Agent、阶段、线索、投票、TTS、复盘 | 目前面向**1 真人 + AI 角色**；剧本解析有截断和补写事实风险；缺多人会话与玩家级权限 | 仓库未发现 `LICENSE`，元数据无许可证 | **功能与玩法最贴近；获得授权才推荐直接改造** |
| **Blood-on-the-Clocktower-auto-dm** | https://github.com/EthanQC/Blood-on-the-Clocktower-auto-dm | 多人房间、玩家手机、WebSocket、身份隔离、规则裁决、Agent 主持和整局测试 | 《血染钟楼》专用昼夜/提名/技能/处决系统，需重建传统推理本流程及导入 | **MIT，已检查 `LICENSE`** | **许可安全备选基座；重点借鉴会话与权限** |
| **Open Dungeon Master** | https://github.com/Lebbitheplow/open-dungeon-master | 多人、局域网、TTS、私密信息、邀请、PWA、API/本地模型、Claude Code/Codex CLI 适配 | 基于 D&D 5e，大量战斗与桌面 RPG 特定业务耦合；需重做固定剧本主持 | **MIT，已检查 `LICENSE`** | 模型适配和语音参考，不宜整个项目硬改 |
| **4-bits** | https://github.com/codinggita/4-bits | 多人房间码、Socket.IO、证据板、角色档案、投票、LiveKit | 以动态生成案件为核心；部分接口会暴露凶手字段，缺少足够的玩家级敏感信息防护 | 仓库未发现 `LICENSE`，元数据无许可证 | **只参考 UI/交互思路，不直接作为基座** |
| **jubensha-ai** | https://github.com/JianWang97/jubensha-ai | 多 Agent、AI 角色演绎、TTS、剧本与图像生成 | 与“真人玩家 + AI 仅担任 DM”偏离 | **MIT** | 非首选 |

附加参考：
- https://github.com/leo-lab-2026/dsh-xgame —— DSH 插件，确定性游戏内核与 AI GM 分离；单人推理/文字冒险为主，适合参考“LLM 不直接决定事实”的设计。`package.json` 声明 MIT，但公开仓库未找到根 LICENSE；需单独确认授权。
- https://github.com/yuuu956/mysteryhost-ai-lite —— AI 主持产品原型，有规则、状态、提示与 DeepSeek 接口；线上静态演示和真实本地后端不是同一能力水平。更适合产品交互参考。
- https://github.com/skeinkeeper/skeinkeeper —— Discord/Foundry 语音 AI DM，标注 Pre-MVP Alpha，适合参考语音系统但不适合传统推理本基座。
- https://github.com/MilkyBoat/BoTC-AI —— 《血染钟楼》命令行 AI 说书人；许可证属于非商业限制，需单独核查。

### 2.2 已核查的关键源码证据

**SoloPlay 的主持业务确实已拆成模块：**

- [`lib/agents/dm-agent.ts`](https://github.com/pengcong2020520/soloplay/blob/main/lib/agents/dm-agent.ts)：`PHASE_ANNOUNCE`、`HINT`、`RECAP`、`GUIDE`、`CLUE_DESCRIPTION`、`VOTE_SUMMARY`、`REVEAL` 等主持行为；未配模型时会走部分硬编码的 Mock 回复。
- [`lib/game/phase-director.ts`](https://github.com/pengcong2020520/soloplay/blob/main/lib/game/phase-director.ts)：结合发言、举证、阶段结论等判断是否推进；当前包含 AI NPC 参与条件，应改为纯真人。
- [`lib/game/clue-director.ts`](https://github.com/pengcong2020520/soloplay/blob/main/lib/game/clue-director.ts)、`lib/game/vote-engine.ts`：线索/投票模块。
- [`app/api/game/[id]/next-phase/route.ts`](https://github.com/pengcong2020520/soloplay/blob/main/app/api/game/%5Bid%5D/next-phase/route.ts)：使用数据库事务和比较更新，阶段推进与线索发放可避免部分重复及半状态。
- [`app/api/script/upload/route.ts`](https://github.com/pengcong2020520/soloplay/blob/main/app/api/script/upload/route.ts)：支持 TXT、MD、DOCX、PDF 上传、文本提取和结构化处理。
- **高风险：**[`lib/parsers/script-extractor.ts`](https://github.com/pengcong2020520/soloplay/blob/main/lib/parsers/script-extractor.ts) 内有 `rawText.slice(0, 40000)`，且提示词指示“缺失字段合理补全”“未明示凶手时推断一个最可能者”。对固定推理本必须**禁止**此行为。
- [`prisma/schema.prisma`](https://github.com/pengcong2020520/soloplay/blob/main/prisma/schema.prisma) 和 [`lib/game/session.ts`](https://github.com/pengcong2020520/soloplay/blob/main/lib/game/session.ts)：`GameSession` 当前归属单个用户，剩余角色可分配 AI；改多真人时需重新设计玩家成员、座位/角色映射及可见性校验。
- [`app/api/game/[id]/state/route.ts`](https://github.com/pengcong2020520/soloplay/blob/main/app/api/game/%5Bid%5D/state/route.ts)：单用户模式下返回玩家个人剧本/已发布线索；改造时确保服务端按请求者真实成员身份投影，而非前端隐藏秘密。

**Auto DM 的多人安全设计：**

- [`backend/internal/projection/projection.go`](https://github.com/EthanQC/Blood-on-the-Clocktower-auto-dm/blob/main/backend/internal/projection/projection.go)：事件按观看者权限过滤、敏感状态投影；适合参考“角色身份不可由客户端伪造”。
- [`backend/internal/agent/core/orchestrator.go`](https://github.com/EthanQC/Blood-on-the-Clocktower-auto-dm/blob/main/backend/internal/agent/core/orchestrator.go)：主持、旁白、规则、记忆等子 Agent 协调。
- 仓库 `progress.md` 记录了房间、入夜、提名、投票流程的浏览器回归和多局自动对局测试；**这些不是独立的 4–6 人传统推理本真人试玩验证**。
- 官方 README 的部署依赖较多：Go + Vue + MySQL/Redis/RabbitMQ/Qdrant 等，MVP 可考虑简化，但不能擅自删除原项目必要服务。

**4-bits 的安全/设计风险：**

- [`backend/src/gameEngine/index.js`](https://github.com/codinggita/4-bits/blob/main/backend/src/gameEngine/index.js)：启动时生成故事/凶手/证据，不是导入已有固定剧本。
- [`backend/src/services/game.service.js`](https://github.com/codinggita/4-bits/blob/main/backend/src/services/game.service.js)：`startGame` 返回的会话摘要中包含 `murderer`；在未重新设计访问层前不可让普通玩家获取。
- [`backend/src/routes/game.routes.js`](https://github.com/codinggita/4-bits/blob/main/backend/src/routes/game.routes.js) 和 `controllers`：若仅凭房间码/`playerId` 读信息，存在伪造身份或越权风险，需要服务端认证/授权与负面测试。

**Open Dungeon Master 的技术亮点：**

- [`src/lib/harness/adapters/codex.ts`](https://github.com/Lebbitheplow/open-dungeon-master/blob/main/src/lib/harness/adapters/codex.ts)：通过 `codex app-server` 管理 Codex CLI，对模型运行沙箱、安全权限做控制；不等于任意用户的 ChatGPT 订阅能自动转换成 API 调用。
- `src/lib/harness/adapters/claude.ts`、`src/lib/tts.ts`、`src/app/api/campaigns/[campaignId]/invite/route.ts`：其他适配器、语音、邀请码设计参考。
- README 提供 `npm run start:lan` 局域网启动方式；项目仍以 D&D 而非传统剧本杀为核心。

### 2.3 选型结论（带门槛）

**路线 A——业务最贴近（有授权才启用）：** 使用 SoloPlay 作为主干，**保留**剧本/阶段/DM/线索/投票/TTS 的设计和实现，**改造**导入保真、真人多人、玩家级数据隔离、手机入口、同步、全员协商推进；**移除或绕开** AI NPC 自动扮演、自由剧本生成主流程。

**路线 B——许可证明确（授权不可得时优先）：** 使用 MIT 的 Auto DM 多人会话及状态可见性框架作基座，把《血染钟楼》规则替换/抽象成独立的传统推理本流程；剧本解析、线索和固定真相模块需要重新开发。可参考 SoloPlay 产品思路，但**不得未获授权直接复制其源码**。

**路线 C——自建更精简 MVP：** 若现有仓库过于耦合，采用 Next.js / TypeScript + SQLite/Prisma（或其他经审计的精简组合）实现基础多人主持，不为“尽量复用”而让 6 人局域网项目承受整个 D&D/血染钟楼体系。

**不建议：** 把多个大型项目直接拼接或让多套数据库/会话系统并行；以 4-bits 为主要安全基座；在现有固定剧本上启用任何随机剧情生成逻辑。

---

## 3. 建议架构与边界

```text
已有剧本材料（DM手册 / 角色本 / 线索 / 真相）
                   |
           文本提取与分段处理
                   |
         来源映射 + 结构化审核
                   |
             Canonical Script
       (不可被运行时模型任意改写)
                   |
      ┌────────────┴────────────┐
      |                         |
  状态/规则引擎              AI DM 适配层
  ·阶段状态机                ·主持播报
  ·权限与信息投影            ·语音说明当前环节与规则
  ·确定性线索释放            ·响应共同按键呼叫
  ·票数统计与原本结算        ·引用原本复盘内容
      |                         |
      └────────────┬────────────┘
                   |
               游戏事件总线
       ┌───────────┴───────────┐
       |                       |
   电脑公共主持与控制端    iPad/平板玩家端 ×4–6
   ·二维码/房间状态        ·本人角色本
   ·当前环节/下一步        ·本人私密线索（文字）
   ·按键呼叫/语音播报      ·个人搜证操作
   ·公共线索/复盘/重播     ·就绪确认/投票
```

### 3.1 标准数据对象（建议，不是现有仓库既定 Schema）

- `Script`: `script_id`, `title`, `version`, `player_count`, `source_manifest`, `import_status`。
- `SourceSpan`: `file_id`, `page_or_section`, `start/end_offset`, `hash`，支持关键字段原文回溯。
- `Role`: `role_id`, `name`, `public_profile`, `private_story`, `secrets`, `goals`, `visibility`。
- `Phase`: `phase_id`, `order`, `intro`, `prerequisites`, `completion_rule`, `clue_release_ids`, `allowed_actions`。
- `Clue`: `clue_id`, `original_text`, `release_condition`, `audience (PUBLIC | ROLE | GROUP)`, `source_span`。
- `GroundTruth`: `truth`, `murderer`, `timeline`, `solution`, `source_span`；服务器侧保存，不进入玩家普通 API。
- `Room`: `room_code`, `script_version`, `status`, `phase`, `revision`, `player_slots`。
- `RoomMember`: 服务器端生成且校验的成员身份、角色绑定、身份令牌；客户端传来的 `playerId` 不可信。
- `GameEvent`: 唯一 ID、类型、操作者、时间、阶段、修订号、可见性、幂等键；可按需回放/恢复。
- `Vote`, `ClueRelease`, `PhaseReady`, `DMRequest`: 各自独立记录，可审核，禁止重复提交造成重复推进。

### 3.2 剧本导入：非协商性约束

1. **所有已提供文件完整读入并分段处理**；保留原文，不得用固定 40,000 字符截断。PDF/DOCX 提取失败应显示失败，不可悄悄用内置样例替换。
2. 所有关键事实、角色秘密、线索、主持规则、阶段条件、结局必须附 `source_span`；模型不得“合理补全”人物、凶手、作案方式、线索。
3. 处理相互矛盾或缺失信息时输出 `UNKNOWN / NEEDS_REVIEW`。严重缺漏不能开局；校验报告给所有玩家查看时必须**脱敏**，不可透出答案。
4. 将原始文本、原始文件指纹、结构化版本、解析模型及版本分开保存。任何修改记录差异；运行时冻结所选剧本版本。
5. 必须支持标准文本 PDF、扫描 PDF 和图片版完整剧本，执行 OCR、来源映射及缺漏校验；DOCX/MD 可作为补充输入。OCR 失败或识别不可靠时明确阻止开局，允许必要的无剧透整理，不可假装成功。
6. 导入与准备流程不应要求其中一名真人玩家预览真相。对于无法自动判断的难点，先阻止开局，并提供**独立于本局玩家的授权校验方式**或选用结构清晰的剧本。

### 3.3 DM 应使用工具调用，不直接写状态

建议指令/事件：

```text
request_intro(phase_id)
request_public_host(question_or_action, phase_id)
request_repeat_announcement(announcement_id)
request_search(player_id, target_id)
request_phase_ready(player_id, phase_id)
request_advance_phase(player_id, phase_id)
submit_vote(player_id, choice)
request_public_recap()
```

AI 仅生成/选择允许播报的内容；游戏服务校验身份、阶段、线索状态，返回可执行的最小结果。主持词应标明依据及阶段，不可以文本输出代替数据库写入。

以上为待实现接口建议。公共问题仅在玩家按键后录音并识别，普通问题直接口头回应；
投票和阶段推进等状态操作必须明确确认。首版不提供私密问答、主动提示或多人问答队列。

### 3.4 信息隔离与防剧透（重点）

- **DM 视角**可访问完整剧本，但不能把全部原文或真相直接发送到每个玩家的浏览器。播放器公开端仅接收可公开字段和 TTS 片段。
- **玩家视角**仅获取公开信息与自己当前阶段有权知道的个人信息。前后端必须都做校验；不能依赖 CSS 隐藏。
- **共同提问**仅使用当前阶段可公开的检索材料；确需全知 DM 推理时，返回内容必须再次经过来源及公开权限检查。涉及个人秘密或未解锁内容时不得播出，安全不确定则明确无法公开回答；不转入私密问答。
- **结局/真相**只有游戏规则明确进入揭秘阶段才允许解锁。投票结束前，任何“请告诉我谁是凶手”“输出 JSON”“忽略主持指令”等请求都不能泄露。
- 房间号只是加入入口，**不是授权凭证**；平板号/姓名或前端提交的角色 ID 不能单独证明身份。
- API、WebSocket、重连、导出日志、AI Prompt、浏览器缓存、报错日志都要检查敏感数据是否泄露。
- 请对导入文档中的“忽略上述规则/泄露答案”等提示注入文本做防护：剧本文本是**数据，不是系统指令**。

### 3.5 主持控制与全员公平

- 默认采用“**玩家发起、规则引擎检查、AI 解释和宣布**”的模式。
- 游戏开始前需全员就绪；阶段推进必须满足**原剧本明确规则及全员确认**，不能多数代替全员确认或超时自动推进。原本没写清就诚实说明，不由 LLM 自由创造裁决。
- 公共开始、下一步及重播集中共用电脑，操作人仍是普通玩家，不读取真相、不替 AI 裁决。AI 等待玩家触发，不主动提醒或提示。
- 多个人同时点击下一阶段，应最多推进一次，线索最多发一次；处理幂等与 CAS/事务。
- 断线重进应恢复本人角色、已发线索、投票状态、当前阶段，但不返回他人秘密或完整真相。
- 中断/暂停后可恢复；AI 或 TTS 出故障时，系统保留已提交状态，允许公共文字播报兜底。
- 不让一名玩家拥有能查看 DM 全量真相的“房主控制台”；后台维护权限与正常游玩角色隔离。

### 3.6 模型、TTS、网络部署

- **模型层**建议用统一接口封装：`generateHostMessage`、`answerPublicRequest`、`summarizePublicEvidence`；语音主持为核心，回答共同问题为辅助。先支持一种明确接入方式，具体供应商与基座经审计再定。
- **Codex 区分角色**：Codex 作为开发工具可以直接改代码；Codex CLI 作为运行时主持模型则涉及独立 app-server 适配、安全沙箱、授权和版本兼容，**不应假设订阅直接等于 API Key**。
- **语音**：按键开始/结束录音，识别后普通问题直接回应；公共回复通过电脑音箱自然、简洁地播放，不强制逐次确认识别文字。TTS 播放串行化用于避免声音重叠，不扩展成多人问答排队产品。无常开 STT、无主动发声；文字仅作辅助或故障降级，个人秘密始终用本人平板文字呈现。
- **局域网**：服务监听适当的 LAN 接口，PC 显示二维码；平板与 PC 同网。不得把私密接口无保护暴露到公网。公开语音不包含私密线索内容。
- **开发优先**：Windows 11 + Node/本地服务条件下能运行；具体环境以实际项目依赖为准，不先引入不必要的中间件。

---

## 4. 五阶段实施计划：每阶段均需 Smoke Test

> **说明：** 数字为一名熟悉全栈开发者配合 AI coding 工具的**估算人力工时**，不是“Codex 自动运行所需时间”。假定能获得合法可用的源码，并先适配 1 个结构相对清晰的剧本。复杂排版、授权等待、特殊玩法、模型/语音服务故障将增加工作量。

| 阶段 | 核心交付 | 估计 | 本阶段必须通过的验收 |
|---|---|---:|---|
| **1. 剧本导入保真** | PDF/图片多文件提取与 OCR、Canonical Script、来源映射、缺漏/冲突报告、禁止编造 | **待重新估算** | 角色数、阶段数、线索数与原资料一致；关键事实逐项追溯；遗漏/冲突拦截开局；报告不剧透 |
| **2. 多人房间与私密平板端** | 二维码/房间码加入、成员令牌、真人角色绑定、玩家级 API 投影、断线恢复 | **22–40 h** | 6 设备模拟/真实入场；每人仅看自己的材料；伪造 ID/重连/越权请求均不能读他人秘密 |
| **3. 玩法规则与触发主持** | 阶段机、搜证请求、条件判断、线索发放、投票、公开消息事件 | **16–28 h** | 无跳阶段/重复线索；并发点击只推进一次；投票唯一；结局锁在正确阶段 |
| **4. 按键语音主持与电脑控场** | 按键录音/STT、模型适配、当前流程上下文、电脑 TTS、公共控制、文字降级 | **待重新估算** | 按键呼叫后口头带流程；普通问题直接回应；状态操作明确确认；无主动发声；模型不可越权改状态；故障不破坏已提交状态 |
| **5. 整局 E2E / 体验与回归** | 自动化负面测试、局域网 4–6 人整局试玩、修复、开发/运行文档 | **16–30 h** | 从导入到复盘完成整局；6 端同步；异常处理可恢复；无任何未授权剧透 |
| **合计** | 单本可玩的语音主持 MVP | **待重新估算** | **旧估算未完整覆盖本轮确认的按键语音与图片 OCR 范围，不作为当前交付承诺** |

### 每阶段固定交付格式

```text
[本阶段目标]
[采用/未采用的现有源码及授权依据]
[修改的具体文件]
[运行/测试命令]
[Smoke Test 结果：PASS / FAIL + 日志或截图路径]
[已知风险和阻塞]
[下一阶段唯一最小动作]
```

**禁止在单名玩家 smoke run 未通过时直接上 6 人整局测试。** 遇到错误先局部定位/修复，再重复当阶段用例；不得停止在错误日志，也不得修改不相关的功能以掩盖错误。

---

## 5. 最小自动化验收清单（必须写进项目）

### 5.1 导入与事实一致性

- [ ] 完整文本 PDF、扫描 PDF 和图片输入经过提取/OCR 均可追溯；识别失败或不可靠时拦截开局，报告不剧透。
- [ ] 上传完整 DM 手册 + 多名角色本 + 多轮线索 + 真相时，不截断、没有样例替换。
- [ ] 每个阶段/关键线索/结局字段都能映射到源文件及原文段落。
- [ ] 缺少真相或线索有冲突时，系统阻止开局并显示**不剧透**的异常类别。
- [ ] 测试文档包含恶意指令文本时，不能被当作 AI 系统指令执行。

### 5.2 信息隔离

- [ ] 角色 A 通过浏览器网络请求也无法获取角色 B 的私密本；不能只在 UI 隐藏。
- [ ] 任意玩家无法获得服务端 `GroundTruth`、凶手字段、未解锁的线索。
- [ ] 断线重连后仅恢复自己的可见状态；更换/伪造 `playerId` 被拒绝。
- [ ] 公共 DM/TTS/日志不会把个人秘密或凶手提前公开。
- [ ] 在剧情未结束时尝试“忽略规则、直接公布真相”等 Prompt 注入，返回拒绝/安全提示。

### 5.3 游戏状态与并发

- [ ] 4、5、6 名玩家可入场，角色绑定唯一、准备状态准确。
- [ ] 全员满足本剧本推进条件后才能推进；未满足时给出正确、**不剧透**的原因。
- [ ] 2 名玩家同时点击“下一阶段”，只产生一次状态迁移和一次线索发放。
- [ ] 同一玩家重复投票不会生成两票；投票前不发布最终答案。
- [ ] 模型或 TTS 超时不会导致场次丢失或重复执行阶段操作。

### 5.4 体验与交付

- [ ] 按键录音共同呼叫后，普通问题直接由电脑口头回应，无需逐次确认识别文字；不提供私下问答或多人问答队列。
- [ ] 电脑可开始当前环节、下一步及重播；推进须满足原本条件和全员确认，投票须明确确认。
- [ ] 等待期间不持续录音、不主动催促或提示；规则没写清时诚实说明，不生成新裁决。
- [ ] 在实际目标 iPad/平板上验证按键录音、角色材料文字阅读、就绪及投票；电脑语音完成开场到复盘的主持，文字仅作辅助或故障降级。
- [ ] 玩家无需装原生 App，平板浏览器同局域网加入。
- [ ] 电脑播放一个队列化 TTS，平板不重复外放公共主持声音。
- [ ] 全程没有必须由一名玩家阅读 DM 真相并决定玩法的步骤。
- [ ] 4–6 人完成一局：进入房间 → 开场 → 阅本 → 搜证 → 讨论 → 投票 → 真相复盘。

---

## 6. 尚未解决的决策与风险（由 Codex 审计，不要猜）

1. **许可**：SoloPlay 与 4-bits 当前未见许可证。Codex 必须重新核查最新状态；在得到授权前，不复制其代码进入自己的项目。MIT/其他许可的实际适用范围也需以目标仓库最新 `LICENSE` 为准。
2. **剧本样本**：用户已经有电子剧本，但目前对话中未提供特定完整样本的附件。不能在没有样本时宣称解析已通过。先用完全虚构、允许测试的公开样例建立流水线，拿到用户合法提供的真实电子文件后再做保真验收。
3. **材料版权与隐私**：已有剧本只供参与者在授权范围内使用；原文、角色秘密、音频、日志不得默认上传公开仓库或外部服务。远程 API 处理原始材料前需确认用户是否允许外传。
4. **规则差异**：不同推理本的搜证条件、回合数、私密线索规则与终局判定可能完全不同。首版以一个样本实现参数化阶段规则，不把“线索第 3 阶段发布”等旧项目常量写死。
5. **部署环境**：用户有 Windows 电脑及平板，具体 Node/Python/Docker 版本、设备访问网络情况尚未在新项目中核查。先环境探测再给安装命令。
6. **语音输入范围**：用户已确认按键控制录音、公共口头交流，不持续监听。必须验证实际目标设备的麦克风访问、局域网录音与电脑播放；不能把按键 STT 误删为“不需要语音输入”，也不能扩展为全程音频监控。
7. **主机可信边界**：共用电脑若部署后台和保存真相，玩家能否本机访问开发者工具/数据库属于物理安全模型。MVP 应至少分离普通大屏 UI 与后台管理凭据，并明确“共用主机管理员访问本地文件不在普通玩家浏览器权限模型内”。

---

## 7. 建议 Codex 执行的首轮审计任务（直接复制到 Codex）

> 你现在接手一个“4–6 名真人线下推理本、AI 无人主持”的项目。本文件 `AI_DM_Research_and_Implementation_Plan.md` 是唯一已确认需求基线。请先阅读完整文件，**不要重新询问已确认需求；以第 1 节的 grilling 复核为准**。先检查当前目录与 `git status --short`，保护所有未提交修改，然后开展**仅审计、不大改代码**的 Phase 0：
>
> 1. 确认本目录是否已有应用源码；若没有，报告适合初始化的最小基座。核查 SoloPlay 最新许可证，明确能否复用；不明则按“无授权，不复制代码”处理。
> 2. 对比 SoloPlay（业务逻辑）与 MIT Auto DM（多人信息隔离），给出选型结论和证据（具体仓库路径/函数），不要仅依据 README。
> 3. 确认目前项目是否存在：剧本完整导入、按角色权限投影、玩家同步、阶段原子推进、TTS 等模块，并用“已有 / 可改造 / 缺失 / 风险”矩阵输出。
> 4. 给出 **Phase 1：单剧本保真导入 Smoke Test** 的最小文件清单、命令、预期输出和自动化测试。没有真实剧本样本时，创建一个**标注为合成测试数据**的多文件虚构案件，不能声称验证真实剧本成功。
> 5. 只在依赖、许可证、测试范围明确后才实施 Phase 1。每次改动后运行对应 lint/typecheck/test/smoke，不要跳过失败测试，不要强推 Git，不要泄露剧本真相。
>
> 开发终端优先考虑 Windows PowerShell；启动命令应与实际所选仓库匹配，不要照抄其他项目 README 的占位符。最终按“实际检查结果、风险、修改文件、测试命令、PASS/FAIL、下一步”报告。

---

## 8. 参考来源（可复核）

### 优先仓库

1. SoloPlay — https://github.com/pengcong2020520/soloplay
2. Blood-on-the-Clocktower-auto-dm — https://github.com/EthanQC/Blood-on-the-Clocktower-auto-dm
3. Open Dungeon Master — https://github.com/Lebbitheplow/open-dungeon-master
4. 4-bits — https://github.com/codinggita/4-bits
5. jubensha-ai — https://github.com/JianWang97/jubensha-ai

### 补充参考

6. dsh-xgame — https://github.com/leo-lab-2026/dsh-xgame
7. MysteryHost AI Lite — https://github.com/yuuu956/mysteryhost-ai-lite
8. Skeinkeeper — https://github.com/skeinkeeper/skeinkeeper
9. BoTC-AI — https://github.com/MilkyBoat/BoTC-AI

---

## 9. 当前项目进度（务必在后续交接时更新）

- **需求确认：2026-10-19 grilling 复核总结已由用户明确确认，当前以第 1 节为准。**
- **公开项目初步调研与部分源码核查：完成。**
- **最终源码授权：尚未确定 SoloPlay 是否允许二次开发。**
- **本地项目初始化：spec-kit Codex 集成已安装，10 个技能文件已验证；宪章已按确认需求更新至 2.0.0。应用源码尚未实现。**
- **真实剧本导入/6 人平板整局测试：未执行。**
- **最优先下一步：Phase 0 审计选型及授权，随后 Phase 1 导入保真 Smoke Test。**
