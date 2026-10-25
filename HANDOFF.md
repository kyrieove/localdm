# HANDOFF · 2026-10-24 · 「点错了能改」和平板全屏看原稿都已合入；下一步问用户要不要发布

## 现在
- 已验证（都已提交，未推送）：
  - `55397c0` 点错了能改（ADR 0010）：规则核心 `RevertPhase`、同轮改票替换、模型〔提议：…〕变电脑确认条。sol 执行；Claude 独立核对过退回后环节 / Revision / 已发材料、揭秘后拒绝。主树完整 Check（构建 + 两套测试）通过。
  - `dc4c0b4` 平板原稿和线索卡点开全屏（卡 `docs/orch/2026-10-23-tablet-photo-viewer/`）：滑动 / 点边缘翻页、捏合双击、翻页保持缩放、返回键关闭后阅读器停到看过的那屏、新消息横幅、预加载。浏览器实测过打开 / 翻页 / 返回键 / ✕ / Esc / 线索卡；没验：横幅、捏合、点边缘、真全屏和常亮（要真平板）。
  - `6eaac94` 手册页签、`5dd34f1` 重新开始本局与双栏、`bdb3b87` 改名 Dungeon Master（详见 git log）
- 未做：**用户说先别发布**。桌面上装的还是旧版「本局主持」（`%LOCALAPPDATA%\Programs\LocalDm`）。
- 未验证：没在真软件、真平板上点过；真实 iPad / 安卓从没实测（`docs/evidence/tablet/` 为空）。
- 新工作树缺不入库的 `fixtures/synthetic/.private/expected.json`，`ImportCoverageTests` 会红：跑 `scripts/sync-local-fixtures.ps1 -Source D:\2\dev\dm` 补。

## 下一步
00. **先做这个**：2026-10-25 真人四人整局发现 6 个问题，已记录待修（还没改代码）：`docs/playtest/2026-10-25-real-four-player-issues.md`。最要紧的是保密过严导致手册分段引导用不了，和揭秘长文语音超时换声重读。
   - 2026-10-25 已做（`4afc8b3`、`6332a0c`、`7c5d306`）：第 1 条提示词修了（分段引导例外、手册没写如实说）；按 ADR 0011 角色本进主持资料，固定提示词按试玩问题重写；设置页高级项能只读查看提示词。没在真软件里试玩验证。5 人本加角色本后 system 约 5.2 万 token，默认窗口 65,536 时要调大（本机设的是 200 万）。
   - 2026-10-25 也做了：第 5 条长文分段朗读（`8ab9248`，每段 ≤ 500 字，Edge 某段失败从这段起用本地语音接着读；中途失败那条路径会真出声，没有自动测试）；默认上下文窗口改成 100 万（`d6ae7ff`）。
   - 第 3 条改成翻卡（`8cf210d`）：角色本原稿默认盖着，长按按顺序翻开，没按环节解锁。预览稿 `docs/preview/role-flip-20261025/`，探针 `--flip-preview` 生成真实页版本到 `.local/`。真平板手势没验。
   - 还没做：第 2 条引导段进流程、第 4 条找回角色、暂缓线索找不到的原因。
0. 试玩（2026-10-24，报告 `docs/playtest/2026-10-24-four-player-playtest.md`，含完整对话的本机版在 `.local/playtest/20261024-023234/report.md`）发现：玩家直接问 DM 时有 2 次没回复。推测是 `HostViewModel.SendAsync` 在忙的时候直接 `return`，把消息静默吞掉了。先在探针里记录发送那一刻的 `IsBusy` 来确认，确认后改成忙时给提示或排队。试玩命令：`dotnet run --project tools/LocalDm.Probe -- --playtest --model gpt-6.1-sol`（约 45 分钟）。
1. 新版已发布到桌面（2026-10-24），开始菜单的旧图标和旧快捷方式已清理。
   发布：先 `pwsh -File packaging/windows/Publish.ps1`，再 `artifacts/LocalDm-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /NORESTART /TASKS=desktopicon`。改名后快捷方式叫 Dungeon Master，桌面上旧的「本局主持.lnk」要请用户自己删。
2. 实机（只有用户能做）：iPad 和安卓各扫码，验证发言、整套界面、线索卡、手册、双栏，结果写进 `docs/evidence/tablet/`。

## 先读
- `docs/decisions/0005`、`0007`、`0008`、`0009`、`0010`；`LESSONS.md`；orchestrate 规则 `D:/2/dev/orch-runner/orchestrate/SKILL.md`
- 写替换脚本时，Python 字符串里的 `\a`（比如 `Assets\app.ico`）会变成控制字符 0x07，这次 `.iss` 和 `.csproj` 都中过。路径一律用 `chr(92)` 拼。

## 已知缺口
- 整版多卡的图片剧本仍没有确认版式的界面：平板上会发整张大图。
- 全图片手册里被声明「不入包」的空白页，平板手册里会缺这一页（PDF 手册不受影响）。
