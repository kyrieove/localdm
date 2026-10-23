using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using LocalDm.App;
using LocalDm.App.Demo;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Storage;
using LocalDm.Tablet;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

namespace LocalDm.UiTests;

public sealed class TabletUiE2ETests
{
    private static async Task<(WebApplication App, int Port, HostViewModel Host, TabletSpeechE2ETests.GatedGateway Gateway, TabletSpeechE2ETests.QuietTts Tts)> StartAsync(string root)
    {
        var (host, gateway, tts) = await TabletSpeechE2ETests.CreateHostAsync(root);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var feed = new CurrentSessionPublicFeedSource(new StaticMessageFeed(), () => host.Session);
        var port = TabletSpeechE2ETests.FreePort();
        var app = TabletWebHost.Create(
            new TabletSpeechE2ETests.FakeContent(),
            feed,
            ["--urls", $"http://127.0.0.1:{port}"],
            root,
            new TabletSpeechBridge(host, dispatcher),
            ui: new CurrentSessionTabletUiSource(feed, () => host.Session, dispatcher));
        await app.StartAsync();
        return (app, port, host, gateway, tts);
    }

    [Fact]
    public async Task Ui_requires_a_session_cookie_and_valid_after()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TabletSpeechE2ETests.TempRoot();
            var (app, port, host, gateway, tts) = await StartAsync(root.Path);
            try
            {
                using var anonClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                var unauthResp = await anonClient.GetAsync("tablet/ui");
                Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

                using var client = await TabletSpeechE2ETests.NewTablet(port, "role-1");
                var badQueryResp = await client.GetAsync("tablet/ui?after=abc");
                Assert.Equal(HttpStatusCode.BadRequest, badQueryResp.StatusCode);

                var negQueryResp = await client.GetAsync("tablet/ui?after=-1");
                Assert.Equal(HttpStatusCode.BadRequest, negQueryResp.StatusCode);
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await TabletSpeechE2ETests.WaitUntilAsync(() => !host.IsBusy);
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
    public async Task Ui_returns_projection_of_the_loaded_session()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TabletSpeechE2ETests.TempRoot();
            var (app, port, host, gateway, tts) = await StartAsync(root.Path);
            try
            {
                using var client = await TabletSpeechE2ETests.NewTablet(port, "role-1");
                var resp = await client.GetAsync("tablet/ui?after=0");
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(host.Session.Session!.SessionId, json.GetProperty("sessionId").GetString());
                Assert.Equal(host.Session.PhaseTitle, json.GetProperty("phaseTitle").GetString());
                Assert.Equal(host.Session.Projection!.PhaseTrack.Count, json.GetProperty("phases").GetArrayLength());
                Assert.Equal(7, json.GetProperty("controls").GetArrayLength());
                Assert.Equal("请在电脑上操作", json.GetProperty("controlsNote").GetString());
                var messages = json.GetProperty("messages");
                Assert.Equal(2, messages.GetArrayLength());
                Assert.Equal("【甲】大家好", messages[0].GetProperty("text").GetString());
                Assert.Equal(2, json.GetProperty("nextSequence").GetInt64());
                Assert.False(json.GetProperty("hostBusy").GetBoolean());

                var after2Resp = await client.GetAsync("tablet/ui?after=2");
                Assert.Equal(HttpStatusCode.OK, after2Resp.StatusCode);
                var after2Json = await after2Resp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(0, after2Json.GetProperty("messages").GetArrayLength());
                Assert.Equal(2, after2Json.GetProperty("nextSequence").GetInt64());
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await TabletSpeechE2ETests.WaitUntilAsync(() => !host.IsBusy);
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
    public async Task Ui_shows_host_busy_while_model_is_answering()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TabletSpeechE2ETests.TempRoot();
            var (app, port, host, gateway, tts) = await StartAsync(root.Path);
            try
            {
                gateway.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var client = await TabletSpeechE2ETests.NewTablet(port, "role-1");
                var sayResp = await client.PostAsJsonAsync("tablet/say", new { text = "你好" });
                Assert.Equal(HttpStatusCode.Accepted, sayResp.StatusCode);

                var busyResp = await client.GetAsync("tablet/ui");
                Assert.Equal(HttpStatusCode.OK, busyResp.StatusCode);
                var busyJson = await busyResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(busyJson.GetProperty("hostBusy").GetBoolean());

                gateway.Gate.TrySetResult();
                await TabletSpeechE2ETests.WaitUntilAsync(() => !host.IsBusy);

                var freeResp = await client.GetAsync("tablet/ui");
                Assert.Equal(HttpStatusCode.OK, freeResp.StatusCode);
                var freeJson = await freeResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.False(freeJson.GetProperty("hostBusy").GetBoolean());
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await TabletSpeechE2ETests.WaitUntilAsync(() => !host.IsBusy);
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
    public async Task Ui_body_contains_no_private_segment_text_or_internal_state()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TabletSpeechE2ETests.TempRoot();
            var (app, port, host, gateway, tts) = await StartAsync(root.Path);
            try
            {
                var package = DemoPackage.Create();
                var secret = package.Segments.Where(s => s.Visibility != SegmentVisibility.Public).ToList();
                Assert.NotEmpty(secret);

                using var client = await TabletSpeechE2ETests.NewTablet(port, "role-1");
                var body = await client.GetStringAsync("tablet/ui");

                foreach (var s in secret)
                {
                    Assert.DoesNotContain(s.OriginalText, body);
                    Assert.DoesNotContain(s.SegmentId, body);
                }

                Assert.DoesNotContain(host.ContextUsageText, body);
                Assert.DoesNotContain("上下文", body);
                Assert.DoesNotContain("备忘", body);
                if (host.HasMemo && host.Memo is not null)
                {
                    Assert.DoesNotContain(host.Memo, body);
                }
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                try
                {
                    await TabletSpeechE2ETests.WaitUntilAsync(() => !host.IsBusy);
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
    public async Task Ui_is_not_ready_without_a_ui_source()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TabletSpeechE2ETests.TempRoot();
            var port = TabletSpeechE2ETests.FreePort();
            var app = TabletWebHost.Create(
                new TabletSpeechE2ETests.FakeContent(),
                new EmptyPublicFeedSource(),
                ["--urls", $"http://127.0.0.1:{port}"],
                root.Path);
            await app.StartAsync();

            try
            {
                using var client = await TabletSpeechE2ETests.NewTablet(port, "role-1");
                var resp = await client.GetAsync("tablet/ui");
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("NOT_READY", json.GetProperty("code").GetString());
                Assert.Equal("等待主持人开始这一局", json.GetProperty("message").GetString());
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        });
    }

    [Fact]
    public async Task Source_returns_null_when_feed_belongs_to_another_session()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TabletSpeechE2ETests.TempRoot();
            var (host, gateway, tts) = await TabletSpeechE2ETests.CreateHostAsync(root.Path);
            try
            {
                var source1 = new CurrentSessionTabletUiSource(new FixedFeed("other-session"), () => host.Session, Dispatcher.CurrentDispatcher);
                var result1 = await source1.ReadAsync(0, false, false, CancellationToken.None);
                Assert.Null(result1);

                var source2 = new CurrentSessionTabletUiSource(new FixedFeed(string.Empty), () => host.Session, Dispatcher.CurrentDispatcher);
                var result2 = await source2.ReadAsync(0, false, false, CancellationToken.None);
                Assert.Null(result2);
            }
            finally
            {
                gateway.Gate.TrySetResult();
                tts.SpeakGate.TrySetResult();
                await TabletSpeechE2ETests.WaitUntilAsync(() => !host.IsBusy);
            }
        });
    }

    private sealed class FixedFeed(string id) : IPublicFeedSource
    {
        public Task<PublicFeedSnapshot> ReadAsync(long after, CancellationToken cancellationToken) =>
            Task.FromResult(new PublicFeedSnapshot
            {
                SessionId = id,
                Messages = [],
                NextSequence = after,
            });
    }

    private sealed class StaticMessageFeed : IPublicMessageFeed
    {
        public Task<PublicMessage> AppendPublicMessageAsync(PublicMessage message, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PublicMessage>> ReadPublicMessagesAsync(
            string sessionId,
            long after,
            int limit,
            CancellationToken cancellationToken)
        {
            var all = new List<PublicMessage>
            {
                new()
                {
                    Sequence = 1,
                    SessionId = sessionId,
                    MessageId = "msg-1",
                    Author = PublicMessageAuthor.Player,
                    Text = "【甲】大家好",
                    Kind = PublicMessageKind.PlayerSpeech,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                },
                new()
                {
                    Sequence = 2,
                    SessionId = sessionId,
                    MessageId = "msg-2",
                    Author = PublicMessageAuthor.Host,
                    Text = "欢迎各位",
                    Kind = PublicMessageKind.HostReply,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                },
            };

            var matched = all.Where(m => m.Sequence > after).Take(limit).ToList();
            return Task.FromResult<IReadOnlyList<PublicMessage>>(matched);
        }
    }
}
