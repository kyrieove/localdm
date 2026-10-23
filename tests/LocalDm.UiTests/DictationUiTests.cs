using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LocalDm.App.ViewModels;
using LocalDm.App.Views;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;

namespace LocalDm.UiTests;

public sealed class DictationUiTests
{
    [Fact]
    public async Task Mic_button_toggles_listening_and_fills_input_without_sending()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var fakeDictation = new FakeDictation { StopResult = new DictationResult("去书房看看", null) };
            var tts = new SilentTts();
            var host = NewHost(fakeDictation, tts);

            var view = new HostView { DataContext = host };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            WpfRunner.ShowOffscreen(window);
            try
            {
                await WaitForRenderAsync();

                var button = view.FindName("DictationButton") as Button;
                Assert.NotNull(button);
                Assert.Equal(Visibility.Visible, button.Visibility);

                // 点击开始听
                host.ToggleDictationCommand.Execute(null);
                Assert.True(host.IsListening);
                Assert.Equal("正在听…", host.DictationHint);
                Assert.Equal(1, tts.StopCalls);

                // 设初值
                host.InputText = "我想";

                // 点击结束听写
                host.ToggleDictationCommand.Execute(null);
                Assert.False(host.IsListening);
                Assert.Equal("我想 去书房看看", host.InputText);
                Assert.Equal("", host.DictationHint);

                // 确认没发送
                Assert.DoesNotContain(host.Messages, m => m.Author == ChatAuthor.Player);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Start_problem_is_shown_and_not_listening()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var problemText = "没有找到麦克风，请检查 Windows 设置 → 隐私 → 麦克风。";
            var fakeDictation = new FakeDictation { StartProblem = problemText };
            var host = NewHost(fakeDictation);

            var view = new HostView { DataContext = host };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            WpfRunner.ShowOffscreen(window);
            try
            {
                await WaitForRenderAsync();

                host.ToggleDictationCommand.Execute(null);
                Assert.False(host.IsListening);
                Assert.Equal(problemText, host.DictationHint);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Empty_result_shows_problem_and_keeps_input()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var fakeDictation = new FakeDictation
            {
                StopResult = new DictationResult("", "没听清，请再说一次。")
            };
            var host = NewHost(fakeDictation);

            var view = new HostView { DataContext = host };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            WpfRunner.ShowOffscreen(window);
            try
            {
                await WaitForRenderAsync();

                host.InputText = "保留";

                // 开始听
                host.ToggleDictationCommand.Execute(null);
                Assert.True(host.IsListening);

                // 结束
                host.ToggleDictationCommand.Execute(null);
                Assert.False(host.IsListening);
                Assert.Equal("保留", host.InputText);
                Assert.Equal("没听清，请再说一次。", host.DictationHint);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Right_alt_toggles_dictation_in_window()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var fakeDictation = new FakeDictation();
            var host = NewHost(fakeDictation);

            var view = new HostView { DataContext = host };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            WpfRunner.ShowOffscreen(window);
            try
            {
                await WaitForRenderAsync();

                var source = new HwndSource(new HwndSourceParameters("t"));
                var eventArgs = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.RightAlt)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                window.RaiseEvent(eventArgs);

                Assert.True(host.IsListening);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Auto_stop_after_limit()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var fakeDictation = new FakeDictation();
            var host = NewHost(fakeDictation);
            host.DictationLimit = TimeSpan.FromMilliseconds(150);

            var view = new HostView { DataContext = host };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            WpfRunner.ShowOffscreen(window);
            try
            {
                await WaitForRenderAsync();

                host.ToggleDictationCommand.Execute(null);
                Assert.True(host.IsListening);

                await WaitUntilAsync(() => !host.IsListening && fakeDictation.StopCalls == 1);
                Assert.False(host.IsListening);
                Assert.Equal(1, fakeDictation.StopCalls);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task No_dictation_service_hides_button()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var host = NewHost(dictation: null);

            var view = new HostView { DataContext = host };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            WpfRunner.ShowOffscreen(window);
            try
            {
                await WaitForRenderAsync();

                var button = view.FindName("DictationButton") as Button;
                Assert.NotNull(button);
                Assert.Equal(Visibility.Collapsed, button.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static HostViewModel NewHost(IDictationService? dictation = null, SilentTts? tts = null)
    {
        tts ??= new SilentTts();
        var settings = ToolSettings.Default with { ModelId = "ui-test-model", AutoRead = false };
        return new HostViewModel(
            new HostDirector(new EchoGateway(), tts),
            () => settings,
            new HostIntentRouter(),
            dispatcher: null,
            store: null,
            tts: tts,
            onVoiceChanged: null,
            dictation: dictation);
    }

    private static async Task WaitForRenderAsync()
    {
        await Task.Delay(120);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(60);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
        }
    }

    private sealed class EchoGateway : IMagpieGateway
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["ui-test-model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult("这是界面测试用的主持回复。");
    }

    private sealed class SilentTts : ITtsService
    {
        public int StopCalls { get; private set; }

        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop()
        {
            StopCalls++;
        }
    }

    private sealed class FakeDictation : IDictationService
    {
        public string? StartProblem { get; set; }
        public DictationResult StopResult { get; set; } = new("", null);
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public DictationState State { get; private set; } = DictationState.Idle;

#pragma warning disable CS0067
        public event Action<DictationState>? StateChanged;
        public event Action<string>? Progress;
#pragma warning restore CS0067

        public Task<string?> StartAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            if (StartProblem is not null)
            {
                State = DictationState.Idle;
                return Task.FromResult<string?>(StartProblem);
            }

            State = DictationState.Listening;
            return Task.FromResult<string?>(null);
        }

        public Task<DictationResult> StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            State = DictationState.Idle;
            return Task.FromResult(StopResult);
        }

        public void Cancel()
        {
            State = DictationState.Idle;
        }

        public void Dispose()
        {
        }
    }
}
