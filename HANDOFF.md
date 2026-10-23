# HANDOFF · 2026-10-23 · 「点错了能改」卡待派给 sol；之后发布 Dungeon Master

## 现在
- 已验证（都已提交，未推送；构建 0 警告，`LocalDm.Tests` 442 条、`LocalDm.UiTests` 59 条全过）：
  - `6eaac94` 平板「组织者手册」页签（ADR 0009）
  - `5dd34f1` 主持台「重新开始本局」（左栏流程提示下方，两步确认，清存档并让平板回到选角色）；平板原稿双栏改成左右并排，只剩一页时居中，并补上缩放 / 整页按钮的样式
  - `bdb3b87` 改名 Dungeon Master，新图标是二十面骰（`packaging/windows/make-icon.ps1` 生成 `src/LocalDm.App/Assets/app.ico`）；对话里主持人署名改为 DM
  - 预览页 `docs/preview/2026-10-23-restart-dual/index.html`，用户已看过
- 未做：**用户说先别发布**。桌面上装的还是旧版「本局主持」（`%LOCALAPPDATA%\Programs\LocalDm`）。
- 未验证：上面这些都没在真软件、真平板上点过；真实 iPad / 安卓从没实测（`docs/evidence/tablet/` 为空）。
- `docs/preview/fullscreen-viewer.html` 是未跟踪文件，不是本会话产生的，别动。

## 下一步
1. 派发 `docs/orch/2026-10-23-dm-correction/card.md`（用户要求：Claude 只思考和计划，实际写代码交给 sol）。卡片检查的两处问题已修，第一次派发什么都没执行：
   ```bash
   node D:/2/dev/orch-runner/orchestrate/runner/run-card.mjs --card D:/2/dev/dm/docs/orch/2026-10-23-dm-correction/card.md --name dm-correction --executor sol --risk high --repo D:/2/dev/dm # agent-live/codex-dm-correction.jsonl agent-live/codex-review-dm-correction.jsonl agent-live/codex-plan-dm-correction.jsonl
   ```
   内容：规则核心加 `RevertPhase`；同一轮重新提交票数时替换原记录；模型在回复最后一行写〔提议：…〕，软件把它变成电脑上的确认条，点确认才执行；系统提示词只追加第 10 条；ADR 0010 + 宪章 10.0.0。验收时独立核对：从 `AdvancedTo(3)` 退回后环节是 `phase-search`、Revision 加 1、已发材料不变；揭秘后退回被拒。
2. 验收通过后问用户要不要发布：先 `pwsh -File packaging/windows/Publish.ps1`，再 `artifacts/LocalDm-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /NORESTART /TASKS=desktopicon`。改名后快捷方式叫 Dungeon Master，桌面上旧的「本局主持.lnk」要请用户自己删。
3. 实机（只有用户能做）：iPad 和安卓各扫码，验证发言、整套界面、线索卡、手册、双栏，结果写进 `docs/evidence/tablet/`。

## 先读
- `docs/decisions/0005`、`0007`、`0008`、`0009`；`LESSONS.md`；orchestrate 规则 `D:/2/dev/orch-runner/orchestrate/SKILL.md`
- 写替换脚本时，Python 字符串里的 `\a`（比如 `Assets\app.ico`）会变成控制字符 0x07，这次 `.iss` 和 `.csproj` 都中过。路径一律用 `chr(92)` 拼。

## 已知缺口
- 整版多卡的图片剧本仍没有确认版式的界面：平板上会发整张大图。
- 全图片手册里被声明「不入包」的空白页，平板手册里会缺这一页（PDF 手册不受影响）。
