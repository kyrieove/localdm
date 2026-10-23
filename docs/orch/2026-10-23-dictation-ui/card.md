# dictation-ui — 主持台输入框加麦克风按钮与右 Alt，接 IDictationService

## Goal
- 主持台输入框右侧有麦克风按钮（`AutomationId=DictationButton`）：点一下开始听、再点一下结束；录音时按钮变印章红、输入框下方提示「正在听…」；结束后识别出的文字追加进输入框（已有内容后加一个空格），**不自动发送**。
- 右 Alt 触发同一动作，只在主持台所在窗口在最前面时有效；60 秒自动结束。
- 点开始时先停掉当前朗读；开始失败或识别失败时把服务给的文案显示在提示位；模型准备进度显示在提示位。
- `AppComposition` 里真正接上 `DictationService`；有界面测试覆盖（用假的 `IDictationService`，不碰真麦克风）。

## Risk
medium

## Parameters
- 自动结束上限：`HostViewModel.DictationLimit`（`TimeSpan`，公开可写，默认 60 秒）。
- 快捷键：右 Alt（`Key.RightAlt`；WPF 里 Alt 是系统键，要用 `e.Key == Key.System ? e.SystemKey : e.Key` 取真键）。
- 提示文案（原样）：录音中「正在听…」；停止后等识别「正在识别…」；其余文案一律用 `IDictationService` 返回的原话（`StartAsync` 返回的字符串、`DictationResult.Problem`、`Progress` 事件的文字）。
- 按钮图标：字体 `Segoe MDL2 Assets`，字符 `&#xE720;`；录音时背景 `Seal`，平时 `PaperDim`、边框 `Silver`。
- `ToolSettings.DefaultInputHint` 新值：`"回车发送，Shift+Enter 换行；点右侧麦克风或按右 Alt 语音输入。"`。
- 前置：引擎已合入（`IDictationService`、`DictationResult`、`DictationState` 在 `LocalDm.Core.Ports`；`DictationService`、`DictationModelStore` 在 `LocalDm.Infrastructure.Voice.Dictation`；接口定义以仓库里的 `src/LocalDm.Core/Ports/IDictationService.cs` 为准）。

## Files
may change: `src/LocalDm.App/ViewModels/HostViewModel.cs`, `src/LocalDm.App/Views/HostView.xaml`, `src/LocalDm.App/Views/HostView.xaml.cs`, `src/LocalDm.App/AppComposition.cs`, `src/LocalDm.Core/Domain/ToolSettings.cs`, `tests/LocalDm.UiTests/DictationUiTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 6)
1. `src/LocalDm.Core/Domain/ToolSettings.cs` 第 69 行：把 `DefaultInputHint` 的字符串换成 Parameters 里的新值（`grep -rn "聚焦本框" src tests` 确认没有别处依赖旧文字）。
2. `src/LocalDm.App/ViewModels/HostViewModel.cs`：
   - 构造函数末尾加可选参数 `IDictationService? dictation = null`（排在 `onVoiceChanged` 之后），存到 `private readonly IDictationService? _dictation;`。若 `dictation` 非空，订阅 `dictation.Progress += text => DictationHint = text;`（该事件只在首次复制模型时出现）。
   - 新增：`public bool DictationAvailable => _dictation is not null;`；`public bool IsListening { get; private set; }`（用 `SetProperty` 写法，和文件里其它属性一致）；`public string DictationHint { get; private set; } = "";`（同样走 `SetProperty`，并在变化时通知 `HasDictationHint`）；`public bool HasDictationHint => DictationHint.Length > 0;`；`public TimeSpan DictationLimit { get; set; } = TimeSpan.FromSeconds(60);`；`public RelayCommand ToggleDictationCommand { get; }`，在构造函数里 `new RelayCommand(ToggleDictationAsync, () => _dictation is not null, allowReentry: true)`。
   - 私有 `bool _dictationBusy`（开始准备中或识别中为 true）与 `DispatcherTimer? _dictationTimer`。
   - `private async Task ToggleDictationAsync()`：`_dictation` 为空或 `_dictationBusy` 直接返回。
     - 不在听（`!IsListening`）：`_tts?.Stop();` `DictationHint = ""`；`_dictationBusy = true`；`var problem = await _dictation.StartAsync(CancellationToken.None);` `_dictationBusy = false`；`problem` 非空 → `DictationHint = problem` 并返回；否则 `IsListening = true`，`DictationHint = "正在听…"`，启动 `_dictationTimer = new DispatcherTimer { Interval = DictationLimit }`，`Tick` 里 `_ = StopDictationAsync();`。
     - 在听：`await StopDictationAsync();`
   - `private async Task StopDictationAsync()`：`IsListening` 为 false 直接返回（防止计时器与点击同时触发）；停掉并清空 `_dictationTimer`；`IsListening = false`；`_dictationBusy = true`；`DictationHint = "正在识别…"`；`var result = await _dictation!.StopAsync(CancellationToken.None);` `_dictationBusy = false`；`result.Text` 非空 → `InputText = string.IsNullOrWhiteSpace(InputText) ? result.Text : InputText.TrimEnd() + " " + result.Text;` 并 `DictationHint = ""`；否则 `DictationHint = result.Problem ?? ""`。
   - `Dispose()` 里：停掉 `_dictationTimer`（置空）。不要在这里释放 `_dictation`（由 `AppComposition` 释放）。
3. `src/LocalDm.App/Views/HostView.xaml`（输入框下面那个 `Grid Margin="0,8,0,0"`，约第 424–446 行）：
   - 列定义改成三列：`*`、`Auto`、`Auto`；原来 `Grid.Column="1"` 的 `StopButton`、`SendButton` 都改为 `Grid.Column="2"`。
   - 在它们之前加麦克风按钮 `x:Name="DictationButton"`，`Grid.Column="1"`，`Style="{StaticResource SendButton}"`，`Margin="0,0,8,0"`，`FontFamily="Segoe MDL2 Assets"`，`Content="&#xE720;"`，`Command="{Binding ToggleDictationCommand}"`，`AutomationProperties.AutomationId="DictationButton"`，`ToolTip="语音输入（右 Alt）"`，`Background="{StaticResource PaperDim}"`，`BorderBrush="{StaticResource Silver}"`，`Foreground="{StaticResource Ink}"`，`Visibility="{Binding DictationAvailable, Converter={StaticResource BoolToVisibility}}"`；再用 `Button.Style` 内的 `DataTrigger Binding="{Binding IsListening}" Value="True"` 把 `Background`、`BorderBrush` 设为 `{StaticResource Seal}`、`Foreground` 设为白色（做法参照同文件里已有的 `DataTrigger` 写法；若 `Style` 属性与 `Style="{StaticResource ...}"` 冲突，改用 `BasedOn="{StaticResource SendButton}"` 的内联 Style）。
   - 左侧 `StackPanel`（放 `ComposerHint` 那个）里，`ComposerHint` 那行之后加一个 `TextBlock`：`Text="{Binding DictationHint}"`，`Style="{StaticResource ChatMeta}"`，`Margin="12,0,0,0"`，`Foreground="{StaticResource Seal}"`，`VerticalAlignment="Center"`，`AutomationProperties.AutomationId="DictationHint"`，`Visibility="{Binding HasDictationHint, Converter={StaticResource BoolToVisibility}}"`。
4. `src/LocalDm.App/Views/HostView.xaml.cs`：
   - 构造函数里加 `Loaded += (_, _) => AttachWindowKeys();`，`Unloaded` 已有的 lambda 里同时调用 `DetachWindowKeys()`。
   - 字段 `private Window? _window;`。`AttachWindowKeys()`：`_window = Window.GetWindow(this);` 非空则 `_window.PreviewKeyDown += OnWindowPreviewKeyDown; _window.PreviewKeyUp += OnWindowPreviewKeyUp;`。`DetachWindowKeys()` 反向并置空。
   - `private static bool IsRightAlt(KeyEventArgs e) => (e.Key == Key.System ? e.SystemKey : e.Key) == Key.RightAlt;`
   - `OnWindowPreviewKeyDown`：`IsRightAlt(e)` 且 `!e.IsRepeat` 且 `DataContext is HostViewModel vm` 且 `vm.ToggleDictationCommand.CanExecute(null)` → `e.Handled = true; vm.ToggleDictationCommand.Execute(null);`；`IsRightAlt(e)` 但是是重复按键 → 只 `e.Handled = true`。
   - `OnWindowPreviewKeyUp`：`IsRightAlt(e)` → `e.Handled = true`（防止 Alt 抬起去激活系统菜单）。
5. `src/LocalDm.App/AppComposition.cs`：加字段 `private IDictationService? _dictation;`（`using LocalDm.Core.Ports;` 与 `using LocalDm.Infrastructure.Voice.Dictation;` 缺则补）；在创建 `HostViewModel` 之前 `_dictation = new DictationService(new DictationModelStore(_context.DataRoot));`，作为 `HostViewModel` 构造函数最后一个实参传入（在 `voiceId => {...}` 之后）；`Dispose()` 里 `_host?.Dispose();` 之后加 `_dictation?.Dispose();`。
6. 新建 `tests/LocalDm.UiTests/DictationUiTests.cs`（命名空间 `LocalDm.UiTests`；仿 `PublicSyncBannerTests.cs` 的 `WpfRunner.RunAsync`、`HostView` + 离屏 `Window`、`NewHost` 写法；`EchoGateway`、`SilentTts` 在本文件里各写一份最小私有类，`SilentTts` 要能记录 `Stop()` 被调了几次；内存里的 `FakeDictation : IDictationService`：`StartAsync` 返回可配置的 `StartProblem`、`StopAsync` 返回可配置的 `DictationResult`，并记录调用次数；`State` 随之变化，`Progress`/`StateChanged` 事件声明但可不触发，`Dispose`、`Cancel` 空实现）。不需要剧本，`NewHost` 里不用存储（`store` 传 null 即可，若构造函数要求则照 `PublicSyncBannerTests` 用临时 `SessionStore`）。用例：
   - `Mic_button_toggles_listening_and_fills_input_without_sending`：窗口里找 `DictationButton`（`view.FindName("DictationButton")`）；`ToggleDictationCommand.Execute` 一次 → `IsListening` 为 true、`DictationHint` 为「正在听…」、`SilentTts.StopCalls` 为 1；`InputText` 先设为 `"我想"`，再 Execute 一次（结束）→ `IsListening` 为 false，`InputText` 为 `"我想 去书房看看"`（假服务返回 `"去书房看看"`），`DictationHint` 为空，并确认没发送（`host.Messages` 里没有玩家消息；用 `HostViewModel` 里存放对话消息的那个集合，先 `grep -n "ObservableCollection" src/LocalDm.App/ViewModels/HostViewModel.cs` 找到名字）。
   - `Start_problem_is_shown_and_not_listening`：`StartProblem = "没有找到麦克风，请检查 Windows 设置 → 隐私 → 麦克风。"` → Execute 后 `IsListening` 为 false，`DictationHint` 等于该文案。
   - `Empty_result_shows_problem_and_keeps_input`：`InputText = "保留"`，结束时返回 `new DictationResult("", "没听清，请再说一次。")` → `InputText` 仍为 `"保留"`，`DictationHint` 等于该文案。
   - `Right_alt_toggles_dictation_in_window`：显示窗口后，构造一个 `KeyEventArgs`（`new KeyEventArgs(Keyboard.PrimaryDevice, new HwndSource(new HwndSourceParameters("t")), 0, Key.RightAlt) { RoutedEvent = Keyboard.PreviewKeyDownEvent }`）在窗口上 `RaiseEvent`，断言 `IsListening` 变 true。**如果构造或触发不出来，就把这条用例删掉，并在汇报里写明「右 Alt 路径未自动化测试」**，不要为它改产品代码。
   - `Auto_stop_after_limit`：`host.DictationLimit = TimeSpan.FromMilliseconds(150)`；Execute 开始 → 等最多 2 秒（`Task.Delay` 轮询，参照 `WaitUntilAsync`）直到 `IsListening` 为 false 且假服务的 `StopCalls` 为 1。
   - `No_dictation_service_hides_button`：`NewHost` 不传 dictation → `DictationButton.Visibility` 为 `Collapsed`。

## Check
`dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.DictationUiTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不改引擎（`src/LocalDm.Infrastructure/Voice/Dictation/`、`IDictationService.cs`）；引擎接口不合用就停下来在汇报里说，不要自己改。
- 不装全局键盘钩子；不碰设置页、发送链路、朗读链路。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.
