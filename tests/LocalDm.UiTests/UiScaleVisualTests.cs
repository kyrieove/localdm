using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalDm.App;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.UiTests;

/// <summary>
/// 界面缩放与对话宽度：真实窗口、真实设置文件。
///
/// 两件事必须由界面证明，不能只靠 ViewModel 断言：
/// 1) 缩放真的落在内容根上（LayoutTransform），否则 200% 下按钮会被推出窗口；
/// 2) 缩放到 200%、窗口 1280×720 时，左侧栏与输入区都还够得到（左栏可以滚动，但不能被裁掉）。
/// </summary>
public sealed class UiScaleVisualTests
{
    [Fact]
    public async Task Scale_steps_clamp_at_the_range_and_are_saved_immediately()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var shell = await TestShell.OpenAsync(root.Path);
            var settings = root.Path;
            var store = new SettingsStore(Path.Combine(settings, "settings.json"), Path.Combine(settings, "secrets"));
            var window = new MainWindow { DataContext = shell.Shell, ShowActivated = false, Width = 1280, Height = 720 };
            window.ApplyUiSettings(store, store.Load());
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                Assert.Equal(1.0, ScaleOf(window));

                // 每次一格：加号 / 减号
                window.StepUiScale(1);
                Assert.Equal(1.1, ScaleOf(window));
                window.StepUiScale(-1);
                Assert.Equal(1.0, ScaleOf(window));

                // 超出范围夹住，不报错、也不写坏文件
                window.SetUiScale(99);
                Assert.Equal(ToolSettings.MaxUiScale, ScaleOf(window));
                window.SetUiScale(0.1);
                Assert.Equal(ToolSettings.MinUiScale, ScaleOf(window));

                // Ctrl+0 回 1.0
                window.ResetUiScale();
                Assert.Equal(1.0, ScaleOf(window));

                // 改完立即落盘：下次启动保持同一个倍数
                window.SetUiScale(1.6);
                Assert.Equal(1.6, store.Load().UiScale);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Left_rail_and_composer_are_reachable_at_200_percent_on_a_small_window()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var shell = await TestShell.OpenAsync(root.Path);
            var store = new SettingsStore(
                Path.Combine(root.Path, "settings.json"),
                Path.Combine(root.Path, "secrets"));

            var window = new MainWindow { DataContext = shell.Shell, ShowActivated = false, Width = 1280, Height = 720 };
            window.ApplyUiSettings(store, store.Load() with { UiScale = 2.0 });
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                await shell.WalkToChatAsync();
                await WaitForRenderAsync();

                Assert.Equal(2.0, ScaleOf(window));

                var content = (FrameworkElement)window.Content;
                Assert.True(content.ActualWidth > 0 && content.ActualHeight > 0);

                // 输入区必须留在窗口里：这是主持人唯一能打字的地方，被裁掉就发不出话。
                var input = FindByName<TextBox>(content, "InputBox");
                Assert.NotNull(input);
                var inputBounds = BoundsIn(content, input!);
                Assert.True(inputBounds.Bottom <= content.ActualHeight + 1,
                    $"输入区被裁掉了：底部 {inputBounds.Bottom:0} > 窗口 {content.ActualHeight:0}");
                Assert.True(inputBounds.Right <= content.ActualWidth + 1,
                    $"输入区超出了右边界：{inputBounds.Right:0} > {content.ActualWidth:0}");

                // 左侧栏放不下时可以滚动：整栏的 ScrollViewer 确实可滚，
                // 底部的「偏好设置」滚到底之后完整落在窗口里。
                var rail = FindByName<ScrollViewer>(content, "RailScroll");
                Assert.NotNull(rail);
                Assert.Equal(ScrollBarVisibility.Auto, rail!.VerticalScrollBarVisibility);
                Assert.True(rail.ScrollableHeight > 0,
                    "200% 下左栏内容应当高于视口，从而改为滚动而不是被裁掉。");

                var setup = FindByName<Button>(content, "NavSetup");
                Assert.NotNull(setup);
                rail.ScrollToEnd();
                await WaitForRenderAsync();
                var setupBounds = BoundsIn(content, setup!);
                Assert.True(setupBounds.Bottom <= content.ActualHeight + 1,
                    $"左栏底部的按钮滚到底之后仍被裁掉：{setupBounds.Bottom:0} > {content.ActualHeight:0}");
                Assert.True(setupBounds.Top >= -1,
                    $"左栏底部的按钮滚不到可见区域：顶部 {setupBounds.Top:0}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Chat_width_tiers_widen_the_conversation_column()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var shell = await TestShell.OpenAsync(root.Path);

            // 窗口要宽到能看出三档差别：满宽 = 2000 − 278 左栏 − 68 两端边距 − 滚动条。
            var window = new MainWindow { DataContext = shell.Shell, ShowActivated = false, Width = 2000, Height = 900 };
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                await shell.WalkToChatAsync();
                await WaitForRenderAsync();

                var content = (FrameworkElement)window.Content;
                var chat = FindByName<ScrollViewer>(content, "ChatScroll");
                Assert.NotNull(chat);
                var column = (FrameworkElement)chat!.Content;

                // 标准档：封顶 980
                UiWidth.ApplyChatWidth(shell.Shell, ChatWidthMode.Standard);
                await WaitForRenderAsync();
                Assert.Equal(980, shell.Shell.Host.ChatMaxWidth);
                Assert.Equal(620, shell.Shell.Host.BubbleMaxWidth);
                Assert.True(column.ActualWidth <= 981,
                    $"标准档不该超过 980，实际 {column.ActualWidth:0}");

                // 宽档：封顶 1320
                UiWidth.ApplyChatWidth(shell.Shell, ChatWidthMode.Wide);
                await WaitForRenderAsync();
                Assert.Equal(1320, shell.Shell.Host.ChatMaxWidth);
                Assert.InRange(column.ActualWidth, 1300, 1321);

                // 满宽：不限宽，只剩两端边距
                UiWidth.ApplyChatWidth(shell.Shell, ChatWidthMode.Full);
                await WaitForRenderAsync();
                Assert.True(double.IsPositiveInfinity(shell.Shell.Host.ChatMaxWidth));
                Assert.True(column.ActualWidth > 1320,
                    $"满宽应当继续变宽，实际 {column.ActualWidth:0}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Preferences_page_exposes_the_scale_slider_and_the_three_width_tiers()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var shell = await TestShell.OpenAsync(root.Path);
            var store = new SettingsStore(
                Path.Combine(root.Path, "settings.json"),
                Path.Combine(root.Path, "secrets"));

            var window = new MainWindow { DataContext = shell.Shell, ShowActivated = false, Width = 1360, Height = 860 };
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                shell.Shell.ShowSetupCommand.Execute(null);
                await WaitForRenderAsync();

                var content = (FrameworkElement)window.Content;
                var slider = FindByName<Slider>(content, "UiScaleSlider");
                Assert.NotNull(slider);
                Assert.Equal(ToolSettings.MinUiScale, slider!.Minimum);
                Assert.Equal(ToolSettings.MaxUiScale, slider.Maximum);
                Assert.Equal(1.0, slider.Value);

                // 拖动滑块就是改设置里的倍数
                slider.Value = 1.4;
                await WaitForRenderAsync();
                Assert.Equal(1.4, shell.Shell.Setup.UiScale);

                // 三档单选：点「宽」把档位切过去
                var wide = FindByName<RadioButton>(content, "ChatWidthWide");
                var standard = FindByName<RadioButton>(content, "ChatWidthStandard");
                var full = FindByName<RadioButton>(content, "ChatWidthFull");
                Assert.NotNull(wide);
                Assert.NotNull(standard);
                Assert.NotNull(full);
                Assert.True(standard!.IsChecked, "默认应当是标准档。");

                wide!.IsChecked = true;
                await WaitForRenderAsync();
                Assert.Equal(ChatWidthMode.Wide, shell.Shell.Setup.ChatWidthMode);
                Assert.False(standard.IsChecked);

                // 点保存：两项都落盘
                shell.Shell.Setup.SaveCommand.Execute(null);
                for (var i = 0; i < 60 && store.Load().UiScale != 1.4; i++)
                {
                    await Task.Delay(50);
                }

                var saved = store.Load();
                Assert.Equal(1.4, saved.UiScale);
                Assert.Equal(ChatWidthMode.Wide, saved.ChatWidthMode);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ===== 辅助 =====

    private static double ScaleOf(MainWindow window) =>
        ((ScaleTransform)((FrameworkElement)window.Content).LayoutTransform).ScaleX;

    /// <summary>元素在内容根坐标系里的位置：内容根带 LayoutTransform，比较时两边是同一个坐标系。</summary>
    private static Rect BoundsIn(FrameworkElement contentRoot, FrameworkElement element) =>
        element.TransformToAncestor(contentRoot).TransformBounds(new Rect(element.RenderSize));

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

    private static async Task WaitForRenderAsync()
    {
        await Task.Delay(150);
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(80);
    }

    /// <summary>真实组合根的一个变体：只替换模型与扬声器。</summary>
    private sealed class TestShell : IDisposable
    {
        private readonly SessionContext _context;

        private TestShell(SessionContext context, ShellViewModel shell)
        {
            _context = context;
            Shell = shell;
        }

        public ShellViewModel Shell { get; }

        public static async Task<TestShell> OpenAsync(string root)
        {
            var context = await SessionContext.OpenAsync(root, CancellationToken.None);
            await context.Settings.SaveAsync(
                ToolSettings.Default with { ModelId = "ui-test-model", AutoRead = false, UiScale = 1.0 },
                CancellationToken.None);

            var tts = new SilentTts();
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

        /// <summary>切到主持台、装演示剧本，并先问一句：对话与输入区都要有真实内容。</summary>
        public async Task WalkToChatAsync()
        {
            Shell.ShowHostCommand.Execute(null);
            await Shell.Host.LoadDemoAsync();
            await Shell.Host.Session.ExecuteAsync(GameCommandType.Start, null, null);
            await WaitUntilAsync(() => !Shell.Host.IsBusy);

            Shell.Host.InputText = "大家都拿到角色本了，接下来做什么？";
            await Shell.Host.SendAsync();
            await WaitUntilAsync(() => !Shell.Host.IsBusy);
        }

        public void Dispose()
        {
            Shell.Host.Dispose();
            _context.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 120 && !condition(); i++)
        {
            await Task.Delay(20);
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

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "localdm-uiscale-" + Guid.NewGuid().ToString("N"));
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
