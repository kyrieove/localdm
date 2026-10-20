# 对话界面第二版：独立验收

日期：2026-10-20。结论：**未通过完整验收，需要返修。** 对话界面和演示主线已经实现，但动态流程、冻结包身份和重播链路存在实质缺陷。

本轮没有修改产品代码或真实存档。比较基准为改版前源码快照 `.archify/architecture-localdm-20261020-113156/source-snapshot/src/`；当前工作区包含更早的未提交工作，因此不能把整个 HEAD 差异都归因于本次改版。

## 独立验证结果

| 检查 | 本轮实际结果 |
| --- | --- |
| `dotnet build LocalDm.slnx --no-restore` | 通过，0 警告、0 错误 |
| `dotnet test LocalDm.slnx --no-build` | 总数 170，169 通过、0 失败、1 跳过 |
| 跳过项目 | `EdgeTtsTests.Real_edge_engine_speaks_when_environment_supports_it`。当前进程使用的 Python 没有完整 Edge-TTS 依赖；不能把这轮说成 170 项全部实际通过 |
| 真实窗口 | 基于原 UIA 脚本制作隔离副本，将找不到按钮、不可用按钮、无法写入输入框改为立即失败；使用临时数据根，走完演示开局、搜证、两份材料、设置、投票、揭秘 |
| 独立数据库断言 | `state=3`、`current_phase_id=d-revealp`、`revision=9`、两份材料、A=3/B=2、事件和回执均为 9，全部通过 |
| 界面查看 | 实际检查本轮生成的 100% 搜证页与副屏窄窗口截图。对话主区、左侧动作、宽幅复盘已存在；完整 DPI 可用性不能只由脚本退出码判定 |
| 真实语音可听性 | 本轮未以人耳验收；测试结果不证明实际出声 |

本轮 UIA 副本位于 `.appdata/diagnostics/run-v2-live-review.ps1`，执行命令为：

```powershell
pwsh -NoProfile -File .appdata/diagnostics/run-v2-live-review.ps1 -OutDir D:/2/dev/dm/.appdata/diagnostics/acceptance-v2-ui
```

截图和独立数据库断言保存在该输出目录。原 `scripts/verify-live-window.ps1` 在部分按钮缺失时会返回 false 而继续执行，不能仅凭它输出“完成”判定全部操作成功。本轮隔离副本补充了失败即停止与最终数据库断言，未修改原脚本。

## 必须返修的发现

### 1. P1：更改流程会覆盖旧冻结包，旧存档仍接受新流程

位置：`src/LocalDm.Core/Import/RealScriptPackageBuilder.cs:209`、`tools/LocalDm.Probe/BuildPackageProbe.cs:124`。

`PackageId` 与 `ManifestHash` 仍只由原始来源决定，`Version` 固定为 `1`。同一批原始材料生成不同流程时，三项身份完全相同；准备工具写入同一个文件名。旧会话的绑定检查通过，无法发现流程被改写。实测将两步流程改为含新增搜证的三步流程后，旧会话仍停在结局 ID，并被新包接受，跳过新增步骤。

修复要求：将规范化流程及影响行为的配置纳入冻结内容身份，产生新版本或新内容标识并保留旧文件。旧会话继续绑定原冻结包；没有原包时明确拒绝恢复，不能静默接受变更。

### 2. P2：错误材料和公开段落引用被静默删除，包仍可开局

位置：`src/LocalDm.Core/Import/RealScriptPackageBuilder.cs:354`、同文件 `:434`。

准备器对未知材料编号和段落编号直接过滤，后续就绪检查仅确认流程标题与来源文字。实测声明材料 `TYPO-A01`、`RequiresAllMaterials=true`、错误公开段落编号后，包仍为 Ready，材料和公开段落集合都变空，开局后可直接进入揭秘阶段。

修复要求：未知引用必须生成指出具体 ID 的阻塞问题，整包保持 NeedsReview；不能通过删掉要求来让流程合法。

### 3. P2：重复环节 ID 生成 Ready 包，推进会循环停在原步骤

位置：`src/LocalDm.Core/Domain/ScriptFlow.cs:15`、`src/LocalDm.Core/Game/GameEngine.cs:272`。

`IsFullyDeclared` 不验证步骤 ID 唯一性。实测前两步使用相同 ID `same`，两次推进后仍在 `same`，revision 增加至 3，无法到达结局。

修复要求：准备阶段验证 ID 非空且唯一，对重复 ID 给出明确阻塞问题，并验证所有动作引用有效。

### 4. P2：第二轮投票沿用第一轮票数，无需再次提交

位置：`src/LocalDm.Core/Game/GameEngine.cs:77`、同文件 `:124`、`:179`。

`VoteSummary` 是会话级单值，推进时不清除或关联到投票环节。实测 `vote-one → vote-two → end`，只提交第一轮汇总后，第二轮仍可直接推进到结局。

修复要求：汇总必须属于明确的投票阶段。进入新的投票阶段时要求新的提交，同时保留必要历史；若本轮不支持多轮投票，应在准备时拒绝该流程，而不是接受后跳过。

### 5. P2：重播期间仍可推进，下一阶段的播报被丢弃

位置：`src/LocalDm.App/ViewModels/HostViewModel.cs:882`。

`ReplayMessageAsync` 只设置 Host 的 `IsSpeaking`，没有占用 Session 的播报忙碌状态。实测保持重播 TTS 未完成时，`Host.IsBusy=True`，但 `Session.Busy=False`、`CanAdvance=True`；点击推进使 revision 从 1 变成 2。新的阶段播报检测 Host 忙碌后直接退出，模型调用次数为 0。

修复要求：重播开始、完成、停止和异常路径同步维护 Session 忙碌状态，防止旧音频播放期间执行冲突动作，也不能静默吞掉阶段播报。

### 6. P2：重播失败仍显示“已重播”

位置：`src/LocalDm.Core/Hosting/HostDirector.cs:270`、`src/LocalDm.App/ViewModels/HostViewModel.cs:895`。

`ReplayItemAsync` 的失败分支返回非空 Item；界面仅依据 Item 非空判为成功。独立注入 `TTS_UNAVAILABLE` 后，实际显示 `SpeechState=Spoken`、提示“已重播（未请求模型）”，活动错误状态为 false。用户会再次遇到“没有声音却报告完成”。

修复要求：依据结果状态判定成功，或失败时不返回成功 Item。分别覆盖失败和取消，并确保已生成文字仍然可见。

### 7. P2：揭秘自动切页后，停止播报入口不可见

位置：`src/LocalDm.App/ViewModels/HostViewModel.cs:1009`、`src/LocalDm.App/Views/HostView.xaml:350`、同文件 `:407`。

这是代码路径核对结论，未进行真实长语音的声学复现。揭秘后会自动设置 `ShowMaterials=true`，但唯一停止按钮在只对 `ShowConversation` 可见的输入区中；切页后该祖先容器折叠。查看公开材料或进入设置时也有同类问题。

修复要求：生成或播音期间，在所有可切换页面保留可达的停止操作；增加“揭秘自动切页且 TTS 未完成”时停止按钮可见、可操作的真实控件测试。

## 独立合成复现

以下复现使用当前真实业务类，模型与 TTS 仅用于精确控制外部响应。没有替换规则核心、准备器或会话存储，没有使用真实剧本正文。

```powershell
dotnet run --project .appdata/diagnostics/review-v2-core/review.csproj --no-restore
```

本轮实际输出：

```text
MISSING readiness=Ready issues= materialIds=0 publicIds=0 advanced=reveal
IDENTITY sameId=True sameVersion=True sameHash=True oldSessionPhase=end newIndex=2 bindingsAccepted=true
DUPLICATE readiness=Ready repeatedAdvancePhase=same revision=3
VOTE readiness=Ready advancedPastSecondVoteWithoutSubmission=True
```

```powershell
dotnet run --project .appdata/diagnostics/review-v2-app/Review.csproj --no-restore
```

本轮实际输出：

```text
HELD_REPLAY host.IsBusy=True; session.Busy=False; CanAdvance=True; rev=1
DURING_REPLAY rev=2; advanced=True; modelNarrationCalls=0
FAILED_REPLAY speechState=Spoken; note=已重播（未请求模型）; activityError=False
```

这些输出是缺陷复现，不是通过验收的测试结果。修复后需要将对应期望写入正式回归测试。

## 额外的需求缺口

`HostViewModel.cs:241` 的 `ShowOpeningSpeech` 仍按 `CurrentPhase.Index == 1` 决定开场按钮显示。这是固定阶段序号，不能适应“序章就是第一步”的流程；应由流程声明控制对应动作。此项已静态确认，但不计入上述六项独立运行复现。

`HostNarration` 的两条上下文构造路径没有使用新 `ScriptPhase.Hint`。若某环节仅提供 Hint 而 Conditions 和公开段落为空，界面有提示、模型却没有这段依据。需明确哪些流程提示属于可传模型的公开依据，并补相应测试；不应把未知可见性的文字直接外发。

## 返修后的复验范围

先修冻结包身份与流程声明校验，再修投票阶段归属和重播生命周期，最后补所有页面停止入口与数据驱动开场动作。必须重跑同一批合成反例、整套测试和隔离真实窗口流程。包装发布与真实人耳可听性仍需单独验收，不能用本次演示窗口或假 TTS 证明。
