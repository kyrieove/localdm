# readonly-prompt-page — 设置页高级项里只读显示主持提示词和流程起草提示词

## Goal
- 设置页多一个折叠项「高级：主持提示词（只读）」，里面两个只读、可选中复制的文本框：主持人固定提示词（模板原文，`{剧本名}`、`{人数}` 未替换）和导入时起草流程的提示词。
- 不显示任何剧本资料（主持资料、当前局面都不显示）。

## Risk
medium

## Parameters
- 视图模型属性名：`HostPromptText`（= `HostNarration.FixedPromptTemplate`）、`FlowDraftPromptText`（= `FlowDrafter.Prompt`）。
- 控件名：`HostPromptBox`、`FlowPromptBox`；`IsReadOnly="True"`，`TextWrapping="Wrap"`，`MaxHeight="360"`，`VerticalScrollBarVisibility="Auto"`，`FontSize="13"`。
- 折叠项标题文字：`高级：主持提示词（只读）`。

## Files
may change: `src/LocalDm.Core/Hosting/HostNarration.cs`, `src/LocalDm.App/ViewModels/SetupViewModel.cs`, `src/LocalDm.App/Views/SetupView.xaml`, `tests/LocalDm.UiTests/SetupViewTests.cs` / read-only: `src/LocalDm.Core/Import/FlowDrafter.cs`

## Steps (N = 4)
1. `src/LocalDm.Core/Hosting/HostNarration.cs`：紧接 `private const string SystemPromptTemplate = """ … """;` 之后加：
   ```csharp
   /// <summary>设置页只读展示用：固定提示词模板原文，剧本名和人数占位未替换，不含主持资料。</summary>
   public static string FixedPromptTemplate => SystemPromptTemplate;
   ```
2. `src/LocalDm.App/ViewModels/SetupViewModel.cs`：文件头加 `using LocalDm.Core.Import;`；在 `OcrPythonPath` 属性之后加：
   ```csharp
   /// <summary>只读展示：发给主持模型的固定规则。</summary>
   public string HostPromptText => HostNarration.FixedPromptTemplate;

   /// <summary>只读展示：导入剧本时起草流程用的提示词。</summary>
   public string FlowDraftPromptText => FlowDrafter.Prompt;
   ```
3. `src/LocalDm.App/Views/SetupView.xaml`：在「高级：剧本准备」那个 `</Expander>` 之后、「保存设置」按钮之前插入（样式名照抄现有的 `RailLabel`、`ChatMeta`、`InkSoft`、`Muted`）：
   ```xml
   <!-- 高级：主持提示词，只读，方便对照和复制 -->
   <Expander Margin="0,6,0,0" Foreground="{StaticResource InkSoft}">
       <Expander.Header>
           <TextBlock Text="高级：主持提示词（只读）" FontSize="13" Foreground="{StaticResource Muted}" />
       </Expander.Header>
       <StackPanel Margin="0,14,0,0">
           <TextBlock Text="主持人固定提示词" Style="{StaticResource RailLabel}" Margin="0,0,0,6" />
           <TextBox x:Name="HostPromptBox" Text="{Binding HostPromptText, Mode=OneWay}"
                    IsReadOnly="True" TextWrapping="Wrap" MaxHeight="360" FontSize="13"
                    VerticalScrollBarVisibility="Auto" />
           <TextBlock Text="开局时把「{剧本名}」「{人数}」换成实际值。每轮还会附上剧本资料和当前局面，这里不显示，免得剧透。"
                      Style="{StaticResource ChatMeta}" Margin="0,8,0,0" TextWrapping="Wrap" />

           <TextBlock Text="导入时起草流程的提示词" Style="{StaticResource RailLabel}" Margin="0,14,0,6" />
           <TextBox x:Name="FlowPromptBox" Text="{Binding FlowDraftPromptText, Mode=OneWay}"
                    IsReadOnly="True" TextWrapping="Wrap" MaxHeight="360" FontSize="13"
                    VerticalScrollBarVisibility="Auto" />
       </StackPanel>
   </Expander>
   ```
4. `tests/LocalDm.UiTests/SetupViewTests.cs`：照文件里第一个测试 `Load_replaces_unavailable_saved_voice_with_first_available_voice` 的写法（`WpfRunner.RunAsync`、`TempRoot`、`SettingsStore`、`StubGateway`、`SilentTts`、`HostDirector`、`new SetupViewModel(store, gateway, tts, director)`）新加测试 `Advanced_section_shows_fixed_prompts_read_only`：
   - `Assert.Equal(HostNarration.FixedPromptTemplate, setup.HostPromptText);` 并 `Assert.Contains("你是《{剧本名}》", setup.HostPromptText, StringComparison.Ordinal);`
   - `Assert.Equal(LocalDm.Core.Import.FlowDrafter.Prompt, setup.FlowDraftPromptText);`
   - `var view = new LocalDm.App.Views.SetupView { DataContext = setup };`，对 `"HostPromptBox"`、`"FlowPromptBox"` 各做 `var box = Assert.IsType<System.Windows.Controls.TextBox>(view.FindName(name)); Assert.True(box.IsReadOnly);`
   - 如果 `SetupView` 的命名空间或构造方式和上面不同，按 `tests/LocalDm.UiTests/HostDeskVisualTests.cs` 里创建视图的方式改，只改这一处。

## Check
`dotnet test tests/LocalDm.UiTests --filter "FullyQualifiedName~SetupViewTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- Reuse existing functions; don't copy existing logic.
- No rm, no git push, no git checkout . / restore . / reset --hard.
- 提示词不可编辑：不加保存、不加设置字段。
- 不读取、不打印 `stories/`、`.appdata/`、`.local/` 下的文件。

## Acceptance
- verdict: accepted
- files: src/LocalDm.App/ViewModels/SetupViewModel.cs, src/LocalDm.App/Views/SetupView.xaml, src/LocalDm.Core/Hosting/HostNarration.cs, tests/LocalDm.UiTests/SetupViewTests.cs
- check: `dotnet test tests/LocalDm.UiTests --filter "FullyQualifiedName~SetupViewTests"` → exit 0 (run by run-card)
- tokens:
  - round 1: exec antigravity/gemini-3.8-flash: fresh 243k, cached 221k, out 10k  (agy-readonly-prompt-page.jsonl) · review gpt-6.1-sol: fresh 36k, cached 17k, out 1k  (codex-review-readonly-prompt-page.jsonl)
- review findings: 0 real / 0 false positive / 0 missed (medium, gpt-6.1-sol)
