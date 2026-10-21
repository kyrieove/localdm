using System.IO;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.UiTests;

/// <summary>
/// 设置页命令可执行状态通知测试。
/// </summary>
public sealed class SetupViewTests
{
    [Fact]
    public async Task Busy_transition_raises_save_and_check_voices_can_execute_changed()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var settingsPath = Path.Combine(root.Path, "settings.json");
            var secretsDir = Path.Combine(root.Path, "secrets");
            var store = new SettingsStore(settingsPath, secretsDir);
            await store.SaveAsync(ToolSettings.Default, CancellationToken.None);

            var gateway = new StubGateway();
            var tts = new SilentTts();
            var director = new HostDirector(gateway, tts);
            var setup = new SetupViewModel(store, gateway, tts, director);
            await setup.LoadAsync(CancellationToken.None);

            var saveRaised = 0;
            var voicesRaised = 0;
            setup.SaveCommand.CanExecuteChanged += (_, _) => saveRaised++;
            setup.CheckVoicesCommand.CanExecuteChanged += (_, _) => voicesRaised++;

            var busyResetTcs = new TaskCompletionSource();
            setup.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SetupViewModel.Busy) && !setup.Busy)
                {
                    busyResetTcs.TrySetResult();
                }
            };

            // 执行 CheckConnectionCommand，使 Busy 经历 false -> true -> false
            setup.CheckConnectionCommand.Execute(null);
            await busyResetTcs.Task;

            Assert.False(setup.Busy);
            Assert.True(saveRaised >= 2, "Busy 从 true 变回 false 时，SaveCommand 必须收到 CanExecuteChanged。");
            Assert.True(voicesRaised >= 2, "Busy 从 true 变回 false 时，CheckVoicesCommand 必须收到 CanExecuteChanged。");
            Assert.True(setup.SaveCommand.CanExecute(null));
            Assert.True(setup.CheckVoicesCommand.CanExecute(null));
        });
    }

    private sealed class StubGateway : IMagpieGateway
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult("ok");

        public Task<MagpieCompletion> CompleteWithUsageAsync(
            MagpieCompletionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MagpieCompletion("ok", new MagpieUsage(100, 10, null)));
    }

    private sealed class SilentTts : ITtsService
    {
        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop() { }
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "localdm-setup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
        }
    }
}
