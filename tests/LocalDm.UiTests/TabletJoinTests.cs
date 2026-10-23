using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
                    await Task.Delay(1500);
                    Assert.True(shell.Setup.Tablet!.IsRunning);
                    Assert.Equal("关闭平板连接", shell.Setup.Tablet.ToggleLabel);
                    button.Command.Execute(null);
                    await Task.Delay(500);
                    Assert.False(shell.Setup.Tablet!.IsRunning);
                    Assert.Null(new HostView().FindName("TabletJoinButton"));
                }
                finally { window.Close(); }
            }
            finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
        });
    }

    [Fact]
    public async Task Library_page_shows_join_card_before_any_script_is_opened()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "localdm-join-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var context = await SessionContext.OpenAsync(root);
                using var composition = await AppComposition.CreateFromContextAsync(context, CancellationToken.None);
                var shell = await composition.CreateShellAsync(CancellationToken.None);
                await shell.Library.Tablet!.StartAsync();
                var view = new LibraryView { DataContext = shell.Library };
                var window = new Window { Content = view, Width = 1200, Height = 800 };
                WpfRunner.ShowOffscreen(window);
                try
                {
                    Assert.NotNull(view.FindName("TabletJoinCard"));
                    var headline = Assert.IsType<TextBlock>(view.FindName("TabletJoinHeadline"));
                    Assert.Equal("平板可以加入", headline.Text);
                    var qr = Assert.IsType<Image>(view.FindName("TabletJoinQr"));
                    Assert.NotNull(qr.Source);
                }
                finally
                {
                    window.Close();
                    await shell.Library.Tablet!.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    [Fact]
    public async Task Demo_package_gets_no_tablet_materials()
    {
        await WpfRunner.RunAsync(async () =>
        {
            await using var join = new TabletJoinViewModel(new LocalDm.Tablet.EmptyPublicFeedSource());
            await join.StartAsync();
            Assert.True(join.IsRunning);
            await join.SetPackageAsync(DemoPackage.Create(), "unused.json");
            Assert.Contains("平板材料", join.Status, StringComparison.Ordinal);
            Assert.Contains("演示剧本", join.Status, StringComparison.Ordinal);
            using var client = new HttpClient { BaseAddress = new Uri(join.LocalUrl) };
            using var catalog = JsonDocument.Parse(await client.GetStringAsync("tablet/catalog"));
            Assert.Equal(0, catalog.RootElement.GetProperty("roles").GetArrayLength());
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
                await join.StartAsync();
                Assert.True(join.IsRunning);
                await join.SetPackageAsync(package, Path.Combine(root, "packages", "current.json"));
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
                Assert.True(join.IsRunning);
                using var clientAfter = new HttpClient { BaseAddress = new Uri(oldUrl) };
                using var clearedCatalog = JsonDocument.Parse(await clientAfter.GetStringAsync("tablet/catalog"));
                Assert.Equal(0, clearedCatalog.RootElement.GetProperty("roles").GetArrayLength());
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    [Fact]
    public async Task Port_busy_falls_back_and_tells_the_user()
    {
        await WpfRunner.RunAsync(async () =>
        {
            var listener = new TcpListener(IPAddress.Any, 5188);
            try
            {
                listener.Start();
            }
            catch
            {
                return;
            }

            try
            {
                await using var join = new TabletJoinViewModel(new LocalDm.Tablet.EmptyPublicFeedSource());
                await join.StartAsync();
                Assert.True(join.IsRunning);
                Assert.True(join.HasError);
                Assert.Contains("5188", join.ErrorText);
                Assert.DoesNotContain(":5188/", join.LocalUrl);
            }
            finally
            {
                listener.Stop();
            }
        });
    }
}
