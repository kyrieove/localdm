# 提示词驱动的主持播报

更新日期 2026-10-19。本文件不含剧本正文。

## 结论

**每个主持按钮现在都是一条提示词，不是一句写死的话。**

| 按钮 | 交给模型的意图 | 实测输出（真实模型） |
|---|---|---|
| 公共开场 | 开场，请开始阅本 | 「欢迎大家来到《<真实剧本>》，本作是虚构故事…仅限18岁以上玩家参与。本次游戏约4—5小时，现在请大家开始阅读自己的角色本。」 |
| 开始这一局 | 宣布进入阅本并说明要做什么 | 「各位已经拿到自己的角色本了，现在进入阅本环节…读完后示意我一声。」 |
| 下一环节 | 宣布进入新环节 | 「现在我们进入「开场说明」环节…本作含谋杀、阴谋等内容，仅限18岁以上人士参与…」 |
| 发送并朗读 | 回答玩家原话 | 「这个环节具体还要多久，目前公开信息里没有说明，我不能替流程下判断。」 |

最后一条是关键：模型**没有编造**时长，而是说公开信息里没有。这正是「只给公开上下文」的效果。

## 数据流

```text
点按钮
  ↓
程序裁决状态（revision+1、阶段前移）——模型无权参与
  ↓
拼提示词：意图 + 环节名 + 原本条件 + 只含 Public 的原文
  ↓
POST /v1/chat/completions（stream:false）
  ↓
取 choices[0].message.content；空内容或坏 JSON → 不朗读
  ↓
TTS 朗读
```

## 秘密隔离在哪一层

隔离发生在**拼提示词**那一刻，不发生在检查模型输出之后。

- `HostNarration.BuildMessages` / `BuildAskMessages` 只取 `Visibility == Public` 的段；
- 角色本（Private）与真相（Truth）在查询阶段就被过滤，不进入字符串；
- 模型从未见到秘密，因此不需要事后审查它的输出。

## 为什么不再审查模型输出

产品决定：**回什么就念什么。**

理由：自然语言没有可建立的标准。要求「逐字回指原文」会把主持说成套话，而用词表拦又拦不住改写。
真正的硬约束在上下文构造处，那里能明确判断一条段的可见性。

被删除的代码：`PublicReplyPolicy`、`PublicReplyDecision`、`PublicReplyMode`、`ApprovedTransition`
（连同已无人使用的过渡句表）。`tests/LocalDm.Tests/HostUncertaintyTests.cs` 改为断言
「提示词里不含私有段与真相段」——这是现在真正的安全边界。

## 验证

```powershell
# 单元与界面测试
dotnet test tests/LocalDm.Tests/LocalDm.Tests.csproj      # 98 通过
dotnet test tests/LocalDm.UiTests/LocalDm.UiTests.csproj  # 2 通过

# 真实模型实测
python scripts/_final-check.py
```

真实模型单轮约 30 秒（含真实播音时长）。见 `ui-real-narration.png`、`ui-real-flow.png`、`ui-real-ask.png`。

## 已知限制

- 模型延迟明显（10–30 秒），主持人点完按钮要等一下才出声；没有做流式，因为要先落盘状态再播音。
- 播报失败只显示状态文字，不重试；主持人可以再点一次。
- 停止按钮会丢弃晚到的模型结果（按 requestId 判断），已测。
- 真人整局仍未执行。
