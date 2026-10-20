# 2026-10-20 代码审查与修复报告

审查对象：工作区未提交改动（`main` 分支，基线提交 `5092691`）。
工具：`alibaba/open-code-review` v1.13.0（deepseek-flash），20 个文件、41 条意见，逐条回源码核对。
处置：**全部修复**（含 1 条经复核确认为误报、不改）。

## 一、误报更正（重要）

审查时我曾判定「票数汇总面板是死代码」：`HostView.xaml` 里的
`{Binding HasVoteSummary}` 没有 `Session.` 前缀，而该属性只存在于 `SessionViewModel`。

**这个判定是错的。** `HostView.xaml:235` 有一层
`<StackPanel DataContext="{Binding Session}">`，阶段面板整块都在这个作用域里，
`HasVoteSummary` / `VoteSummaryDisplay` 解析的正是 `SessionViewModel`。
对照 `HostView.xaml:336` 的 `Session.HasRevealedText`（外层作用域，指向 `HostViewModel`）
可以看出作者对两处作用域的区分是清楚的。**未作任何改动。**

## 二、已修复项

### 秘密隔离与领域规则

| 项 | 位置 | 处置 |
| --- | --- | --- |
| 「已解锁」判定散落 4 处且漏掉暂停态 | `HostNarration.cs` / `PublicSessionProjector.cs` / `ReadinessProjection.cs` | 新增 `TruthAccess.IsUnlocked` / `RevealedTexts` 收口；`Paused && ResumeState==Revealed` 计入已解锁 |
| `ReadinessProjection` 真相提取漏 `Visibility == Truth` 过滤 | `ReadinessProjection.cs:57-64` | 一并收口（既有隐患，顺手修掉） |
| 真相文本提取逻辑在两处逐行重复 | `HostNarration.cs:80,152` | 收口到 `TruthAccess` |
| 角色本/线索卡靠目录名启发式分流 | `RealScriptPackageBuilder.cs:190-192` | 新增 `ExtractedPage.GroupKind`，采信导入源头事实；启发式降为兜底；探针 `BuildPackageProbe` 传入真实来源 |
| 允许手动发放的阶段，线索可能永久丢失 | `GameEngine.cs:102` | 进入时不再整批并入；**离开该阶段时补齐未发放编号** |
| `Complete` 假定「真相的下一阶段就是复盘」 | `GameEngine.cs:233-236` | 只在下一阶段是剧本最后一个阶段时前进，否则保持原阶段 |
| 搜证阶段没识别到线索时悄悄拿掉发放动作 | `RealScriptPackageBuilder.cs:230-232` | 始终保留 `ReleaseMaterials`；`BuildResult` 增加 `MaterialGroupCount` / `RoleGroupCount` 供核对 |
| 「（N 张）」统计的是页数 | `RealScriptPackageBuilder.cs:122` | 改为「（N 页）」 |

### 语音链路

| 项 | 位置 | 处置 |
| --- | --- | --- |
| 崩溃日志写死 `d:\2\dev\dm\crash.log` | `App.xaml.cs`（6 处） | 改到运行数据根目录 `logs/crash.log`；抽 `LogCrash` 统一加锁 |
| 原始异常未脱敏落盘 | `App.xaml.cs:19,23,42,62` | 一律经 `RedactedLog.Redact` |
| Edge 引擎写死开发机路径 | `EdgeTtsEngine.cs:188,210` | 删除；解释器只认 `LOCALDM_PYTHON_PATH` 或 PATH |
| 子进程管道写满 → 永久阻塞 → 播音队列堵死 | `EdgeTtsEngine.cs:99-100` | 读走 stdout/stderr；播音加硬超时（按文本长度 30–600 秒）并杀进程降级 |
| 探测只验 `edge_tts`、不验 `soundfile`/`sounddevice` | `EdgeTtsEngine.cs:64-65` | 探测改为 `import edge_tts, soundfile, sounddevice` |
| 探测超时留下孤儿 python 进程 | 同上 | 超时显式 Kill |
| 探测负结果永久缓存 | 同上 | 5 分钟过期，装好依赖不必重启 |
| 停止后降级把刚停掉的文本再念一遍 | `TtsService.cs:128-129` | 降级前 `ThrowIfCancellationRequested` |
| 本机无中文音色时回退到英文音色念中文 | `TtsService.cs:213` | 回退链只在中文音色里选 |
| 音色枚举在 UI 线程起进程等 3 秒 | `TtsService.cs:25` / `HostViewModel` 构造 | `AppComposition` 启动时后台预热，界面线程只读缓存 |
| 口播文本经命令行传递 | `EdgeTtsEngine.cs:104` | 改走 stdin；脚本侧改读 stdin、float32 解码 |
| Edge 引擎无法在无依赖机器上测 | `EdgeTtsTests.cs` | 抽出 `IEdgeTtsEngine` 契约 + 假引擎；真实合成用例 `Assert.Skip` 显式跳过 |

### 设置与界面

| 项 | 位置 | 处置 |
| --- | --- | --- |
| 同步 `Save` 与 `SaveAsync` 两份重复序列化 | `SettingsStore.cs:141` | 共用 `ToDto`/`Serialize`；加 `SemaphoreSlim` 串行化；临时文件 + `File.Move` 原子替换 |
| settings.json 损坏导致启动失败 | `SettingsStore.cs:100` | 捕获 `JsonException` 退回默认设置 |
| 设置页保存会用旧值覆盖主持台选的音色 | `AppComposition.cs:89-93` / `SetupViewModel` | 只有「用户确实在设置页改过音色」才覆盖；主持台切换后设置页保存会读回磁盘当前值 |
| 设置页改音色后主持台仍用旧音色 | `HostViewModel` | 新增 `ApplySettingsVoice`，`SettingsSaved` 时同步下拉框 |
| 音色写盘异常从属性 setter 逃逸到绑定层 | `HostViewModel.cs:97-100` | 回调返回失败原因，界面给可读提示 |
| 公共屏显示 `ex.Message`（含本机路径） | `HostViewModel.cs:74-77` | 固定文案 + `Debug.WriteLine` |
| 播报期间阶段按钮仍可点 | `SessionViewModel.cs` | 拆分 `_busy` 与 `_narrationBusy`，`Busy` 为二者之和 |
| `Busy` 时静默返回，主持人以为按钮坏了 | `SessionViewModel.cs:251` | 给一句提示 |
| 装载新剧本后主视窗停在空的真相页 | `HostViewModel` | 装载时按状态复位 `ShowTruthView` |
| 任何一次刷新都把视图拽回真相页 | `HostViewModel.OnSessionPropertyChanged` | 只在 false→true 跃迁时自动切换 |
| 音色下拉框绑定回车不查 `CanExecute` | `HostView.xaml.cs:23` | 加 `CanExecute` 判断 |
| `isImeComposing` 恒为 false 的死参数 | `HostInputController.cs:22` | 删除并写明 WPF 语义（`Key == Key.ImeProcessed` 才是正确判据） |
| `TtsVoice.DisplayName` 把展示文案放进领域层 | `ExternalPorts.cs:65` | 移到 App 层 `VoiceOption` / `TtsVoiceDisplay`；补上漏掉的「Microsoft Huihui」 |
| 装载与恢复两段约 20 行重复 | `HostViewModel` | 抽 `AnnounceLoadedSession` |
| `EdgeTtsEngine.IsPlaying`、`HostViewModel._tts` 死代码 | — | 前者删除；后者接上 `RefreshVoices` |
| 复盘时间线列位漂移（Consolas 0.55em vs 半角 0.5em） | `Shared.xaml` | 字体栈改为 `NSimSun, SimSun, monospace`（半角 0.5em / 全角 1.0em，天然的 2:1 栅格） |
| 仓库根目录 6 个未跟踪试音音频 | `.gitignore` | 加规则忽略 |

## 三、复核中发现的自身错误

1. **票数汇总绑定是误报**（见第一节），未改。
2. 首版把 `ApplyVoice` 的回退链改成「只选中文音色」时，一度连 `chosen` 的首选也一起限死，
   会让显式指定的非中文音色失效；最终只在**回退链**上限制中文，显式选择仍然优先。
3. 新的 Edge 测试首轮跑出两个红灯：`Key.ImeProcessed` 实为 155（不是 224），
   以及 `TtsService` 在引擎不可用时仍会调用一次 `TrySpeakAsync`。
   后者说明服务层缺少可用性判断，已补 `&& _edge.IsAvailable()`。

## 四、重新验证

```
dotnet build LocalDm.slnx   → 0 警告 0 错误
dotnet test  LocalDm.Tests   → 123 项全绿（新增 19 项）
dotnet test  LocalDm.UiTests → 2 项全绿
```

新增覆盖：

- 秘密隔离负向：未揭秘时真相/私有文本不得出现在口播依据与 grounding；
  已揭秘时私有文本仍不得出现；暂停在揭秘态时真相保持解锁。
- 阶段规则：离开允许手动发放的阶段时补齐未发材料；下一阶段不允许手动发放时仍自动释放；
  `Complete` 指向非终态阶段时保持原阶段。
- 分流：导入源头标注的 `GroupKind` 优先于目录名启发式；无线索组时仍保留发放动作。
- 语音：Edge 可用/不可用/播失败/本地音色四条路径各自断言走没走 Edge；
  音色列表排序（神经在前、康康在瑶瑶前）；设置双写路径往返不丢字段。
- 输入：`Key.ImeProcessed` 不得触发发送。

## 五、仍未处理（不在本轮审查范围）

来自 `docs/reviews/2026-10-20-acceptance-niedao.md` 的验收项，本轮审查未覆盖：

- **D2（大屏对齐）**：字体栅格已修，但 8 轨时间线的固定列网格、竖线与行首标签
  属于真相文本的数据层改造，未做。
- **D6（真相双份显示 / Markdown 语法直接上屏）**：未做。

本报告只记录代码事实与验证结果，不表示真人与实机验收已完成。
