# Feature Specification: 本局主持 Windows 单电脑 MVP

**Feature Branch**: `001-local-dm-mvp`
**Created**: 2026-10-19
**Status**: Planned, not implemented
**Input**: 已确认对话、`docs/plans/2026-10-19-local-dm-mvp.md` v0.3-review，用户本次要求执行 plan 和 tasks。

## User Scenarios & Testing

### User Story 1 - 打开软件并听到文字模型主持 (Priority: P1)

玩家打开 Windows 软件，配置已有 Magpie，选择普通文字模型，点击公共主持动作；软件将文字朗读。
不要求语音模型，不要求账户或联网玩家端。

**Why this priority**: 最小价值是可靠的公共主持口播，而非另一款聊天或多人平台。

**Independent Test**: 使用合成公开材料，在真实 Windows 窗口中经本机 Magpie 获取文字并 TTS 播放；可停止、重播及取消请求。

**Acceptance Scenarios**:
1. Given 可用的 Windows Magpie，When 选择实际 model ID 并请求合成开场，Then 显示及朗读公开文本，记录模型实际返回。
2. Given 已有本段口播，When 点击重播，Then 只播放已有文字，不重新生成或修改游戏。
3. Given 模型超时或旧请求晚到，When 重试或取消，Then 提示准确且晚到结果不会播放。

### User Story 2 - 准备完整剧本而不提前剧透 (Priority: P1)

玩家导入 PDF、图片文件夹或可信的预解析产物，软件复用现有文档工具，保留完整来源并提供脱敏准备报告。
私人角色本与线索由软件外的独立 PDF、纸本或编号材料包查看。

**Why this priority**: 错误或剧透的剧本不能支撑公平完整的一局。

**Independent Test**: 对带已知真值的合成 PDF/图片验证全文、续读与来源；缺页、冲突、服务不可用或无法校验的内容不能成为 Ready。

**Acceptance Scenarios**:
1. Given 多文件剧本，When 完成解析，Then 每个文件及页有处理记录，关键角色、阶段、线索和结局均能回指原文。
2. Given MinerU 首段输出含继续请求，When 导入，Then 必须完整续读，不能用首段作为全文。
3. Given 关键缺漏或冲突，When 准备检查，Then 只显示脱敏异常并阻止开局，不补写事实或改用样例。
4. Given 未允许外发，When 工具需要远程服务，Then 停在授权门禁，不上传真实文件。

### User Story 3 - 按原本带完整局并可靠恢复 (Priority: P1)

操作者通过共用电脑触发开场、阅本、搜证、讨论、投票主持和复盘；每人仍是玩家，不需要看真相。
阶段推进由原本条件与大家口头确认决定，程序持久化每次有效操作。

**Why this priority**: 控场和完整局流程是用户的核心用途。

**Independent Test**: 从一个已冻结合成剧本包走完所有环节，检查重复操作、强退恢复和提前揭秘均不会破坏流程。

**Acceptance Scenarios**:
1. Given 未确认全员准备或原本条件未满足，When 下一步，Then 保持当前阶段并给出无剧透原因。
2. Given 相同 operationId 或过期 revision，When 重试，Then 返回原提交或冲突，不额外推进和发材料。
3. Given 强退时存在已提交操作，When 重启，Then 恢复相同剧本版本与最后有效状态，不自动播放。
4. Given 纸笔投票汇总，When 录入并二次确认，Then 按剧本结算；揭秘仍需明确确认和规则条件。

### User Story 4 - 用既有 OpenLess 共同询问 (Priority: P2)

玩家聚焦主持台文字框，用已有 OpenLess 快捷键转写，回车或点击发送；软件通过普通文字模型和 TTS 回应。

**Why this priority**: 利用现有语音输入工具，保持说话交流而不扩大应用音频实现。

**Independent Test**: 在打包后的真实文字框通过 OpenLess 输入中文、标点和长句；发送不丢字、不重复，状态意图不越过确认。

**Acceptance Scenarios**:
1. Given 正在中文组合输入，When 输入法处理 Enter，Then 不误提交未完成文本。
2. Given OpenLess 写入已完成文本，When 回车或发送，Then 正常提交且不另弹普通转写确认。
3. Given 文本请求下一阶段，When 识别出状态意图，Then 只打开确认，不直接修改状态。
4. Given OpenLess 不运行，When 手动输入，Then 仍可正常主持，应用不会改为自行录音。

### User Story 5 - 安装软件并完成真人一局 (Priority: P2)

交付 Windows x64 安装包与便携包，在无开发 SDK 的环境双击运行。
Magpie、OpenLess 为用户已有独立软件；文档工具仅在准备阶段使用。

**Why this priority**: 用户需要成型软件，不是终端命令或浏览器演示。

**Independent Test**: 安装实际发布包，在目标电脑用授权固定剧本完成真实 4–6 人整局并验证故障恢复。

**Acceptance Scenarios**:
1. Given 无开发环境的 Windows 用户，When 安装运行，Then 独立窗口可开，不要求 Node、Python 或 .NET SDK；外部工具由设置检查。
2. Given 已准备的冻结剧本包，When MinerU 服务关闭，Then 可正常开始和完成游戏，不重新 OCR。
3. Given 升级或卸载，When 操作完成，Then 数据保留策略明确，不能静默删除剧本或存档。

### Edge Cases

文档缺页/乱序/低置信度；工具报成功但结果截断；已解析包源指纹不匹配；Magpie 返回未知 model 或无效 JSON；取消后回复晚到；TTS 不可用；OpenLess 焦点变化；并发双击；落盘中断；原本没有明确规则；秘密被提示注入索取。

## Requirements

### Functional Requirements

- **FR-001**: 必须从零自建单电脑 Windows 软件；不复用 SoloPlay 源码，不创建平板、房间、账号或多人问答系统。
- **FR-002**: 必须使用本机 Windows Magpie 普通文字模型与独立中文 TTS；地址可配置，不自动切到 WSL 或直连另一个供应商。
- **FR-003**: 必须支持 OpenLess 向文字框写入后回车/按钮发送及手动文字输入；不得采音、自建 ASR 或要求音频模型。
- **FR-004**: 必须支持公共开场、规则、搜证/材料包提示、投票主持、结果和复盘；默认等待玩家触发，不催促、提示或自动跳阶段。
- **FR-005**: 必须覆盖完整文本 PDF、扫描 PDF、图片及预解析产物；优先复用已有 MinerU 等工具，保留全文、文件指纹与页块 SourceSpan。
- **FR-006**: 必须阻止关键缺漏、矛盾、来源缺失或未完成续读的数据开局；不得推断凶手、补造事实、静默截断或用样例替换输入。
- **FR-007**: 公共屏幕、TTS、日志、普通问答上下文不得获取未公开秘密或真相；不能仅靠 UI 隐藏或 LLM 自审来隔离。
- **FR-008**: 阶段迁移、材料释放、投票汇总与结算必须由程序裁决；模型不能写状态。
- **FR-009**: 推进必须满足原本条件、预期 revision 和全员口头确认勾选；投票提交和揭秘必须另有明确确认。
- **FR-010**: operationId 重试不能重复提交；存档绑定不可变剧本版本，原子提交和重启恢复不得自动重播或重做动作。
- **FR-011**: TTS 仅一段播放，重播不重新生成，取消后丢弃晚到结果；模型或 TTS 故障不改变已提交状态。
- **FR-012**: 必须交付自包含 Windows x64 安装包和便携包，数据不存安装目录，不公开分发真实剧本。
- **FR-013**: 文档工具本机入口不构成外发授权；真实文件/剧本文字外发前须明确允许，未允许仅使用合成数据。
- **FR-014**: 原本未写清必须诚实说明，不能自动补规则；不支持的玩法在准备阶段明确阻止。

### Required Verification

规格明确要求有意义的自动化用例验证来源覆盖、秘密隔离、状态确认、幂等、原子恢复、取消及中文输入；实际 Windows 输入、TTS 和发布包必须现场操作验证。
不要求全项目强制 TDD；关键安全/状态用例在各故事的实现任务之前准备。
真实剧本和 4–6 人整局不得被合成测试、Mock 或多端模拟结果替代。

### Key Entities

ScriptPackage、SourceSpan、ImportJob、ReadinessReport、Session、GameCommand、GameEvent、PublicHostRequest、PublicHostReply、ToolSettings。
细节由 data-model.md 与 contracts 定义，避免在此重复。

## Success Criteria

- **SC-001**: 目标 Windows 用户可安装并双击启动，两种发布物都通过实际启动检查。
- **SC-002**: 完整合成输入页覆盖率和关键事实来源覆盖率均为 100%；至少缺页、冲突、未续读、来源失配四类输入被拦截。
- **SC-003**: 自动化公开投影测试及实际公共窗口检查中，未解锁秘密字段出现次数为 0。
- **SC-004**: 对同一操作重复提交 10 次只发生一次有效迁移，过期 revision 被拒绝；至少在事件写入和快照替换两个故障点验证恢复。
- **SC-005**: 实机 OpenLess 中文输入与 20 次发送/取消/重播场景无丢字、重复提交或重叠播音；应用麦克风访问为 0。
- **SC-006**: 获准的固定剧本由真实 4–6 人从开局玩到复盘；没有必须由一名玩家阅读真相的步骤。

以上是验收目标，不是当前测量结果。实际响应耗时单独记录，不捏造性能提升。

## Assumptions

用户已确认外部私人材料包，首版不支持软件内秘密分发。
纸笔投票后人工录入汇总是最小设计默认，不声称用户单独确认；开局前必须显式接受该方式且剧本允许，否则不支持该局。
用户选择执行 plan/tasks 表示继续设计，不授权本轮开始实现或外发真实剧本。
Magpie、OpenLess 作为已有外部依赖；MinerU 未运行不等于可离线 OCR，pdf-inspector 尚未定位且不作为必须依赖。
