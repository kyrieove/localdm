using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using LocalDm.App;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Storage;
using LocalDm.Tablet;
using Microsoft.Data.Sqlite;

namespace LocalDm.UiTests;

public sealed class TabletSpeechE2ETests
{
    [Fact]
    public async Task Two_tablets_post_at_once_only_one_is_accepted()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var (host, gateway, tts) = await CreateHostAsync(root.Path);
            gateway.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var port = FreePort();
            var app = TabletWebHost.Create(
                new FakeContent(),
                new EmptyPublicFeedSource(),
                ["--urls", $"http://127.0.0.1:{port}"],
                root.Path,
                new TabletSpeechBridge(host, Dispatcher.CurrentDispatcher));
            await app.StartAsync();

            try
            {
                using var clientA = await NewTablet(port, "role-1");
                using var clientB = await NewTablet(port, "role-2");

                var taskA = Say(clientA, "一");
                var taskB = Say(clientB, "二");
                var responses = await Task.WhenAll(taskA, taskB);

                var acceptedCount = responses.Count(r => r.StatusCode == HttpStatusCode.Accepted);
                var busyCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

                Assert.Equal(1, acceptedCount);
                Assert.Equal(1, busyCount);

                var busyResp = responses.First(r => r.StatusCode == HttpStatusCode.Conflict);
                var busyJson = await busyResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("BUSY", busyJson.GetProperty("code").GetString());
                Assert.Equal("主持正在回应", busyJson.GetProperty("message").GetString());

                gateway.Gate.TrySetResult();
                await WaitUntilAsync(() => !host.IsBusy);

                Assert.Equal(1, gateway.CompletionCount);
                var playerMessages = host.Messages.Where(m => m.IsPlayer).ToList();
                Assert.Single(playerMessages);
                Assert.StartsWith("【", playerMessages[0].Text, StringComparison.Ordinal);
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await WaitUntilAsync(() => !host.IsBusy);
                }
                finally
                {
                    await app.StopAsync();
                    await app.DisposeAsync();
                }
            }
        });
    }

    [Fact]
    public async Task Desktop_send_and_tablet_post_at_once_only_one_is_accepted()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var (host, gateway, tts) = await CreateHostAsync(root.Path);
            gateway.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var port = FreePort();
            var app = TabletWebHost.Create(
                new FakeContent(),
                new EmptyPublicFeedSource(),
                ["--urls", $"http://127.0.0.1:{port}"],
                root.Path,
                new TabletSpeechBridge(host, Dispatcher.CurrentDispatcher));
            await app.StartAsync();

            try
            {
                using var client = await NewTablet(port, "role-1");

                host.InputText = "电脑的话";
                var desktop = host.SendAsync();

                var tabletResp = await Say(client, "平板的话");
                Assert.Equal(HttpStatusCode.Conflict, tabletResp.StatusCode);
                var busyJson = await tabletResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("BUSY", busyJson.GetProperty("code").GetString());

                gateway.Gate.TrySetResult();
                await desktop;
                await WaitUntilAsync(() => !host.IsBusy);

                Assert.Equal(1, gateway.CompletionCount);
                var playerMessages = host.Messages.Where(m => m.IsPlayer).ToList();
                Assert.Single(playerMessages);
                Assert.Equal("电脑的话", playerMessages[0].Text);
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await WaitUntilAsync(() => !host.IsBusy);
                }
                finally
                {
                    await app.StopAsync();
                    await app.DisposeAsync();
                }
            }
        });
    }

    [Fact]
    public async Task Tablet_post_while_host_is_speaking_is_busy_and_can_be_stopped()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var (host, gateway, tts) = await CreateHostAsync(root.Path, autoRead: true);
            gateway.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.Gate.SetResult();
            tts.SpeakGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var port = FreePort();
            var app = TabletWebHost.Create(
                new FakeContent(),
                new EmptyPublicFeedSource(),
                ["--urls", $"http://127.0.0.1:{port}"],
                root.Path,
                new TabletSpeechBridge(host, Dispatcher.CurrentDispatcher));
            await app.StartAsync();

            try
            {
                using var clientA = await NewTablet(port, "role-1");
                using var clientB = await NewTablet(port, "role-2");

                var respA = await Say(clientA, "说话");
                Assert.Equal(HttpStatusCode.Accepted, respA.StatusCode);

                await WaitUntilAsync(() => host.IsSpeaking);

                var respB = await Say(clientB, "插话");
                Assert.Equal(HttpStatusCode.Conflict, respB.StatusCode);

                var pubResp = await clientA.GetAsync("tablet/public");
                pubResp.EnsureSuccessStatusCode();
                var pubJson = await pubResp.Content.ReadFromJsonAsync<JsonElement>();
                var hostElem = pubJson.GetProperty("host");
                Assert.True(hostElem.GetProperty("speaking").GetBoolean());
                Assert.True(hostElem.GetProperty("busy").GetBoolean());

                var stopResp = await clientA.PostAsync("tablet/say/stop", null);
                stopResp.EnsureSuccessStatusCode();
                var stopJson = await stopResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(stopJson.GetProperty("stopped").GetBoolean());

                // 不放开朗读门：只有「停止」本身能让主持台恢复空闲。
                await WaitUntilAsync(() => !host.IsBusy);
                Assert.False(host.IsSpeaking);
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await WaitUntilAsync(() => !host.IsBusy);
                }
                finally
                {
                    await app.StopAsync();
                    await app.DisposeAsync();
                }
            }
        });
    }

    [Fact]
    public async Task Many_tablets_post_at_once_exactly_one_reaches_the_model()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var (host, gateway, tts) = await CreateHostAsync(root.Path);
            gateway.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var port = FreePort();
            var app = TabletWebHost.Create(
                new FakeContent(),
                new EmptyPublicFeedSource(),
                ["--urls", $"http://127.0.0.1:{port}"],
                root.Path,
                new TabletSpeechBridge(host, Dispatcher.CurrentDispatcher));
            await app.StartAsync();

            try
            {
                var clients = new List<HttpClient>();
                for (var i = 1; i <= 6; i++)
                {
                    clients.Add(await NewTablet(port, $"role-{i}"));
                }

                var tasks = clients.Select((c, idx) => Say(c, $"消息{idx}")).ToArray();
                var responses = await Task.WhenAll(tasks);

                var acceptedCount = responses.Count(r => r.StatusCode == HttpStatusCode.Accepted);
                var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

                Assert.Equal(1, acceptedCount);
                Assert.Equal(5, conflictCount);

                gateway.Gate.TrySetResult();
                await WaitUntilAsync(() => !host.IsBusy);

                Assert.Equal(1, gateway.CompletionCount);

                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await WaitUntilAsync(() => !host.IsBusy);
                }
                finally
                {
                    await app.StopAsync();
                    await app.DisposeAsync();
                }
            }
        });
    }

    [Fact]
    public async Task Tablet_advance_phrase_is_a_proposal_and_does_not_advance()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var (host, gateway, tts) = await CreateHostAsync(root.Path);
            var initialRevision = host.Session.Revision;

            var port = FreePort();
            var app = TabletWebHost.Create(
                new FakeContent(),
                new EmptyPublicFeedSource(),
                ["--urls", $"http://127.0.0.1:{port}"],
                root.Path,
                new TabletSpeechBridge(host, Dispatcher.CurrentDispatcher));
            await app.StartAsync();

            try
            {
                using var client = await NewTablet(port, "role-1");
                var resp = await Say(client, "进入下一环节");
                Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("PROPOSED", json.GetProperty("code").GetString());

                await WaitUntilAsync(() => !host.IsBusy);

                Assert.Equal(0, gateway.CompletionCount);
                Assert.Equal(initialRevision, host.Session.Revision);
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await WaitUntilAsync(() => !host.IsBusy);
                }
                finally
                {
                    await app.StopAsync();
                    await app.DisposeAsync();
                }
            }
        });
    }

    private static async Task<(HostViewModel Host, GatedGateway Gateway, QuietTts Tts)> CreateHostAsync(string root, bool autoRead = false)
    {
        var dbPath = Path.Combine(root, "sessions.db");
        var store = new SessionStore(dbPath);
        await store.InitializeAsync(CancellationToken.None);

        var gateway = new GatedGateway();
        var tts = new QuietTts();
        var settings = ToolSettings.Default with { ModelId = "ui-test-model", AutoRead = autoRead };
        var host = new HostViewModel(
            new HostDirector(gateway, tts),
            () => settings,
            new HostIntentRouter(),
            new CommandDispatcher(store),
            store,
            tts);
        await host.LoadDemoAsync();
        return (host, gateway, tts);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<HttpClient> NewTablet(int port, string roleId)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        };

        var claimResp = await client.PostAsync($"tablet/claim?roleId={roleId}", null);
        claimResp.EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> Say(HttpClient c, string text) =>
        c.PostAsJsonAsync("tablet/say", new { text });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(30);
        }

        Assert.True(condition(), "等待条件超时。");
    }

    private sealed class GatedGateway : IMagpieGateway
    {
        private int _count;

        public TaskCompletionSource Gate { get; set; } =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CompletionCount => Volatile.Read(ref _count);

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["ui-test-model"]);

        public async Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            await Gate.Task;
            return "这是端到端测试的主持回复。";
        }
    }

    private sealed class QuietTts : ITtsService
    {
        private bool _isSpeaking;

        /// <summary>默认已放开；要卡住朗读的用例再换成未完成的。</summary>
        public TaskCompletionSource SpeakGate { get; set; } = Opened();

        private static TaskCompletionSource Opened()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult();
            return tcs;
        }

        public bool IsSpeaking => _isSpeaking;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

        public async Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
        {
            _isSpeaking = true;
            try
            {
                await SpeakGate.Task;
            }
            finally
            {
                _isSpeaking = false;
            }
        }

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop()
        {
            _isSpeaking = false;
            SpeakGate.TrySetResult();
        }
    }

    private sealed class FakeContent : ITabletContent
    {
        private static readonly string[] Names = ["甲", "乙", "丙", "丁", "戊", "己"];

        public string StoryTitle => "端到端";

        public IReadOnlyList<TabletRole> Roles { get; } =
            Names.Select((name, i) => new TabletRole($"role-{i + 1}", name)).ToArray();

        public IReadOnlyList<TabletCatalogItem> Cards => [];

        public IReadOnlyDictionary<string, RoleBookText> RoleBooks =>
            new Dictionary<string, RoleBookText>();

        public bool TryGetCardImage(
            string cardId,
            TabletCardSide side,
            out byte[] bytes,
            out string contentType,
            out TabletQueryFailure failure)
        {
            bytes = [];
            contentType = "";
            failure = TabletQueryFailure.NotFound;
            return false;
        }

        public bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType)
        {
            bytes = [];
            contentType = "";
            return false;
        }
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "localdm-e2espeech-" + Guid.NewGuid().ToString("N"));
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
