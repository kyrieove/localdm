using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LocalDm.App;
using LocalDm.App.Demo;
using LocalDm.App.ViewModels;
using LocalDm.App.Views;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.UiTests;

public sealed class TabletJoinTests
{
    [Fact]
    public async Task Switching_and_shutdown_wait_for_the_whole_desktop_change_without_blocking_ui()
    {
        await WpfRunner.RunAsync(async () =>
        {
            await using var join = new TabletJoinViewModel(new LocalDm.Tablet.EmptyPublicFeedSource());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new List<string>();
            var first = join.SetPackageAsync(DemoPackage.Create() with { Title = "A" }, "a.json", async () =>
            {
                events.Add("A开始");
                entered.SetResult();
                await release.Task;
                events.Add("A完成");
            });
            await entered.Task;
            var second = join.SetPackageAsync(DemoPackage.Create() with { Title = "B" }, "b.json", () =>
            {
                events.Add("B装载");
                return Task.CompletedTask;
            });
            var shutdown = join.DisposeAsync().AsTask();
            var lateStart = join.StartAsync();
            Assert.False(second.IsCompleted);
            Assert.False(shutdown.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, second, shutdown, lateStart).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "A开始", "A完成" }, events);
            Assert.Equal("A", join.StoryTitle);
            Assert.False(join.IsRunning);
        });
    }

    [Fact]
    public async Task Settings_connection_button_shows_current_status_without_a_popup()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "localdm-join-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var context = await SessionContext.OpenAsync(root);
                using var composition = await AppComposition.CreateFromContextAsync(context, CancellationToken.None);
                var shell = await composition.CreateShellAsync(CancellationToken.None);
                var view = new SetupView { DataContext = shell.Setup };
                var window = new Window { Content = view, Width = 1200, Height = 800 };
                WpfRunner.ShowOffscreen(window);
                try
                {
                    var button = Assert.IsType<Button>(view.FindName("TabletConnectionToggle"));
                    Assert.True(button.IsEnabled);
                    Assert.Equal("开启平板连接", button.Content);
                    button.Command.Execute(null);
                    await Task.Yield();
                    Assert.False(shell.Setup.Tablet!.IsRunning);
                    Assert.Contains("平板清单", shell.Setup.Tablet.Status, StringComparison.Ordinal);
                    Assert.Null(new HostView().FindName("TabletJoinButton"));
                }
                finally { window.Close(); }
            }
            finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
        });
    }

    [Fact]
    public async Task Unprepared_package_never_starts_demo_service()
    {
        await WpfRunner.RunAsync(async () =>
        {
            await using var join = new TabletJoinViewModel(new LocalDm.Tablet.EmptyPublicFeedSource());
            await join.SetPackageAsync(DemoPackage.Create(), "unused.json");
            await join.StartAsync();
            Assert.False(join.IsRunning);
            Assert.Empty(join.LocalUrl);
            Assert.Null(join.QrCode);
            Assert.Contains("平板清单", join.Status, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Same_process_service_serves_current_package_and_saved_public_messages_then_stops_on_switch()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "localdm-join-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
            await File.WriteAllTextAsync(Path.Combine(root, "wwwroot", "custom.css"), "body { color: red; }");
            try
            {
                using var store = new SessionStore(Path.Combine(root, "sessions.db"));
                await store.InitializeAsync(CancellationToken.None);
                var package = DemoPackage.Create() with { Title = "当前测试包", Tablet = TabletManifest.Empty };
                var session = new Session
                {
                    SessionId = "current-tablet-session", PackageId = package.PackageId,
                    PackageVersion = package.Version, ManifestHash = package.ManifestHash,
                    State = SessionState.Prepared, CurrentPhaseId = package.Phases[0].PhaseId,
                    Revision = 0, ReleasedMaterialIds = [],
                };
                var view = new SessionViewModel(new CommandDispatcher(store));
                view.Attach(package, session);
                await store.AppendPublicMessageAsync(new PublicMessage
                {
                    SessionId = session.SessionId, MessageId = "public-one", Author = PublicMessageAuthor.Player,
                    Kind = PublicMessageKind.PlayerSpeech, Text = "公开测试发言", CreatedAt = DateTimeOffset.UtcNow,
                }, CancellationToken.None);
                await using var join = new TabletJoinViewModel(new CurrentSessionPublicFeedSource(store, () => view), root);
                await join.SetPackageAsync(package, Path.Combine(root, "packages", "current.json"));
                await join.StartAsync();
                Assert.True(join.IsRunning, join.Status);
                Assert.NotNull(join.QrCode);
                Assert.True(join.QrCode.PixelWidth > 200);
                using var client = new HttpClient { BaseAddress = new Uri(join.LocalUrl) };
                using var joined = await client.GetAsync("/", TestContext.Current.CancellationToken);
                joined.EnsureSuccessStatusCode();
                using var catalog = JsonDocument.Parse(await client.GetStringAsync("tablet/catalog"));
                Assert.Equal("当前测试包", catalog.RootElement.GetProperty("storyTitle").GetString());
                using var feed = JsonDocument.Parse(await client.GetStringAsync("tablet/public?after=0"));
                Assert.Equal("current-tablet-session", feed.RootElement.GetProperty("sessionId").GetString());
                Assert.Equal("公开测试发言", feed.RootElement.GetProperty("messages")[0].GetProperty("text").GetString());
                Assert.Equal(1, feed.RootElement.GetProperty("nextSequence").GetInt32());
                Assert.Equal("body { color: red; }", await client.GetStringAsync("custom.css"));
                await File.WriteAllTextAsync(Path.Combine(root, "wwwroot", "custom.css"), "body { color: blue; }");
                Assert.Equal("body { color: blue; }", await client.GetStringAsync("custom.css"));
                var oldUrl = join.LocalUrl;
                await join.SetPackageAsync(DemoPackage.Create(), null);
                Assert.False(join.IsRunning);
                Assert.Null(join.QrCode);
                using var stopped = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var failure = await Record.ExceptionAsync(() => stopped.GetAsync(oldUrl + "tablet/catalog", TestContext.Current.CancellationToken));
                Assert.True(failure is HttpRequestException or TaskCanceledException, "关闭旧局后不得再取得目录响应。");
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }
}
