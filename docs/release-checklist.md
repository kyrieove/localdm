# 发布核对表

更新日期 2026-10-19。本文件把 spec 的 SC-001–SC-006 映射到实际证据。
**不要**把「自动化通过」当成「完整交付」；真人整局与真实剧本准备仍未完成。

## 成功标准映射

| 标准 | 要求 | 当前状态 | 证据 |
|---|---|---|---|
| SC-001 | 目标 Windows 用户可安装并双击启动，两种发布物都通过实际启动检查 | **已通过（本机）** | `docs/evidence/us5/install-portable.md`；`portable-smoke.json` |
| SC-002 | 合成输入页覆盖率与关键事实来源覆盖率 100%；缺页/冲突/未续读/来源失配四类被拦截 | **已通过（合成）** | `docs/evidence/us2/tool-parse.md`；`ImportCoverageTests` |
| SC-003 | 自动化投影测试与公共窗口检查中未解锁秘密出现 0 次 | **已通过** | `PhaseFlowTests`；`docs/evidence/us3/flow-recovery.md`（揭秘前无真相文字） |
| SC-004 | 同操作 10 次只迁移一次；过期 revision 被拒；两个故障点恢复 | **已通过** | `IdempotencyTests`、`RecoveryTests`、`FaultMatrixTests` |
| SC-005 | 实机 OpenLess 中文输入与 20 次发送/取消/重播无丢字、无重复、无重叠播音；麦克风访问 0 | **部分通过** | 长中文与分流已验证（`docs/evidence/us4/openless-input.md`）；**真实语音落字与 20 次压力未执行** |
| SC-006 | 获准真实剧本由真实 4–6 人从开局玩到复盘 | **未执行** | 需要真人到场；真实剧本因 MinerU 引擎受阻未 Ready |

## 关键门禁

| 门禁 | 状态 | 说明 |
|---|---|---|
| 真实剧本准备为 Ready | **受阻** | 本机 MinerU 无 basic/standard/advanced 引擎（`quality_tier_unavailable`），剧本全部为图片页 |
| 真实 4–6 人整局 | **未执行** | 无真人玩家、无 Ready 包 |
| 代码签名 | **未做** | SmartScreen 可能提示；不要求绕过 |
| 125% / 150% 缩放实测 | **未执行** | 需要前台 GUI 切换系统缩放 |

## 结论

本版本可以：

- 在无开发环境的 Windows x64 上安装/便携运行；
- 用演示剧本走完一局确定性流程，重复点击、强退都能正确恢复；
- 用本机 Magpie + 中文 TTS 完成公开口播，取消与重播行为正确；
- 接受中文长句输入，且状态意图不越过确认。

本版本**不能**声称：

- 已用真实剧本完成一局；
- 已完成真人 4–6 人验收；
- 已证明 OCR 解析质量（引擎不可用）。

## 发布前必做（由用户决定）

1. 为目标 Windows 配置 MinerU 本地解析引擎，重新跑 `docs/evidence/us2/tool-parse.md` 里的命令；
2. 准备真实剧本并逐项核对来源；
3. 找 4–6 名真人完成一次整局，记录到 `docs/evidence/us5/real-tabletop.md`；
4. 在 125% / 150% 缩放下复核界面；
5. 如需正式分发，考虑代码签名。
