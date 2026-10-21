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

## Tablet distribution entry

主持台提供「开启平板分发 / 停止」入口，默认关闭；开启后显示局域网地址、二维码与端口/网卡更换。
连不上时给出去除步骤：同一 Wi-Fi、访客网络隔离、网络类型改为专用、Windows 防火墙对本应用的专用网络放行、端口占用与网卡选择；不自动关闭防火墙。
导入向导在识别后、建包前显示平板材料准备区：公开名称、编号、数量、来源页号与准备状态，不显示卡面、角色本正文、手册或真相。
平板页面只读：没有聊天输入、推进、投票或揭秘按钮；双指缩放与横竖屏不禁用浏览器原生行为。
卡片全部按名称列出，点哪张给哪张，没有按环节解锁；平板浏览器不限定品牌（iPad、安卓平板使用各自设备上可用的浏览器即可，不要求安装 App）。旧游戏状态规则保持不变。
