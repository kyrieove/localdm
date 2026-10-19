# US4 OpenLess 中文输入：实测记录

更新日期 2026-10-19。使用真实 `LocalDm.exe` 窗口与真实 Windows UI Automation。

## 结论

| 场景 | 结论 | 证据 |
|---|---|---|
| 长中文文本（含标点、角色名）写入输入框 | **已通过** | 47 字符，读回逐字符一致 |
| 清空后重新写入，不重复、不丢字 | **已通过** | `replace-and-retype` identical=true |
| 状态意图只产生待确认提案，不改阶段 | **已通过** | 提示「这只是待确认提案…」，阶段与 revision 未变 |
| 索取秘密只得到安全回应 | **已通过** | 回应「这个问题涉及尚未公开的内容，我不能回答。」；界面无真相文字 |
| OpenLess 进程存在 | **已确认运行** | `openless` 进程在跑（`OpenLess Capsule Mirror` 窗口） |
| 回车/Shift+Enter/IME 组合判定 | **已通过（自动化）** | `TextSubmissionTests` 6 项 |
| 20 次发送/取消/重播压力 | **未执行** | 见限制 |
| 真实 OpenLess 语音转写落字 | **未执行** | 需要麦克风与人工说话 |

## 命令

```powershell
python scripts/verify-openless-input.py
```

输出：`openless-input-steps.json` + `01-input-long-text.png`、`02-state-intent-proposal.png`、`03-secret-probe.png`。

## 实际结果（节选）

```json
{
  "case": "long-chinese-text",
  "expectedLength": 47,
  "actualLength": 47,
  "identical": true,
  "text": "请主持人说明一下：唐这个角色现在可以查看哪些线索，我们想先核对灯塔值班记录，然后再决定下一步。"
}
```

```json
{
  "case": "state-intent-proposal-only",
  "proposalHint": "这只是待确认提案：需要在阶段面板里手动确认后才会提交。"
}
```

```json
{
  "case": "secret-probe",
  "answer": "这个问题涉及尚未公开的内容，我不能回答。请按当前环节继续。",
  "leaked": false
}
```

## 实现方式说明

- 本软件不调用麦克风、不模拟 OpenLess 全局快捷键、不读取 OpenLess 配置（见 `contracts/integrations.md`）。
- 外部转写工具把文字落到聚焦文本框后，回车或「发送」按钮提交；`Shift+Enter` 换行；IME 组合态下的 Enter 不发送。
- 应用侧判定函数 `HostInputController.ShouldSendOnEnterArgs` 有独立自动化用例，覆盖组合态与 Shift。

## 限制（未执行项）

- **没有真实 OpenLess 语音转写落字记录**：需要人对着麦克风说话；本轮无法录音，因此只验证了「文字进入输入框之后」的路径。
- **20 次发送/取消/重播压力未执行**：发送需要真实 Magpie 生成与真实播音，反复 20 次会产生大量长时间播音与外部模型调用，本轮没有做。
  取消/晚到结果丢弃的行为已由 `SpeechLifecycleTests` 覆盖。
- **麦克风访问为 0**：应用没有录音代码路径，但本轮没有用系统「隐私 → 麦克风」记录做截图核对。
