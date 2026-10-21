# 任务："继续一局"退回演示剧本时对话记忆不存档

现象：软件刚打开、还没装载剧本就点"继续一局"，会退回演示剧本；这条路径下 AI 主持的对话记忆（`DmConversation`）不会存档，重启后接不上。

相关位置：`src/LocalDm.App/ViewModels/LibraryViewModel.cs`（`ContinueCommand`、`ContinueRequested`），`src/LocalDm.App/ViewModels/HostViewModel.cs`（约第 598、701 行附近的"继续一局"逻辑），`src/LocalDm.Infrastructure/Storage/SessionStore.cs`（`dm_conversations`）。

要求：
- 先查清为什么这条路径不存档，在最终小结里写明原因。
- 修好后，这条路径和正常装载剧本时一样保存、恢复对话记忆；不改变正常路径的行为。
- 如果正确的做法其实是"没装载剧本时不该退回演示剧本"，这是产品决定，不要自己改：写 OVERNIGHT-QUESTION.md 停下。
- 补测试：没装载剧本直接继续，聊一轮，重新加载后记忆还在。
- 不要改 `CompactAsync` 及其附近的压缩提示代码（另一个助手在改）。
