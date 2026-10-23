# HANDOFF · 2026-10-23 · 主持台语音按钮（本地识别）

## 现在
- 已验证：平板入口改造全部完成并推送（`d212ea2`），启动器 `启动本局测试.cmd` 会自动重新发布最新版。
- 语音按钮：设计已经和用户逐条定完，写进 `docs/decisions/0006-in-app-dictation-local-asr.md`。引擎卡 `docs/orch/2026-10-23-dictation-engine/card.md` 已派给 agy（后台任务名 `dictation-engine`，worktree 隔离，看汇报：`~/.claude/agent-live/orch-runner/dictation-engine.json`）。
- 未验证：Qwen3-ASR 在本机 C# 里真的能识别、速度和准确率（引擎卡完成后跑 `dotnet run --project tools/LocalDm.Probe -- --dictation-check`，把结果写进 `docs/evidence/us4/` 新文件）。

## 下一步
1. 看引擎卡结果：`ORCH-RUNNER dictation-engine outcome=…`；review_fail 先核对每条 finding 的 file:line，小改自己修，再 `run-card.mjs --recheck --name dictation-engine`，然后 `--commit … --accept accepted --findings …`（流程见 `D:/2/dev/orch-runner/orchestrate/SKILL.md`）。
2. 引擎合入后写并派**界面卡** `docs/orch/2026-10-23-dictation-ui/card.md`（agy，medium）：
   - `src/LocalDm.App/ViewModels/HostViewModel.cs`：构造函数加 `IDictationService? dictation = null`；加 `ToggleDictationCommand`、`IsListening`/`DictationHint`（「正在听…」「正在准备语音…」+ 进度）；点开始时先 `_tts?.Stop()`；开始失败把返回文案当提示显示；结束后把文字追加进 `InputText`（已有内容后加一个空格或换行），不发送；VM 自己用 `DispatcherTimer` 60 秒后自动结束。
   - `src/LocalDm.App/Views/HostView.xaml`（输入框 Grid 的按钮区，约第 430–450 行，发送/停止按钮同一格）：加麦克风按钮，`AutomationProperties.AutomationId="DictationButton"`，录音时变印章红并显示「正在听…」；`HostView.xaml.cs` 在 `OnInputPreviewKeyDown` 旁加窗口级 `PreviewKeyDown`：右 Alt（`e.Key == Key.System ? e.SystemKey : e.Key` 等于 `Key.RightAlt`）触发同一命令，`e.Handled = true`，仅本窗口生效。
   - `src/LocalDm.App/AppComposition.cs`：`new DictationService(new DictationModelStore(_context.DataRoot))` 传给 `HostViewModel`，`Dispose` 里释放。
   - UI 测试用假 `IDictationService`（不碰真麦克风）：点按钮→开始→再点→文字进输入框且没发送；开始时朗读被停止；右 Alt 触发；失败文案显示。
3. 再写并派**文档卡**（low）：`PRODUCT.md` 与 `docs/user-guide.md` 去掉「不碰麦克风」改成 ADR 0006 第 8 条；宪章 `.specify/memory/constitution.md` 升版本号并记这条；第三方许可说明（`packaging/windows/THIRD-PARTY-NOTICES.txt`）补 sherpa-onnx、NAudio、Qwen3-ASR（Apache-2.0）。
4. 全部合入后：`dotnet build LocalDm.slnx`、两个测试项目全过 → 用启动器实机点一次麦克风（模型复制约 0.9 GB，首次要等）→ 提交推送。

## 先读
- `docs/decisions/0006-in-app-dictation-local-asr.md`（决定与否掉的方案）
- `docs/orch/2026-10-23-dictation-engine/card.md`
- 平板改造的卡与做法可参考 `docs/orch/2026-10-23-tablet-*/`；`docs/project-state.md` 末尾
- 记忆：`tablet-entry-done-2026-10-23`（含用户偏好：按推荐直接推进，大改动先出 HTML 原型）

## 坑
- 卡片 lint：Don't 段必须有「Reuse existing functions」；`read-only` 不能列会被构建改动的目录（`src/LocalDm.Tablet/` 之类会触发 readonly_violated）。
- 一条 Bash 命令里同时放多个大 heredoc 容易解析失败：卡片用 Write 工具写。
- 并行整套测试偶发 `EdgeTtsTests` 探针超时、`HostConversationTests.Replay_holds_the_session_busy_gate`，单跑稳定通过。
- OpenLess 源码是 AGPL-3.0：不能复制它的代码。

## 等用户回答
- 晚上用户回来要**导入新剧本**：整版多卡剧本导入时还没有逐组确认版式的界面（见 `docs/project-state.md`）。回来先问新剧本是不是整版多卡。
