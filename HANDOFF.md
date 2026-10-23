# HANDOFF · 2026-10-23 · 平板界面镜像（第二期）：网页已做完，等真实平板实测

## 现在
- 已验证：投影（`178fcc6`）、接口 `GET /tablet/ui`（`314aa82`）、网页卡 `tablet-ui-page`（本次提交）。「公共对话」页签里 `LiveBoard.razor` 画镜像界面：环节三态列表、提示、灰色按钮（全部 `disabled`）加 `controlsNote`、材料数、投票汇总、揭秘文本、公开对话；≤900px 折叠成一行当前环节；投影为 null 显示「等待主持人开始这一局」。页面直接注入 `ITabletUiSource` 读数据（和读公开消息同一做法），不走 HTTP；`/tablet/ui` 保留作契约。
- 实测：构建 0 警告 0 错误；`LocalDm.Tests` 429 过 / 6 跳过 / 0 败；`LocalDm.UiTests` 59 过。`tools/tablet-qr/verify-ui.mjs`（合成探针，Playwright）在 1360×1000、1112×834、834×1112、430×932 四个视口全过：灰色按钮全 disabled、无横向溢出、页面无「备忘」「上下文」、折叠状态正确，截图在被忽略的 `artifacts/tablet/ui-mirror/`。
- 未验证：第一期（发言）和第二期（整套界面）都没有真实 iPad / 安卓平板实测，`docs/evidence/` 没有记录，通过前不能说完成。真实桌面局里的 `CurrentSessionTabletUiSource` 只在测试里验过，没有人在真机上看过。
- 本卡没有 sol 审查（codex 额度用尽），由 Claude 看 diff + Playwright 验收；`unreviewed-fix`：`LiveBoard.razor` / `Home.razor` 合并前请用户扫一眼。另有旧的：卡 1 防泄露用例 `tests/LocalDm.Tests/TabletUiProjectionTests.cs`（原应 high，经同意降 medium）、`tests/LocalDm.UiTests/TabletSpeechE2ETests.cs` 的 5 处 try/finally。

## 下一步
1. 实机（只有用户能做）：`启动本局测试.cmd` 开一局，iPad 和安卓平板各扫码，验证发言和整套界面（「公共对话」页签），把设备型号、系统、浏览器版本、结果写进 `docs/evidence/tablet/` 新文件。
2. 探针怎么跑（`verify-reader.mjs` 已改为先确认原稿模式再点「文字重排」，11 项全过）：在 `src/LocalDm.Tablet` 目录里 `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build -- --urls http://127.0.0.1:5099`（Production 下不映射 Blazor 脚本，页面空白；从仓库根目录启动内容根不对，静态文件 404），再 `node tools/tablet-qr/verify-ui.mjs`（或 `verify-reader.mjs`）`--playwright-module <playwright 目录>`；每次探针要重启服务以释放已认领角色。

## 先读
- `docs/decisions/0008-tablet-ui-mirror.md`、`docs/decisions/0007-tablet-live-chat.md`
- `docs/orch/2026-10-23-tablet-ui-page/card.md`

## 坑
- 见 `LESSONS.md`。

## 等用户回答
- 新剧本《加个V呗》：线索是 PDF、一页一份材料，已按页出平板卡（探针 `--prepare-tablet` 9 张卡 2 组可用）；手册最后一页无文字，向导里改成「不入包」才放行。用户还没在界面里真正导入一遍，也没在平板上看过线索卡。整版多卡的图片剧本仍没有确认版式的界面（真实应用把所有组当单卡）。

## 复盘待办
- 全部做完（钩子、派活卡 lint、交接归档、CODING_STANDARDS.md、scripts/ui-shot.ps1、偶发红测试根因）。
