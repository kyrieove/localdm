# HANDOFF · 2026-10-23 · 平板界面镜像（第二期）：第一张卡已合入，接线卡和网页卡未做

## 现在
- 已验证：ADR 0008 + 宪章 8.0.0 + 规格/user-guide 已同步（`e0da28d`）；第一张卡 `tablet-ui-projection` 合入（`178fcc6`）：`TabletUiProjector.Project(package, session, feed, hostBusy, hostSpeaking)` 返回只含公开内容的 `TabletUiProjection`（环节、环节列表、七个按钮的显示/可用状态、公开对话、揭秘文本、票数）。构建 0 警告 0 错误；`LocalDm.Tests` 435（429 过 / 6 人工授权跳过 / 0 败）；`LocalDm.UiTests` 53 过。独立重算：Playing 真相 0 次，Revealed 真相 1 次且在 `.RevealedTexts[0]`，角色本/线索卡/主持页/阶段条件 0 次。
- **unreviewed-fix**：该卡应为 high，计划审查用满两轮（第二轮因我的改卡脚本报错审了旧卡），经用户同意降 medium 派发；合并前请看 `tests/LocalDm.Tests/TabletUiProjectionTests.cs` 的防泄露用例。
- 未验证：第一期（平板发言）和第二期都没有真实 iPad / 安卓平板实测，`docs/evidence/` 里没有记录；通过前两期都不能说完成。投影还没接进任何接口，平板网页看不到它。

## 下一步
1. 写第二张卡 `tablet-ui-wiring`：`IPublicFeedSource` 加读投影的方法（`EmptyPublicFeedSource`、`SpikePublicFeedSource`、`CurrentSessionPublicFeedSource` 都要实现），平板服务加 `GET /tablet/ui`（要浏览器会话，沿用 `/tablet/public` 的 `after` 补拉与 `host.busy/speaking`）；端到端测试里加真实包的泄露检查（含备忘、上下文占用）。
2. 第三张卡 `tablet-ui-page`：Razor 页面重做软件界面（见 ADR 0008 决定 3–5；七个按钮灰色加「请在电脑上操作」，竖屏折叠成一行当前环节，不显示备忘/上下文占用），能点的只有发送和停止朗读，发言走 `SpeechBar.razor` 现有路径。依赖第二张卡验收。
3. 实机：用户用 `启动本局测试.cmd` 开一局，iPad 和安卓平板各扫码，验证发言（第一期）和整套界面（第二期），把设备型号、系统、浏览器版本、结果写进 `docs/evidence/` 新文件。

## 先读
- `docs/decisions/0008-tablet-ui-mirror.md`、`docs/decisions/0007-tablet-live-chat.md`
- `docs/orch/2026-10-23-tablet-ui-projection/card.md`（含 Acceptance 与 deferred）
- 派活流程：`D:/2/dev/orch-runner/orchestrate/SKILL.md`

## 坑
- 派活卡 Risk 首词必须等于 `--risk`；high 会做 sol 计划审查，同一张卡最多两轮，第二次 `plan_revise` 后 runner 没有绕过开关，只能用户裁决降级。改卡后先确认改成了（我用 python 写 `\u` 没转义，脚本报错但 runner 仍派了旧卡，白用一轮）：改卡用 Edit，且 `&&` 串联派发。
- 卡里的只读路径别写 `src/LocalDm.App/` 这种整目录：基线 Check 编译会改它的 `bin/obj`，判 `readonly_violated`。只列源码子目录。
- 并行派两张卡用 `--isolation worktree`，两张卡的 may-change 不能重叠。
- agy 在含测试脚手架的卡上会卡在探索：卡里直接给现成写法（本卡这样写，agy 2.6 分钟做完）。
- 测试里序列化投影必须用 `UnsafeRelaxedJsonEscaping`，默认会把中文转义成 `\uXXXX`，标记词检查恒为 0。
- 并行整套测试偶发 `EdgeTtsTests` 探针超时、`HostConversationTests.Replay_holds_the_session_busy_gate`，单跑稳定通过。
- `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs` 的 5 处 try/finally 是 `unreviewed-fix`（卡 `tablet-speech-wiring-v2`），合并前请看一眼。
- 旧 worktree/分支 `orch/tablet-speech-wiring`、`orch/tablet-speech-ui` 还留在 `~/.claude/agent-live/orch-runner/wt/`，内容已合入，删分支需用户确认。

## 等用户回答
- 晚上导入新剧本：先问是不是整版多卡（一张图多张卡）。
