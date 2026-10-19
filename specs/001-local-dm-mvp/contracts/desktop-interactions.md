# Desktop interaction contract

## Surfaces

Library 选择冻结本及继续游戏；Setup 检查外部工具、模型与音色；Import/Readiness 只展示脱敏准备状态；Host 仅接收公共投影。
Host 的 TextBox 保留原生 IME 行为，支持 OpenLess 插入或剪贴板后手动粘贴，不提供采音 API。
界面保留发送、停止、重播、下一环节、暂停保存和复盘入口，只有当前允许的动作可用。

## Text submit

回车发送，但 WPF IME 正在组合、ImeProcessed 或候选确认时不得误发；Shift+Enter 为换行，按钮发送始终可用。
文本提交后不再弹普通转写确认。程序不根据输入停止若干毫秒自动发送，不代用户触发 OpenLess 全局快捷键。
普通问题走 PublicHostRequest；状态意图只打开确认视图。打开对话框不提交状态。

## Confirmations

Start/Advance 明确勾选大家已口头准备好，并列出无剧透的原本条件。
SubmitVoteSummary 显示录入汇总和确认；开局须接受软件不提供电子秘密投票。
Reveal 确认是公共揭秘且满足当前原本条件；普通模型回复不能触发它。

## Cancellation

Stop 取消当前 HttpClient token、使 requestId/generationId 失效并取消 TTS 队列。
晚到回复既不显示也不播音；取消不承诺上游停止计费或计算。
Replay 只播放当前已批准并保存的口播，不请求模型、不写状态。

## Failures and privacy

无模型或无中文声音显示实际缺项，保留文字和存档；不能以样例假装成功。
OpenLess 不可用仍可手动输入；应用不读取它的配置，也不声称其 ASR 离线。
界面、错误和日志只呈现脱敏信息；验证至少覆盖 125% 和 150% 缩放以及目标 Windows 输入方式。
