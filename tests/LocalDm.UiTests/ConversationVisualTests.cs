using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalDm.App;
using LocalDm.App.Demo;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.UiTests;

/// <summary>
/// 第二版对话式界面的桌面验收快照。
///
/// 这里用的是真实 MainWindow、真实 SQLite 与真实 ViewModel，只把「模型」和「扬声器」
/// 换成确定性测试实现 —— 它们不是本任务要验证的对象，截图里也不该等它们。
/// 产物在 artifacts/ui/，文件名见每处 <c>Save</c>。
/// </summary>
public sealed class ConversationVisualTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 5 && dir is not null; i++)
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }

    private static string ArtifactsDir => Path.Combine(RepoRoot, "artifacts", "ui");

    /// <summary>常规桌面窗口：默认 1360×860，与 MainWindow 的初始尺寸一致。</summary>
    [Fact]
    public async Task Conversation_layout_snapshot_at_default_window_size()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var testShell = await TestShell.OpenAsync(root.Path);
            var shell = testShell.Shell;
            var window = NewWindow(shell, 1360, 860);
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                await WalkToSearchWithChatAsync(shell);
                await WaitForRenderAsync();

                // 对话按时间顺序：玩家消息、主持回复、阶段事件都在同一个列表里
                var host = shell.Host;
                Assert.True(host.Messages.Count >= 4, "对话里应当已经有多条消息。");
                Assert.Contains(host.Messages, m => m.IsPlayer);
                Assert.Contains(host.Messages, m => m.IsHost && m.CanReplay);
                Assert.Contains(host.Messages, m => m.IsEvent);
                Assert.False(host.IsBusy, "截图时不应还停在忙碌状态。");

                Save(window, "hostdesk-conversation.png");

                // 再补一张对话开头的：能看到玩家原话与主持的第一句回复。
                // 两张合起来才是「按时间顺序」的完整证据。
                var chatScroll = FindChatScroll(window);
                Assert.NotNull(chatScroll);
                chatScroll!.ScrollToTop();
                await WaitForRenderAsync();
                Save(window, "hostdesk-conversation-top.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 125% / 150% 缩放：系统 DPI 越高，同样一块屏幕给出的 DIP 越少。
    /// 这里不改系统 DPI（那属于系统设置），改为按同样的 DIP 空间渲染，
    /// 让布局承受与高缩放下完全一致的压力。
    /// </summary>
    [Theory]
    [InlineData(1.25, 1536, 864, "hostdesk-scale-125.png")]
    [InlineData(1.50, 1280, 720, "hostdesk-scale-150.png")]
    public async Task Conversation_layout_snapshot_at_scaled_dpi(double scale, int width, int height, string fileName)
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var testShell = await TestShell.OpenAsync(root.Path);
            var shell = testShell.Shell;
            var window = NewWindow(shell, width, height);
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                await WalkToSearchWithChatAsync(shell);
                await WaitForRenderAsync();

                // 布局必须在更小的 DIP 空间里仍站得住：左侧轨道、对话、输入区都在
                Assert.True(window.ActualWidth >= 1040, $"缩放到 {scale:P0} 时窗口不该被压到 1040 以下。");
                var host = shell.Host;
                Assert.True(host.Session.Projection is not null);
                Assert.Equal("搜证", host.Session.PhaseTitle);

                Save(window, fileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>揭秘后自动切到宽幅的公开材料与复盘页。</summary>
    [Fact]
    public async Task Materials_and_review_snapshot_after_reveal()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var testShell = await TestShell.OpenAsync(root.Path);
            var shell = testShell.Shell;
            var window = NewWindow(shell, 1360, 860);
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                var host = shell.Host;
                shell.ShowHostCommand.Execute(null);
                await host.LoadDemoAsync();
                var session = host.Session;

                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Start));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包一"]));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包二"]));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));

                Assert.True(SessionViewModel.TryParseVotes("A=3, B=2", out var votes, out _));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.SubmitVoteSummary, null, votes));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Reveal));

                await WaitForRenderAsync();

                Assert.True(host.ShowMaterials, "揭秘后应自动切到公开材料与复盘页。");
                Assert.Equal("公开材料与复盘", host.MaterialsTitle);
                Assert.True(host.Session.HasRevealedText);
                Assert.Contains(host.Session.TruthBlocks, b => b.Kind == TruthBlockKind.Code);

                Save(window, "hostdesk-materials.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 揭秘后会自动切到「公开材料与复盘」，此时那段播报可能还在念。
    /// 停止入口必须在所有页面上都够得着，不能只留在已经折叠的输入区里。
    /// </summary>
    [Fact]
    public async Task Stop_stays_reachable_after_reveal_switches_pages()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var tts = new GatedTts();
            using var testShell = await TestShell.OpenAsync(root.Path, tts);
            var shell = testShell.Shell;
            var window = NewWindow(shell, 1360, 860);
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                var host = shell.Host;
                shell.ShowHostCommand.Execute(null);
                await host.LoadDemoAsync();
                var session = host.Session;

                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Start));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包一"]));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包二"]));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                Assert.True(SessionViewModel.TryParseVotes("A=3, B=2", out var votes, out _));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.SubmitVoteSummary, null, votes));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                Assert.Equal("真相", session.PhaseTitle);

                // 从这里开始「一直在念」：揭秘会触发播报，播报不会自己结束
                tts.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reveal = session.ExecuteAsync(GameCommandType.Reveal);
                await WaitUntilAsync(() => host.IsBusy && host.ShowMaterials);
                await WaitForRenderAsync();

                Assert.True(host.ShowMaterials, "揭秘后应自动切到公开材料与复盘页。");
                Assert.True(host.IsBusy, "播报还没结束时应当是忙碌状态。");
                Assert.True(host.Session.Busy, "播报期间会话闸也要占住。");

                var stop = window.FindName("RailStopButton") as Button;
                Assert.NotNull(stop);
                Assert.True(stop!.IsVisible, "切到复盘页后，停止按钮必须仍然可见。");
                Assert.True(stop.IsEnabled, "停止按钮必须可点。");

                // 切到设置页也照样能停：停止入口在轨道上，不属于任何一个页面
                shell.ShowSetupCommand.Execute(null);
                await WaitForRenderAsync();
                Assert.True(stop.IsVisible, "设置页上也必须能停。");

                shell.ShowHostCommand.Execute(null);
                await WaitForRenderAsync();

                stop.Command!.Execute(null);
                await WaitUntilAsync(() => !host.IsBusy);
                Assert.False(host.Session.Busy, "停止后会话闸要放开。");
                Assert.Contains(host.Messages, m => m.IsEvent && m.Text.Contains("已停止本次回应", StringComparison.Ordinal));

                tts.Gate.SetResult();
                await reveal;
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>设置页快照与呼出路径。</summary>
    [Fact]
    public async Task Settings_page_is_reachable_from_the_rail()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var testShell = await TestShell.OpenAsync(root.Path);
            var shell = testShell.Shell;
            var window = NewWindow(shell, 1360, 860);
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                shell.ShowHostCommand.Execute(null);
                await shell.Host.LoadDemoAsync();
                await WaitForRenderAsync();

                shell.ShowSetupCommand.Execute(null);
                await WaitForRenderAsync();
                Assert.IsType<SetupViewModel>(shell.Current);
                Assert.True(
                    shell.CanReturnToSession,
                    "从设置页应能一键回到本局，否则只能重走剧本库。");
                Save(window, "hostdesk-settings-v2.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>剧本数据驱动的双轮搜证、无投票流程：阶段数量与动作名都来自剧本包。
    /// 这条用例证明桌面端真的按包渲染，而不是按固定七步。
    /// </summary>
    [Fact]
    public async Task Data_driven_double_round_flow_renders_in_the_real_window()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var testShell = await TestShell.OpenAsync(root.Path);
            var shell = testShell.Shell;
            var window = NewWindow(shell, 1360, 860);
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                var host = shell.Host;
                shell.ShowHostCommand.Execute(null);

                var package = DoubleRoundPackage();
                await host.LoadPackageAsync(package);

                Assert.Equal("序章共读", host.Session.PhaseTitle);
                Assert.Equal("1 / 6", host.Session.PhaseProgress);
                Assert.Equal("进入第一轮搜证", host.Session.AdvanceLabel);
                Assert.False(host.Session.IsVoteVisible, "这个流程没有投票。");

                var session = host.Session;
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Start));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                Assert.Equal("第一轮搜证", session.PhaseTitle);
                Assert.Equal("发放材料 A01", session.ReleaseLabel);

                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["A01"]));
                await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
                await WaitForRenderAsync();

                Save(window, "hostdesk-double-round.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 占位提示只在「失去焦点且为空」时出现。
    ///
    /// 用输入法打拼音时，拼音还在候选窗口里、没进 Text；占位提示若只看 InputText，
    /// 就会和拼音叠在同一个位置。
    /// </summary>
    [Fact]
    public async Task Composer_placeholder_hides_while_the_input_box_has_focus()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var testShell = await TestShell.OpenAsync(root.Path);
            var shell = testShell.Shell;
            var window = NewWindow(shell, 1280, 800);
            WpfRunner.MoveOffScreen(window);
            window.Show();
            window.Activate();
            try
            {
                shell.ShowHostCommand.Execute(null);
                await shell.Host.LoadDemoAsync();
                await WaitForRenderAsync();

                var input = FindByName<TextBox>(window, "InputBox");
                var placeholder = FindByName<TextBlock>(window, "ComposerPlaceholder");
                Assert.NotNull(input);
                Assert.NotNull(placeholder);
                Assert.Equal("", shell.Host.InputText);

                // 拿到焦点：即使还没输入任何字，提示也要让位
                Assert.True(input!.Focus(), "输入框应当能拿到键盘焦点。");
                Assert.True(input.IsKeyboardFocusWithin);
                await WaitForRenderAsync();
                Assert.Equal(Visibility.Collapsed, placeholder!.Visibility);

                // 打拼音：文字还没提交时输入框是空的，提示同样不能出现
                Assert.Equal("", input.Text);
                Assert.Equal(Visibility.Collapsed, placeholder.Visibility);

                // 失去焦点并且为空：提示回来
                System.Windows.Input.Keyboard.ClearFocus();
                await WaitForRenderAsync();
                Assert.False(input.IsKeyboardFocusWithin);
                Assert.Equal(Visibility.Visible, placeholder.Visibility);

                // 有内容时不管有没有焦点都不显示提示
                shell.Host.InputText = "我们去调查书房";
                await WaitForRenderAsync();
                Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ===== 辅助 =====

    private static MainWindow NewWindow(ShellViewModel shell, int width, int height) =>
        new() { DataContext = shell, ShowActivated = false, Width = width, Height = height };

    /// <summary>按 x:Name 在真实可视树里找一个元素。模板里的元素不在逻辑树的字段上，只能这样找。</summary>
    private static T? FindByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        if (root is T match && match.Name == name)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindByName<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>在真实可视树里找到对话的滚动区（模板里叫 ChatScroll）。</summary>
    private static ScrollViewer? FindChatScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll && scroll.Name == "ChatScroll")
        {
            return scroll;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindChatScroll(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>走到搜证环节，并先问一句，让对话里同时有玩家、主持与阶段事件。</summary>
    private static async Task WalkToSearchWithChatAsync(ShellViewModel shell)
    {
        var host = shell.Host;
        shell.ShowHostCommand.Execute(null);
        await host.LoadDemoAsync();

        var session = host.Session;
        await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Start));

        host.InputText = "大家都拿到角色本了，接下来做什么？";
        await host.SendAsync();
        await WaitUntilAsync(() => !host.IsBusy);

        await StepAsync(host, () => session.ExecuteAsync(GameCommandType.Advance));
        await StepAsync(host, () => session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包一"]));
    }

    private static async Task StepAsync(HostViewModel host, Func<Task> action)
    {
        await action();
        // 提交后的播报是事件驱动的：等它念完，截图里才有完整就地状态
        await WaitUntilAsync(() => !host.IsBusy);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 120 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }

    private static async Task WaitForRenderAsync()
    {
        await Task.Delay(150);
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(80);
    }

    private static void Save(Window window, string fileName)
    {
        Directory.CreateDirectory(ArtifactsDir);
        window.UpdateLayout();

        var target = (FrameworkElement?)window.Content ?? window;
        var width = (int)Math.Max(target.ActualWidth, 1);
        var height = (int)Math.Max(target.ActualHeight, 1);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(ArtifactsDir, fileName));
        encoder.Save(stream);

        var file = new FileInfo(Path.Combine(ArtifactsDir, fileName));
        Assert.True(file.Exists && file.Length > 0, $"截图没有生成：{fileName}");
    }

    /// <summary>
    /// 与 RealScriptPackageBuilder 的产出形状一致的合成双轮流程包。
    /// 全部为占位文本，只为在真实窗口里验证「流程跟着剧本数据走」。
    /// </summary>
    private static ScriptPackage DoubleRoundPackage()
    {
        var pages = new[]
        {
            Page("组织", 1, "1", "公共序章占位文本", "f0", "sp0", RealScriptPackageBuilder.ExtractedGroupKind.Role),
            Page("A01", 1, "1", "第一轮线索占位", "f1", "sp1", RealScriptPackageBuilder.ExtractedGroupKind.Material),
            Page("B01", 1, "1", "第二轮线索占位", "f2", "sp2", RealScriptPackageBuilder.ExtractedGroupKind.Material),
            Page("组织", 9, "9", "合成结局占位文本", "f3", "sp3", RealScriptPackageBuilder.ExtractedGroupKind.Role),
        };
        var sources = pages.Select(p => new SourceFile
        {
            FileId = p.FileId,
            PrivatePath = p.FileId,
            ContentHash = new string('a', 64),
            PageCount = 1,
            Origin = SourceOrder.ManifestOrder,
            ToolIdentity = "synthetic-ui",
            ParseState = FileParseState.Complete,
        }).ToArray();

        var flow = new ScriptFlow
        {
            Steps =
            [
                new ScriptFlowStep
                {
                    StepId = "prologue", Title = "序章共读", Kind = ScriptStepKind.Intro,
                    SourceRef = "第 1 页 序章", Hint = "一起读完公共序章后，进入第一轮搜证。",
                    Conditions = ["公共序章已读完"], PublicGroups = ["组织"],
                    AdvanceLabel = "进入第一轮搜证",
                },
                new ScriptFlowStep
                {
                    StepId = "search-one", Title = "第一轮搜证", Kind = ScriptStepKind.Search,
                    SourceRef = "第 3 页 第一轮搜证", Hint = "领取本轮材料，发放完成后进入讨论。",
                    Conditions = ["本轮材料全部发放"], MaterialIds = ["A01"], RequiresAllMaterials = true,
                },
                new ScriptFlowStep
                {
                    StepId = "talk-one", Title = "第一轮讨论", Kind = ScriptStepKind.Talk,
                    SourceRef = "第 5 页 第一轮讨论", Hint = "交流第一轮发现。",
                    Conditions = ["讨论完成"],
                },
                new ScriptFlowStep
                {
                    StepId = "search-two", Title = "第二轮搜证", Kind = ScriptStepKind.Search,
                    SourceRef = "第 6 页 第二轮搜证", Hint = "本轮只发放新增材料。",
                    Conditions = ["本轮材料发出"], MaterialIds = ["B01"], RequiresAllMaterials = true,
                },
                new ScriptFlowStep
                {
                    StepId = "talk-two", Title = "最终讨论", Kind = ScriptStepKind.Talk,
                    SourceRef = "第 8 页 最终讨论", Hint = "综合两轮信息讨论。",
                    Conditions = ["讨论完成"],
                },
                new ScriptFlowStep
                {
                    StepId = "ending", Title = "结局与复盘", Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页 结局", Hint = "点击会向所有玩家公开结局。",
                    RevealLabel = "公开结局",
                },
            ],
        };

        return RealScriptPackageBuilder.Build("双轮搜证合成剧本（非真实剧本）", 5, pages, sources, flow: flow).Package;
    }

    private static RealScriptPackageBuilder.ExtractedPage Page(
        string group,
        int page,
        string label,
        string text,
        string fileId,
        string spanId,
        RealScriptPackageBuilder.ExtractedGroupKind kind) => new()
    {
        Group = group,
        GroupKind = kind,
        PageNumber = page,
        PageLabel = label,
        Text = text,
        FileId = fileId,
        SpanId = spanId,
    };

    /// <summary>真实组合根的一个变体：只替换模型与扬声器，其余都是真实现。</summary>
    private sealed class TestShell : IDisposable
    {
        private readonly SessionContext _context;

        private TestShell(SessionContext context, ShellViewModel shell)
        {
            _context = context;
            Shell = shell;
        }

        public ShellViewModel Shell { get; }

        public static async Task<TestShell> OpenAsync(string root, ITtsService? replaceTts = null)
        {
            var context = await SessionContext.OpenAsync(root, CancellationToken.None);
            await context.Settings.SaveAsync(
                ToolSettings.Default with { ModelId = "ui-test-model", AutoRead = true },
                CancellationToken.None);

            var tts = replaceTts ?? new SilentTts();
            var gateway = new EchoGateway();
            var director = new HostDirector(gateway, tts);
            var setup = new SetupViewModel(context.Settings, gateway, tts, director);
            var dispatcher = new CommandDispatcher(context.Store);
            var host = new HostViewModel(
                director,
                () => context.Settings.Load(),
                new HostIntentRouter(),
                dispatcher,
                context.Store,
                tts);
            var library = new LibraryViewModel(Path.Combine(root, "packages"));
            var shell = new ShellViewModel(library, setup, host);
            await shell.InitializeAsync(CancellationToken.None);
            return new TestShell(context, shell);
        }

        public void Dispose()
        {
            Shell.Host.Dispose();
            _context.Dispose();
        }
    }

    private sealed class EchoGateway : IMagpieGateway
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["ui-test-model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult("这是界面验收用的合成主持文字，不含任何真实剧情。");
    }

    private sealed class SilentTts : ITtsService
    {
        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "ui-test-voice", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop()
        {
        }
    }

    /// <summary>
    /// 会「一直念不完」的扬声器：用来验证长语音期间界面还留不留得住停止入口。
    /// Gate 可以在运行中挂上，避免开局阶段的播报也被挡住。
    /// </summary>
    private sealed class GatedTts : ITtsService
    {
        public TaskCompletionSource? Gate { get; set; }

        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "ui-test-voice", Culture = "zh-CN" }];

        public async Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
        {
            if (Gate is not null)
            {
                await Gate.Task;
            }
        }

        public async Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
        {
            if (Gate is not null)
            {
                await Gate.Task;
            }
        }

        public void Stop()
        {
        }
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "localdm-visual2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
