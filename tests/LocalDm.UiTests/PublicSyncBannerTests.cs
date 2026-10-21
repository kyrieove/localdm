using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LocalDm.App.ViewModels;
using LocalDm.App.Views;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.UiTests;

/// <summary>
/// 公开消息写不进去时，主持台要真的挂出横幅，而不是只在 ViewModel 里有个标志。
///
/// 横幅要写着「同步异常」、带一个能点的「重试同步」。
/// </summary>
public sealed class PublicSyncBannerTests
{
    [Fact]
    public async Task Sync_failure_raises_a_visible_banner_with_a_retry()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var dbPath = Path.Combine(root.Path, "sessions.db");
            using var store = new SessionStore(dbPath);
            await store.InitializeAsync(CancellationToken.None);
            await BreakPublicMessageTableAsync(dbPath);

            var host = NewHost(store);
            await host.LoadDemoAsync();

            var view = new HostView { DataContext = host };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            WpfRunner.ShowOffscreen(window);
            try
            {
                await WaitForRenderAsync();

                var banner = view.FindName("PublicSyncErrorBanner") as Border;
                Assert.NotNull(banner);
                Assert.Equal(Visibility.Collapsed, banner.Visibility);

                // 说一句话触发写入；写入必然失败
                host.InputText = "说一句话。";
                await host.SendAsync();
                await WaitUntilAsync(() => host.HasPublicSyncError);
                await WaitForRenderAsync();

                Assert.Equal(Visibility.Visible, banner.Visibility);
                Assert.Contains("同步异常", host.PublicSyncErrorText, StringComparison.Ordinal);
                Assert.True(host.RetryPublicSyncCommand.CanExecute(null), "同步异常时必须能重试。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static HostViewModel NewHost(ISessionStore store)
    {
        var tts = new SilentTts();
        var settings = ToolSettings.Default with { ModelId = "ui-test-model", AutoRead = false };
        return new HostViewModel(
            new HostDirector(new EchoGateway(), tts),
            () => settings,
            new HostIntentRouter(),
            new CommandDispatcher(store),
            store,
            tts);
    }

    /// <summary>把公开消息表换成一条永远不通过的约束：写进去就失败。</summary>
    private static async Task BreakPublicMessageTableAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DROP TABLE public_messages;" +
            "CREATE TABLE public_messages (" +
            "session_id TEXT NOT NULL, sequence INTEGER NOT NULL, message_id TEXT NOT NULL," +
            "author INTEGER NOT NULL, kind INTEGER NOT NULL, body TEXT NOT NULL, created_at TEXT NOT NULL," +
            "PRIMARY KEY (session_id, sequence), UNIQUE (session_id, message_id)," +
            "CHECK (1 = 0));";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitForRenderAsync()
    {
        await Task.Delay(120);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(60);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
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
        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

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
                "localdm-pubsync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
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
