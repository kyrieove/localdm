STATUS: done

改动的文件：
- 新增 `src/LocalDm.Tablet/Pages/SpeechBar.razor`（输入条、对话面板、发送、停止朗读、快捷键、提示行和字数）
- `src/LocalDm.Tablet/Pages/Home.razor`（注入服务，在所有页签底部挂上输入条，轮询时刷新主持忙与朗读状态，最近 8 条公开消息，公共对话说明改文案）
- `src/LocalDm.Tablet/wwwroot/reader.css`（`.speech-bar`、`.speech-panel`、`.speech-input` 16px、`.speech-hint`、`.speech-count`、按钮样式与窄屏单列）
- 新增 `src/LocalDm.Tablet/SpikeTabletSpeech.cs`（探针演示宿主，3 秒忙窗口，可选 `TimeProvider`）
- `src/LocalDm.Tablet/Program.cs`（合成模式下传入 `SpikeTabletSpeech`，包模式继续用默认空实现）
- `tests/LocalDm.Tests/TabletSpeechTests.cs`（新增 `Spike_speech_accepts_once_then_is_busy_until_the_deadline`）

验证：`dotnet build src/LocalDm.Tablet` 与 `dotnet build tests/LocalDm.Tests` 均 `0 个警告 0 个错误`。

没有做的事：按要求没跑 Check、没提交；`SpeechBar.razor` 用的是 `_text.Length`，卡片里说的 Enter 换行由清空 `_text` 覆盖，没有引入 `preventDefault`（Blazor 那是整体开关，会挡掉输入）。