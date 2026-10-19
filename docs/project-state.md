# 当前项目状态

更新日期 2026-10-19。本轮执行 `speckit-implement`，按 tasks.md 实施 US1–US5 与收尾。

功能为 001-local-dm-mvp，活动规范是 specs/001-local-dm-mvp/spec.md。
宪章 3.0.0；范围为从零自建 Windows 单电脑软件。OpenLess 负责外部转写，Windows Magpie 普通文字模型与 System.Speech TTS 主持。

## 已实现

- 解决方案 `LocalDm.slnx`（.NET SDK 10.0.402，C# 14，net10.0-windows）：
  `src/LocalDm.Core`（领域/规则/投影/主持策略）、`src/LocalDm.Infrastructure`（Magpie、TTS、SQLite、MinerU、导入）、
  `src/LocalDm.App`（WPF）、`tests/LocalDm.Tests`、`tests/LocalDm.UiTests`、`tools/LocalDm.Probe`。
- 确定性玩法：`GameEngine` 决定阶段/材料/票数/揭秘；`CommandDispatcher` 事务内去重与 revision 校验；
  单文件 SQLite，event/receipt/投影同一事务。
- 秘密隔离：`PublicSessionProjector` 只投影 Public 段；`PublicContextBuilder` + `PublicReplyPolicy` 只允许公开原文与白名单过渡句。
- 播音：`HostDirector` 串行单段、requestId 失效即丢弃晚到结果、重播不请求模型。
- 文档准备：`MineruPrivacyPreflight`（版本/app-owned home/遥测）、`MineruRunner`（ArgumentList + 异步 stdout/stderr）、
  `MineruContinuationReader`（续读到 next_request=null + 循环检测）、`ImportPipeline`（无授权时在调用工具前拦截）。
- 发布物：自包含 win-x64、便携 ZIP、Inno Setup per-user 安装器。

## 验证结果（实测）

- `LocalDm.Tests`：94 项通过。`LocalDm.UiTests`：2 项通过（真实 MainWindow + 真实 SQLite）。
- 真实 Magpie 合成请求 + 真实中文播音通过（10.3 秒），重播 0 ms 不请求模型。
- 真实窗口经 UI Automation 走完 `Prepared → Playing → Revealed → Completed`（rev 0→8），未勾选准备时被拒。
- 安装器静默安装 → 启动 → 卸载全部退出码 0，卸载后用户数据保留。
- 详细证据见 `docs/evidence/`，核对表见 `docs/release-checklist.md`。

## 受阻与未执行（不得当作已交付）

- **真实剧本准备受阻**：本机 MinerU 4.0.1 无 basic/standard/advanced 引擎（`quality_tier_unavailable`）。
  `stories/<剧本目录>` 全部为 JPG 页面，依赖 OCR，因此尚未成为 Ready 包。
- **真实 4–6 人整局未执行**（需真人到场）。
- OpenLess 真实语音转写落字、20 次发送/取消/重播压力未执行。
- 125%/150% 缩放截图未执行。
- 发布物未做代码签名。

## 下一步

1. 在应用专属 MinerU profile 配置本地解析引擎，重跑 US2 解析与真实剧本准备；
2. 准备真实剧本并由独立校验者核对来源；
3. 组织 4–6 名真人完成一局，替换 `docs/evidence/us5/real-tabletop.md` 的「未执行」；
4. 目标机缩放复核与（可选）代码签名。

tasks.md 的完成状态见其末尾「实施状态」表（59 已完成 / 5 部分完成 / 2 受阻 / 1 未执行）。

## 历史（实施前）

设计阶段曾生成 plan 的研究与 Phase 1 设计产物，tasks 共 67 项：US1=10、US2=13、US3=12、US4=6、US5=8，其余 18 项为 Setup、Foundation、Polish。
`.specify/extensions.yml` 不存在，因此 before/after implement 钩子无注册项可执行。
设计验证命令仍是 `scripts/validate-planning.ps1`（校验设计文件、任务格式/编号/故事、12 条约束及活动功能），本轮实际运行通过。
真实剧本 `stories/`、运行数据与密钥由 `.gitignore` 排除（含 `fixtures/synthetic/.private/` 的合成真值）。
