# 任务：/compact 在对话太短时提示写错

现象：只聊两三轮就输入 `/compact` 或点"压缩上下文"，状态栏提示"最近 2 轮之前的对话都整理过了，这次没有需要整理的内容。"，但这局其实从来没整理过。

位置：`src/LocalDm.App/ViewModels/HostViewModel.cs` 的 `CompactAsync`，`manual && keepFromIndex == 0` 分支。

要求：
- 本局从没整理过（备忘为空、已整理次数为 0）时，提示改为"对话还太短，暂时没有需要整理的内容。"
- 已经整理过、又没有新内容可整理时，保留原提示。
- 两种情况都不发模型请求（保持现状）。
- 补测试覆盖这两种情况，放在已有的相关测试文件里（`tests/LocalDm.Tests` 或 `tests/LocalDm.UiTests`，跟着现有压缩测试放）。

只改这一项。
