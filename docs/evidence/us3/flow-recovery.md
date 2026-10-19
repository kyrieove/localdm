# US3 确定性完整局与恢复：实测记录

更新日期 2026-10-19。使用内置**演示用合成剧本**（非真实剧本），不含任何真实剧情或秘密。

## 结论

| 场景 | 结论 | 证据 |
|---|---|---|
| 真实窗口走完一局（Prepared → Playing → Revealed → Completed） | **已通过** | `ui-session-steps.json`，15/15 步 |
| 未勾选「大家已准备好」时开始被拒 | **已通过** | 步骤 3 后仍为 `Prepared · revision 0`，提示「请先确认大家已口头准备好。」 |
| revision 严格递增，每次提交恰加 1 | **已通过** | 0 → 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 |
| 同 operationId 重试 10 次只提交一次 | **已通过（自动化）** | `IdempotencyTests` |
| 同 ID 不同 payload → 冲突 | **已通过（自动化）** | `OPERATION_CONFLICT` |
| 过期 revision 被拒绝 | **已通过（自动化）** | `STALE_REVISION` |
| 事务三个故障点回滚 | **已通过（自动化）** | `SessionStoreTests` / `IdempotencyTests` |
| 强退恢复到最后完整状态，不自动播音 | **已通过（自动化 + 界面）** | `RecoveryTests`、`ReleaseSmokeTests` |
| 提前揭秘被拒 | **已通过（自动化 + 界面）** | `RevealLock`：`本阶段原本不允许揭秘。` |
| 未解锁时界面无真相文字 | **已通过（界面）** | 揭秘前 UI 文本无「已公开的揭秘内容」，揭秘后出现 |
| 论文级 4–6 人真人整局 | **未执行** | 见 US5，需要真人到场 |

## 真实窗口驱动方式

WPF 没有子 HWND，控件通过 Windows UI Automation 定位与读回，用 UIA 的 Invoke / Toggle 模式激活（与屏幕阅读器同一机制）。
驱动脚本：`scripts/drive-ui-session.py`。

```powershell
dotnet build LocalDm.slnx
python scripts/drive-ui-session.py
```

执行后生成 `ui-session-steps.json` 与 `00-*.png` … `11-*.png`。

## 步骤与阶段对照（实际输出）

| 步骤 | 操作后的界面状态 |
|---|---|
| 主持台 | （导航） |
| 装载演示剧本 | 阅本 · Prepared · revision 0 |
| 开始这一局（未勾选准备） | 阅本 · Prepared · revision 0（**被拒**，提示请先确认大家已口头准备好） |
| 勾选准备 → 开始这一局 | 阅本 · Playing · revision 1 |
| 下一环节 | 搜证 · Playing · revision 2 |
| 发放一个材料编号 | 搜证 · Playing · revision 2（已发放：线索包一、线索包二） |
| 下一环节 | 讨论 · Playing · revision 3 |
| 下一环节 | 投票 · Playing · revision 4 |
| 录入 A=3, B=2 + 勾选汇总确认 | 投票 · Playing · revision 4 |
| 提交票数汇总 | 投票 · Playing · revision 5 |
| 下一环节 | 真相 · Playing · revision 6 |
| 勾选揭秘确认 → 揭秘 | 真相 · Revealed · revision 7 |
| 进入复盘 | 真相 · Completed · revision 8 |

截图：`02-start-without-groupready.png`（被拒）、`05-material.png`（材料编号）、`08-votes.png`（票数已录）、`10-revealed.png`（揭秘后）、`11-completed.png`（复盘）。

## 自动化用例统计

```powershell
dotnet test tests\LocalDm.Tests\LocalDm.Tests.csproj
dotnet test tests\LocalDm.UiTests\LocalDm.UiTests.csproj
```

- `LocalDm.Tests`：90 项通过
- `LocalDm.UiTests`：2 项通过（真实 MainWindow + 真实 SQLite + 真实组合根）

## 限制

- 演示剧本是合成的，用于验证状态机与界面门禁；**不等于**真实剧本一局。
- 真实 4–6 人整局未执行（缺少真人玩家，且真实剧本因 MinerU 引擎受阻尚未准备）。
- 界面截图来自 100% 缩放；125% / 150% 需要人工复核。
