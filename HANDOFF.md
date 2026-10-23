# HANDOFF · 2026-10-23 · 平板界面镜像（第二期）：投影和接口已合入，网页卡未做

## 现在
- 已验证：ADR 0008 + 宪章 8.0.0 + 规格同步（`e0da28d`）；卡 1 `tablet-ui-projection`（`178fcc6`）：`TabletUiProjector` 只含公开内容 + 防泄露测试；卡 2 `tablet-ui-wiring`（`314aa82`）：`GET /tablet/ui?after=N`（无 Cookie 401、`after` 非法 400、桌面未装载 404 `NOT_READY`）、`CurrentSessionTabletUiSource`（UI 线程上包与会话一起投影）、`TabletJoinViewModel`/`AppComposition` 接线。构建 0 警告 0 错误；`LocalDm.Tests` 435（429 过 / 6 人工授权跳过 / 0 败）；`LocalDm.UiTests` 59 过。独立重算：演示剧本非公开段落 1 个，原文在 `/tablet/ui` 响应体出现 0 次。
- `/tablet/ui` 的 JSON 形状（Prepared 态实测）：`sessionId,title,state,revision,phaseId,phaseTitle,phaseIndex,phaseCount,hint,phases[{phaseId,title,status:Done|Current|Upcoming}],controls[{kind:Start|Advance|ReleaseMaterials|SubmitVote|Reveal|Complete|PauseResume,label,visible,enabled}],controlsNote:"请在电脑上操作",pendingMaterialCount,releasedMaterialCount,voteSummary,revealedTexts,messages[{sessionId,messageId,author,text,createdAt,kind,sequence}],nextSequence,hostBusy,hostSpeaking`；`messages` 只含 `after` 之后的，网页要自己累积。
- **unreviewed-fix**：卡 1 应为 high，计划审查用满两轮后经用户同意降 medium；合并前请看 `tests/LocalDm.Tests/TabletUiProjectionTests.cs` 的防泄露用例。另 `tests/LocalDm.UiTests/TabletSpeechE2ETests.cs` 的 5 处 try/finally 是旧的 `unreviewed-fix`（卡 `tablet-speech-wiring-v2`）。
- 未验证：网页还没做，平板上看不到镜像界面；第一期（发言）和第二期都没有真实 iPad / 安卓平板实测，`docs/evidence/` 里没有记录，通过前不能说完成。

## 下一步
1. 写第三张卡 `tablet-ui-page`（medium，agy）：先读 `src/LocalDm.Tablet/Pages/`、`Layout/`、`SpeechBar.razor`、`wwwroot/` 的现有结构再写卡（卡里直接给现成写法）。要点（ADR 0008 决定 3–6）：轮询 `/tablet/ui?after=N`（沿用 `/tablet/public` 的 2 秒轮询方式）；重做软件主窗口布局：左栏环节列表 + 当前环节 + 七个灰色按钮旁写 `controlsNote`；竖屏放不下时折叠成一行当前环节；中间公开对话；不显示备忘/上下文占用；能点的只有「发送」和「停止朗读」，发言走 `SpeechBar.razor` 现有路径，不另写发送逻辑；`NOT_READY` 时显示「等待主持人开始这一局」。参照桌面布局看 `src/LocalDm.App/MainWindow.xaml` 约 191–271 行（行号以现状为准）。
2. 页面用 `tools/tablet-qr/verify-reader.mjs` 同类的 Playwright 探针在手机/平板视口下截图检查横竖屏（脚本里不要启动真实剧本，用合成内容）。
3. 实机（只有用户能做）：`启动本局测试.cmd` 开一局，iPad 和安卓平板各扫码，验证发言（第一期）和整套界面（第二期），把设备型号、系统、浏览器版本、结果写进 `docs/evidence/` 新文件。

## 先读
- `docs/decisions/0008-tablet-ui-mirror.md`、`docs/decisions/0007-tablet-live-chat.md`
- `docs/orch/2026-10-23-tablet-ui-wiring/card.md`（含 Acceptance）
- 派活流程：`D:/2/dev/orch-runner/orchestrate/SKILL.md`

## 坑
- 派活卡 Risk 首词必须等于 `--risk`；high 会做 sol 计划审查，同一张卡最多两轮，第二次 `plan_revise` 后 runner 没有绕过开关，只能用户裁决降级。改卡用 Edit（python 写 `\u` 没转义会静默失败，runner 仍派旧卡，白用一轮）。
- 卡里的只读路径别写 `src/LocalDm.App/` 这种整目录：基线 Check 编译会改 `bin/obj`，判 `readonly_violated`。只列源码文件或子目录。
- 基线 Check 若只跑旧测试类也会绿：runner 会 WARNING「Check 已经通过」，验收时要单独跑新测试类确认真的执行。
- agy 在含测试脚手架的卡上会卡在探索：卡里直接给现成写法（卡 1、2 这样写，agy 2.6、4.4 分钟做完）。
- 测试里序列化投影必须用 `UnsafeRelaxedJsonEscaping`，默认会把中文转义成 `\uXXXX`，标记词检查恒为 0。
- 并行整套测试偶发 `EdgeTtsTests` 探针超时、`HostConversationTests.Replay_holds_the_session_busy_gate`，单跑稳定通过。
- 旧 worktree/分支 `orch/tablet-speech-wiring`、`orch/tablet-speech-ui` 还留在 `~/.claude/agent-live/orch-runner/wt/`，内容已合入，删分支需用户确认。

## 等用户回答
- 晚上导入新剧本：先问是不是整版多卡（一张图多张卡）。
