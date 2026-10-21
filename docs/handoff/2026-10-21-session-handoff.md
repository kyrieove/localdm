# 会话交接：AI 主持改造与导入向导

日期 2026-10-21。写给接手这个项目的下一次对话（本地 Claude Code 或其他助手）。
读完这一份，就能接着上一次云端对话做下去，不用重新问一遍需求。

本文件**不含任何剧本正文、卡面或真相**。仓库里的文档都遵守这条规矩。

---

## 0. 接手时先做的三件事

1. `git pull origin main`，确认 `main` 至少到 `a45a55c`。
2. 读这一份，再按需读第 6 节列出的方案与证据文件。
3. 看第 5 节"未完成事项"，从第一项接着做。

---

## 1. 项目是什么

LocalDm（本局主持）：Windows 单电脑剧本杀 AI 主持软件。.NET 10 + WPF。

- 解决方案：`LocalDm.slnx`
- 代码：`src/LocalDm.Core`（领域、主持、导入核心）、`src/LocalDm.Infrastructure`（MinerU、存储、模型网关、TTS）、`src/LocalDm.App`（WPF 界面）
- 测试：`tests/LocalDm.Tests`（单元测试，Windows）、`tests/LocalDm.UiTests`（界面测试，Windows）
- 命令行探针：`tools/LocalDm.Probe`
- 模型：通过本机 **Windows Magpie**（OpenAI 兼容接口，`http://127.0.0.1:3425/`）调用
- 宪章：`.specify/memory/constitution.md`，当前 4.1.0（导入向导那次升的）
- 用户本机仓库路径：`D:\2\dev\dm`

---

## 2. 用户已确认的需求与产品决定

这些是一条条问过用户、用户确认过的，**不要重新讨论**，除非用户自己提出要改。

### 2.1 AI 主持的定位

- 像真人 DM 一样主持**任何剧本**，不为某一本剧本写专属规则或专属提示词措辞。
- 现场形态：
  - 每人拿纸质角色本或 PDF，线索卡是桌上的实体卡。
  - **只有一个人操作软件**，AI 说的话全桌都能看到、听到，**没有私聊**。
  - 玩家说"去调查某地"时，AI **只告诉翻哪张或哪组卡，不念卡面**。
  - "说出关键词就翻回忆"这类规则由**玩家自己留意**，AI 不检测、不提醒。
- 收尾：AI 讲真相、对照指认、主持复盘。
- 普通聊天正常回应，不限句数，不机械复述规则。
- 被问到模型名称时，按软件配置的 model ID 如实回答。

### 2.2 AI 读什么

| 资料 | 给不给 | 时机 |
|---|---|---|
| 组织者手册（含真相、主持人专用页） | 给 | 开局就全部给，像真人 DM 提前读完 |
| 线索卡（正面和卡背） | 给 | 开局就给 |
| 角色本 | **不给** | 任何时候都不给 |
| 本局对话历史 | 给 | 按上下文窗口计量，快满时自动压缩成备忘，也可手动压缩 |

角色本的判定：私有段，且来源 span 属于某个 `RoleIndex`、不属于任何 `Material`。按剧本包里的事实判断，不按目录名猜（`HostNarration.IsRoleBookSegment`）。

### 2.3 软件只做通用的事

- 不做调查次数、钥匙、计时这类剧本专属状态，交给 AI 读手册、看对话自己掌握。
- 软件负责：流程环节（`ScriptFlow`）、推进与揭秘需要人点按钮确认、对话记忆与压缩。

### 2.4 上下文管理（像 agent 一样）

- 不设固定字数上限。按模型上下文窗口计量（设置页"模型上下文长度"），读取真实 `usage`。
- 占用到 80% 先压缩：较早的对话整理成"本局备忘"，保留最近几轮原话；也可以点"压缩上下文"或整句输入 `/compact`。
- 压缩失败不丢内容；只有发不出去时才丢最早的轮次，并在聊天区说明丢了几轮。
- 记忆存进 `dm_conversations` 表，重启后能接上，不重播旧对话。
- 备忘正文**不上公共屏幕**（操作者也是玩家），界面只显示"已整理 N 次"。

### 2.5 剧本库导入向导

- 在软件里导入新剧本，不再需要命令行，也不再手写 `script-flow.json`。
- 材料分类：软件先猜（组织者手册 / 角色本 / 线索卡），人确认。
- 流程：AI 读组织者手册起草，人确认，可以改、增、删、排序。
- **导入的人可能就是玩家，所以导入全程不显示正文、卡面、真相、缩略图。**
- AI 起草时只标真相页；不标公开页，也不标"不入包"页，手册每一页都给 AI 主持。
- PDF、图片，以及两者混合的剧本都要能用。

### 2.6 不做的事

- 不做私聊。
- 不做关键词检测和回忆提醒。
- 不审查 AI 主持的回复（产品决定）。秘密隔离只在上下文构造处做；唯一的输出检查是 `DmMemoCheck`，它只针对备忘。
- 整本剧本只有一份 PDF（手册、角色本、线索卡全在一个文件里）的情况，暂不支持。

---

## 3. 工作方式

- **分工**：云端助手（就是上一次对话）读代码、写方案和给 DeepSeek 的提示词、审查提交；用户把提示词发给本机的 **DeepSeek 编码助手**，由它在 Windows 上实现、构建、跑测试、推送 `main`。云端没有 .NET 环境，编译不了，也跑不了测试。
- **分支**：云端助手只推 `claude/busy-clarke-ozj3n9`，再由 DeepSeek 合进 `main`。本地对话可以直接在 `main` 上工作，但要遵守下面这些规矩。
- **给 DeepSeek 的提示词里一直有这些规矩**：
  - 每一步 `dotnet build LocalDm.slnx`、`dotnet test tests/LocalDm.Tests`、`dotnet test tests/LocalDm.UiTests` 全部通过后才提交，提交信息用中文。
  - **不能删除或跳过测试来让测试通过。** 唯一例外：`EdgeTtsTests` 里 6 条会真的出声的语音用例，用户授权在本机有中文系统音色时跳过。
  - 只改方案里列出的内容；方案和代码对不上时停下来问，不要自己发挥。
  - 方案里写明"用原文"的提示词措辞不要改写。
  - 不读取、不打印、不提交 `stories/`、`.appdata/`、`.local/` 下的文件（剧本原文、剧本包、验收对话记录）。验收和评审需要时，允许读来做判断，但不写进仓库。
  - 汇报 K7 这类用户还没玩过的剧本时，不出现任何剧情，只报文件夹名、页数、页号、环节标题。
- **评审标准**（验收时用）：
  - 守真相要严：被追问时只要有可能让玩家推出答案的暗示，就判"不合格"或"存疑"。
  - 但下面这些**不算**剧透：玩家说查过哪里就按查过处理；列出地图上的公开地点；讲手册里讲给玩家听的规则（例如"进某房间要先找到某把钥匙"）。
  - 不要对着验收题逐条调提示词，那是过拟合。新问题要用"未调参题"另测。

---

## 4. 已完成的工作（时间顺序）

| 内容 | 关键提交 | 说明 |
|---|---|---|
| AI 主持改造方案 | `660eb6a`、`85acf70` | `docs/plans/2026-10-20-ai-dm-organizer-context.md` |
| 方案实现（去角色本、提示词、动态状态移出 system、token 用量、上下文窗口设置、`DmConversation`、压缩、存档、整句分流、分层按流程声明、宪章 4.0.0） | `540d1d3`…`62e6258` | DeepSeek 实现 |
| 压缩保留起点与手动压缩的两个 bug | `4cab600`、`614fa4f` | 起点只能往更早的方向挪到 user；没东西可压时不发请求 |
| 人工验收 13 条与未调参题；提示词去掉剧本专属名词；`DmMemoCheck`；备忘不上屏 | `530a595`…`cbd4638` | `docs/evidence/us3/ai-dm-acceptance.md` |
| 输入法拼音和占位提示重叠；界面缩放（Ctrl +/-/0、Ctrl+滚轮）与对话宽度三档；验收记录按运行分目录 | `14b9dd4`、`0b1931f`、`737ad1d` | |
| 导入向导方案 | `de0d820` | `docs/plans/2026-10-21-script-import-wizard.md` |
| 导入向导实现（扫描与猜类别、导入服务、`FlowDrafter`、`FlowSpoilerCheck`、五步界面、文档、宪章 4.1.0） | `a8d777d`…`5df6770` | |
| 起草提示词没写 JSON 结构 → 模型自拟字段名、解析不出 `steps` | `4b9c756` | 云端助手修的 |
| 起草不再标"不入包"页 | `4155153` | |
| PDF 多页被压成一页（拆页、页数、进度、建包页号、文件类型统一） | `e5413ca` | |
| 向导环节 ID 用了随机哈希、确认页丢字段 | `2975080` | 改为以原环节为底，只覆盖可编辑字段 |
| 第二本剧本 K7-星火号验收通过 | `bec00f1`、`830f380` | `docs/evidence/us3/import-wizard-acceptance.md` |
| 模型对比报告 | `d0e1c5c` | `docs/evidence/us3/model-comparison.md`，见第 5 节第 2 项 |
| 向导第一步输入框字被裁、选完文件夹后"下一步"不亮 | `a45a55c` | 云端助手修的，已合进 `main` 并测试通过 |
| 按钮自动刷新与手动通知补齐 | `ec8a3be` | |
| 默认模型不退回 `group/auto…` 路由 | `2520c1f` | |

测试数（`2520c1f` 时）：`LocalDm.Tests` 287 通过 / 0 失败 / 6 跳过；`LocalDm.UiTests` 33 通过 / 0 失败。

---

## 5. 未完成事项（按优先级）

### 5.1 按钮"该亮不亮"——已完成

`ec8a3be`：`RelayCommand` 同时挂接 `CommandManager.RequerySuggested`，并补齐设置页、聊天消息、导入向导各处的手动通知，新增 3 条界面测试。
`2520c1f`：默认模型不再退回会丢 system 消息的 `group/auto…` 路由。
还没做的只有人工确认：用户从界面导入 K7 再走一遍，看第 ④ 页重写提示后"下一步"能不能点。

### 5.2 选主持模型

- `docs/evidence/us3/model-comparison.md` 对比了 `codex/gpt-6.1-sol` 和 `workbuddy-ai/deepseek-v4.1-flash`。
- **这份对比不公平**：DeepSeek 那一列没有重跑，用的是旧记录，数字是估算值，当时的提示词版本也不同。DeepSeek 的几条"不合格"按第 3 节的评审标准其实不算剧透。
- 6.1sol 的优点：不调参就能守住真相，揭秘讲解完整，system 消息稳定送达，没有思考内容泄漏。缺点：缓存命中为 0（一轮验收用了约 177 万 prompt token，费用要查），平均 7.5 秒，揭秘大段讲解最长 75 秒。
- 用户还没定用哪个。如果要公平重跑，提示词是：

```
model-comparison.md 里 DeepSeek 那一列用的是旧记录，不是这次重跑的结果，和我的要求不符。请：
1. 用当前代码、同一份题目，把 workbuddy-ai/deepseek-v4.1-flash 重新跑一遍（新局、新存档副本），碰到 429 就等一会儿重试。等待秒数和 token 数都要实测值，不要写"约"。
2. 评判标准两边统一：玩家说查过哪里就按查过处理，列出地图上的公开地点、讲手册里的钥匙规则，都不算剧透；6.1sol 的第 10a 条按同一标准重新评。
3. 两个模型都补一项"估算费用"：按 Magpie 或模型提供方公布的单价，算出这一轮验收的费用（含缓存折扣）；查不到单价就写"未知"。
4. 更新 model-comparison.md，提交信息用中文，推送 origin main，把两张新表和建议告诉我。
```

- **换模型时在设置页里改 model ID**，不要用 DeepSeek 给的那条正则改 `settings.json` 的命令。上下文长度按所选模型的实际上限填。用户说过有 1M 上下文的模型可用，云端助手建议先填 200000 试玩。

### 5.3 只有用户能做的

1. 从界面导入一本剧本，完整走完五步，看有没有哪里露出剧情、哪里看不懂（之前的验收都是程序跑的）。
2. 真玩一局（K7 或《孽岛疑云》）。
3. 亲耳听一下 Edge-TTS 和 Windows 本地语音两条朗读路径，`docs/project-state.md` 里这项还标着"未由人耳确认"。

### 5.4 小问题（不急）

- 只聊两三轮就打 `/compact` 时，提示写的是"最近 2 轮之前的对话都整理过了"，但其实从没整理过。应改成"对话还太短，暂时没有需要整理的内容"。
- `RelayCommand.Execute` 执行中不会禁用自己，连点可能重复执行。
- 软件刚打开、没装载剧本就点"继续一局"时会退回演示剧本，这条路径下对话记忆不会存档（只影响演示剧本）。
- 《孽岛疑云》"回忆舒"那组有 1 页没识别出文字，可能是纯图片卡。
- K7 起草时 AI 多标了一页真相页（第 5 页是警告页），方向是安全的；剧透检查命中的那一条，多半是和警告页重合造成的误报。
- K7 起草出的流程比手册粗（三轮搜证 + 公聊并成一环），用户导入时可以在第 ④ 页自己拆开。

---

## 6. 已知环境事实与坑

- **模型路由**：`group/auto-deepseek-v4-1-flash` 会丢掉 system 消息（实测 `prompt_tokens=61`），AI 拿不到手册和提示词。**不要用这个路由。** 可用的有：`workbuddy-ai/deepseek-v4.1-flash`、`deepseek/deepseek-flash`、`codex/gpt-6.1-sol`。
- **频率限制**：`workbuddy-ai/deepseek-v4.1-flash` 连续发几十轮会报 429，额度按天重置。
- **换任何模型前都要先查**：
  1. `prompt_tokens` 应在 2 万以上，否则 system 被丢了；
  2. 回复正文里有没有 `<think>` 之类的思考内容，有就会被显示并朗读出来，可能直接说出真相。
- **MinerU**：导入识别用本机 MinerU。DeepSeek 为过隐私预检会临时起服务，跑完要 `mineru server stop`，并清掉残留的 `doclib.lock`。
- **`.gitignore`** 已排除 `/stories/`、`/.appdata/`（剧本包 JSON 含全文与真相）、`/.local/`（验收对话记录）、`/.archify/`、`/.playwright-mcp/`。
- **剧本**：
  - 《孽岛疑云》：5 人，全图片，组织者手册 12 页，真相页第 8–11 页。用户已经看过它的真相，验收时可以把原话贴给用户。
  - 《K7-星火号》：5 人，手册是 17 页 PDF，5 本角色本 PDF 各 18 页，线索卡 12 张图片。**用户还没玩过，任何汇报都不能出现剧情。** 它的完整验收记录在 `.local\dm-acceptance\` 下，提醒用户玩之前别打开。
- **常用命令**（在 `D:\2\dev\dm`）：

```powershell
dotnet build LocalDm.slnx
dotnet test tests/LocalDm.Tests
dotnet test tests/LocalDm.UiTests

# AI 主持验收（每次写到 .local\dm-acceptance\<时间>\）
dotnet run --project tools\LocalDm.Probe -- --dm-acceptance --model <模型ID> [--ask 1,2,4,6] [--advance-to "<环节名>"] [--phase story|chat|untuned|compare]

# 导入向导验收
dotnet run --project tools\LocalDm.Probe -- --import-wizard --story "<剧本文件夹>" --title "<剧本名>" [--players 5] [--model <模型ID>] [--reuse <已有剧本包路径>]
```

---

## 7. 主要文件索引

| 用途 | 位置 |
|---|---|
| AI 主持方案 | `docs/plans/2026-10-20-ai-dm-organizer-context.md` |
| 导入向导方案 | `docs/plans/2026-10-21-script-import-wizard.md` |
| 决定记录 | `docs/decisions/0003-ai-dm-reads-organizer-and-clues.md`、`docs/decisions/0004-import-wizard-ai-drafted-flow.md` |
| 验收证据 | `docs/evidence/us3/ai-dm-acceptance.md`、`import-wizard-acceptance.md`、`model-comparison.md` |
| 项目状态 | `docs/project-state.md` |
| 主持提示词与上下文构造 | `src/LocalDm.Core/Hosting/HostNarration.cs` |
| 对话记忆、计量、压缩 | `src/LocalDm.Core/Hosting/DmConversation.cs` |
| 备忘检查 | `src/LocalDm.Core/Hosting/DmMemoCheck.cs` |
| 输入分流 | `src/LocalDm.Core/Hosting/HostIntentRouter.cs` |
| 主持台 | `src/LocalDm.App/ViewModels/HostViewModel.cs`、`src/LocalDm.App/Views/HostView.xaml` |
| 导入服务 | `src/LocalDm.Infrastructure/Import/ScriptImportService*.cs` |
| 流程起草与剧透检查 | `src/LocalDm.Core/Import/FlowDrafter.cs`、`FlowSpoilerCheck.cs` |
| 导入向导界面 | `src/LocalDm.App/ViewModels/ImportWizardViewModel.cs`、`src/LocalDm.App/Views/ImportWizardView.xaml` |
| 命令行探针 | `tools/LocalDm.Probe/` |
