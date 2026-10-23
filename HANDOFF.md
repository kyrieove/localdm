# HANDOFF · 2026-10-23 · 平板界面镜像（第二期）：网页已做完，等真实平板实测

## 现在
- 已验证：投影（`178fcc6`）、接口 `GET /tablet/ui`（`314aa82`）、网页卡 `tablet-ui-page`（本次提交）。「公共对话」页签里 `LiveBoard.razor` 画镜像界面：环节三态列表、提示、灰色按钮（全部 `disabled`）加 `controlsNote`、材料数、投票汇总、揭秘文本、公开对话；≤900px 折叠成一行当前环节；投影为 null 显示「等待主持人开始这一局」。页面直接注入 `ITabletUiSource` 读数据（和读公开消息同一做法），不走 HTTP；`/tablet/ui` 保留作契约。
- 实测：构建 0 警告 0 错误；`LocalDm.Tests` 429 过 / 6 跳过 / 0 败；`LocalDm.UiTests` 59 过。`tools/tablet-qr/verify-ui.mjs`（合成探针，Playwright）在 1360×1000、1112×834、834×1112、430×932 四个视口全过：灰色按钮全 disabled、无横向溢出、页面无「备忘」「上下文」、折叠状态正确，截图在被忽略的 `artifacts/tablet/ui-mirror/`。
- 未验证：第一期（发言）和第二期（整套界面）都没有真实 iPad / 安卓平板实测，`docs/evidence/` 没有记录，通过前不能说完成。真实桌面局里的 `CurrentSessionTabletUiSource` 只在测试里验过，没有人在真机上看过。
- 本卡没有 sol 审查（codex 额度用尽），由 Claude 看 diff + Playwright 验收；`unreviewed-fix`：`LiveBoard.razor` / `Home.razor` 合并前请用户扫一眼。另有旧的：卡 1 防泄露用例 `tests/LocalDm.Tests/TabletUiProjectionTests.cs`（原应 high，经同意降 medium）、`tests/LocalDm.UiTests/TabletSpeechE2ETests.cs` 的 5 处 try/finally。

## 下一步
1. 实机（只有用户能做）：`启动本局测试.cmd` 开一局，iPad 和安卓平板各扫码，验证发言和整套界面（「公共对话」页签），把设备型号、系统、浏览器版本、结果写进 `docs/evidence/tablet/` 新文件。
2. 修 `tools/tablet-qr/verify-reader.mjs`：它假定阅读器默认是文字重排，现在合成数据带原稿页、默认显示原稿，第 26 行 `tabletReader.total()>0` 超时（HEAD 上同样失败，与本卡无关）。
3. 探针怎么跑：在 `src/LocalDm.Tablet` 目录里 `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build -- --urls http://127.0.0.1:5099`（Production 下不映射 Blazor 脚本，页面空白；从仓库根目录启动内容根不对，静态文件 404），再 `node tools/tablet-qr/verify-ui.mjs --playwright-module <playwright 目录>`；每次探针要重启服务以释放已认领角色。

## 先读
- `docs/decisions/0008-tablet-ui-mirror.md`、`docs/decisions/0007-tablet-live-chat.md`
- `docs/orch/2026-10-23-tablet-ui-page/card.md`

## 坑
- 派活卡 Risk 首词必须等于 `--risk`；high 的计划审查同一张卡最多两轮。改卡用 Edit。
- 卡里的只读路径别写 `src/LocalDm.App/`、`tests/` 这种整目录：基线 Check 编译会改 `bin/obj`，判 `readonly_violated`（本次又踩了一次）。只列源码文件或不含 bin/obj 的子目录。
- 基线 Check 没有针对新代码的用例时也会绿：runner 会 WARNING，验收要另测。
- 测试里序列化投影必须用 `UnsafeRelaxedJsonEscaping`。
- 并行整套测试偶发 `EdgeTtsTests` 探针超时、`HostConversationTests.Replay_holds_the_session_busy_gate`，单跑稳定通过。
- 旧 worktree/分支 `orch/tablet-speech-wiring`、`orch/tablet-speech-ui` 还留在 `~/.claude/agent-live/orch-runner/wt/`，内容已合入，删分支需用户确认。

## 等用户回答
- 晚上导入新剧本：先问是不是整版多卡（一张图多张卡）。
