using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using LocalDm.Core.Tablet;
using LocalDm.Tablet;

namespace LocalDm.Tests;

public sealed class TabletSpeechTests
{
    private sealed class FakeSpeech : ITabletSpeech
    {
        public TabletSpeechOutcome Outcome { get; set; } = TabletSpeechOutcome.Accepted;
        public bool Busy { get; set; }
        public bool Speaking { get; set; }
        public bool IsBusy => Busy;
        public bool IsSpeaking => Speaking;

        public List<(string role, string text)> Calls { get; } = [];
        public int StopCalls { get; private set; }

        public Task<TabletSpeechAck> SubmitAsync(string roleName, string text)
        {
            if (Outcome is TabletSpeechOutcome.Accepted or TabletSpeechOutcome.Proposed)
            {
                lock (Calls)
                {
                    Calls.Add((roleName, text));
                }

                return Task.FromResult(new TabletSpeechAck(Outcome, Task.CompletedTask));
            }

            return Task.FromResult(TabletSpeechAck.Rejected(Outcome));
        }

        public bool StopSpeaking()
        {
            StopCalls++;
            return true;
        }
    }

    private sealed class AtomicallyBusyFakeSpeech : ITabletSpeech
    {
        private int _isBusy;

        public bool IsBusy => Volatile.Read(ref _isBusy) == 1;
        public bool IsSpeaking => false;

        public List<(string role, string text)> Calls { get; } = [];

        public Task<TabletSpeechAck> SubmitAsync(string roleName, string text)
        {
            // 第一个成功进入的调用把 busy 置为 1
            if (Interlocked.CompareExchange(ref _isBusy, 1, 0) == 0)
            {
                lock (Calls)
                {
                    Calls.Add((roleName, text));
                }

                return Task.FromResult(new TabletSpeechAck(TabletSpeechOutcome.Accepted, Task.CompletedTask));
            }

            return Task.FromResult(TabletSpeechAck.Rejected(TabletSpeechOutcome.Busy));
        }

        public bool StopSpeaking() => false;
    }

    private sealed class ManualTime(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class MinimalTabletContent : ITabletContent
    {
        public string StoryTitle => "测试剧本";
        public IReadOnlyList<TabletRole> Roles { get; } =
        [
            new TabletRole("role-1", "角色甲"),
            new TabletRole("role-2", "角色乙"),
        ];

        public IReadOnlyList<TabletCatalogItem> Cards => [];
        public IReadOnlyDictionary<string, RoleBookText> RoleBooks => new Dictionary<string, RoleBookText>();

        public bool TryGetCardImage(string cardId, TabletCardSide side, out byte[] bytes, out string contentType, out TabletQueryFailure failure)
        {
            bytes = [];
            contentType = string.Empty;
            failure = TabletQueryFailure.AssetMissing;
            return false;
        }

        public bool TryGetRoleSourcePage(string roleId, int pageNumber, out byte[] bytes, out string contentType)
        {
            bytes = [];
            contentType = string.Empty;
            return false;
        }
    }

    [Fact]
    public async Task Missing_key_is_rejected()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        var speech = new FakeSpeech();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var nullResult = await service.SayAsync(null, "你好");
        var emptyResult = await service.SayAsync("", "你好");

        Assert.Equal(TabletSayCode.NoSessionKey, nullResult.Code);
        Assert.Equal(401, nullResult.HttpStatus);
        Assert.Equal("NO_SESSION_KEY", nullResult.CodeText);
        Assert.Equal("请先从电脑上的二维码或网址重新打开本页。", nullResult.Message);

        Assert.Equal(TabletSayCode.NoSessionKey, emptyResult.Code);
        Assert.Empty(speech.Calls);
    }

    [Fact]
    public async Task Unclaimed_session_cannot_speak()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        var speech = new FakeSpeech();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var result = await service.SayAsync("session-1", "你好");

        Assert.Equal(TabletSayCode.NoRole, result.Code);
        Assert.Equal(403, result.HttpStatus);
        Assert.Equal("NO_ROLE", result.CodeText);
        Assert.Equal("请先认领角色", result.Message);
        Assert.Empty(speech.Calls);
    }

    [Fact]
    public async Task Empty_and_too_long_are_rejected()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        state.TryClaim("session-1", "role-1");
        var speech = new FakeSpeech();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var empty = await service.SayAsync("session-1", "   ");
        Assert.Equal(TabletSayCode.Empty, empty.Code);
        Assert.Equal(400, empty.HttpStatus);
        Assert.Equal("EMPTY", empty.CodeText);
        Assert.Equal("说点什么再发送。", empty.Message);

        var over300 = new string('字', 301);
        var tooLong = await service.SayAsync("session-1", over300);
        Assert.Equal(TabletSayCode.TooLong, tooLong.Code);
        Assert.Equal(400, tooLong.HttpStatus);
        Assert.Equal("TOO_LONG", tooLong.CodeText);
        Assert.Equal("单条最多 300 字。", tooLong.Message);

        var exactly300 = new string('字', 300);
        var accepted = await service.SayAsync("session-1", exactly300);
        Assert.Equal(TabletSayCode.Accepted, accepted.Code);
        Assert.Equal(202, accepted.HttpStatus);
        Assert.Equal("ACCEPTED", accepted.CodeText);
        Assert.Single(speech.Calls);
    }

    [Fact]
    public async Task Second_send_within_two_seconds_is_too_fast()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        state.TryClaim("session-1", "role-1");
        state.TryClaim("session-2", "role-2");

        var speech = new FakeSpeech();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var first = await service.SayAsync("session-1", "第一句");
        Assert.Equal(TabletSayCode.Accepted, first.Code);

        time.Advance(TimeSpan.FromSeconds(1.9));
        var tooFast = await service.SayAsync("session-1", "第二句");
        Assert.Equal(TabletSayCode.TooFast, tooFast.Code);
        Assert.Equal(429, tooFast.HttpStatus);
        Assert.Equal("TOO_FAST", tooFast.CodeText);
        Assert.Equal("发得太快了，请稍等一下。", tooFast.Message);

        // 另一个平板不受影响
        var other = await service.SayAsync("session-2", "另一台发");
        Assert.Equal(TabletSayCode.Accepted, other.Code);

        // 达到 2.0 秒后 session-1 恢复正常
        time.Advance(TimeSpan.FromSeconds(0.1));
        var ok = await service.SayAsync("session-1", "第三句");
        Assert.Equal(TabletSayCode.Accepted, ok.Code);
    }

    [Fact]
    public async Task Rejected_by_host_does_not_use_the_quota()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        state.TryClaim("session-1", "role-1");

        var speech = new FakeSpeech { Outcome = TabletSpeechOutcome.Busy };
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var busy = await service.SayAsync("session-1", "说话");
        Assert.Equal(TabletSayCode.Busy, busy.Code);
        Assert.Equal(409, busy.HttpStatus);

        // 随后主持变空闲，马上再发不消耗额度
        speech.Outcome = TabletSpeechOutcome.Accepted;
        var ok = await service.SayAsync("session-1", "重新说");
        Assert.Equal(TabletSayCode.Accepted, ok.Code);
    }

    [Fact]
    public async Task Host_busy_is_checked_before_quota()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        state.TryClaim("session-1", "role-1");

        var speech = new FakeSpeech { Busy = true };
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var result = await service.SayAsync("session-1", "你好");
        Assert.Equal(TabletSayCode.Busy, result.Code);
        Assert.Equal(409, result.HttpStatus);
        Assert.Equal("BUSY", result.CodeText);
        Assert.Equal("主持正在回应", result.Message);
        Assert.Empty(speech.Calls);
    }

    [Fact]
    public async Task Proposed_and_not_ready_map_to_the_right_codes()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        state.TryClaim("session-1", "role-1");

        var speech = new FakeSpeech();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        speech.Outcome = TabletSpeechOutcome.Proposed;
        var proposed = await service.SayAsync("session-1", "推进案");
        Assert.Equal(TabletSayCode.Proposed, proposed.Code);
        Assert.Equal(202, proposed.HttpStatus);
        Assert.Equal("PROPOSED", proposed.CodeText);
        Assert.Equal("已提议，等电脑确认", proposed.Message);

        time.Advance(TimeSpan.FromSeconds(3));
        speech.Outcome = TabletSpeechOutcome.NoSession;
        var noSession = await service.SayAsync("session-1", "发言");
        Assert.Equal(TabletSayCode.NotReady, noSession.Code);
        Assert.Equal(409, noSession.HttpStatus);
        Assert.Equal("NOT_READY", noSession.CodeText);
        Assert.Equal("电脑还没有开始本局。", noSession.Message);

        time.Advance(TimeSpan.FromSeconds(3));
        speech.Outcome = TabletSpeechOutcome.NoModel;
        var noModel = await service.SayAsync("session-1", "发言");
        Assert.Equal(TabletSayCode.NotReady, noModel.Code);
        Assert.Equal(409, noModel.HttpStatus);
        Assert.Equal("NOT_READY", noModel.CodeText);
        Assert.Equal("电脑还没有选好主持模型。", noModel.Message);
    }

    [Fact]
    public async Task Signature_role_name_comes_from_the_claimed_role()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        state.TryClaim("session-1", "role-1");

        var speech = new FakeSpeech();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var result = await service.SayAsync("session-1", "我是角色甲");
        Assert.Equal(TabletSayCode.Accepted, result.Code);
        var call = Assert.Single(speech.Calls);
        Assert.Equal("角色甲", call.role);
        Assert.Equal("我是角色甲", call.text);
    }

    [Fact]
    public async Task Concurrent_double_tap_from_one_tablet_sends_once()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        state.TryClaim("session-1", "role-1");

        var speech = new FakeSpeech();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => service.SayAsync("session-1", "连点测试")))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r.Code == TabletSayCode.Accepted);
        Assert.Equal(19, results.Count(r => r.Code == TabletSayCode.TooFast));
        Assert.Single(speech.Calls);
    }

    [Fact]
    public async Task Two_tablets_at_once_only_one_reaches_a_busy_host()
    {
        var content = new MinimalTabletContent();
        var state = new TabletSpikeState(content);
        state.TryClaim("session-1", "role-1");
        state.TryClaim("session-2", "role-2");

        var speech = new AtomicallyBusyFakeSpeech();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var service = new TabletSpeechService(state, speech, time);

        var t1 = Task.Run(() => service.SayAsync("session-1", "平板1抢先"));
        var t2 = Task.Run(() => service.SayAsync("session-2", "平板2抢先"));

        var results = await Task.WhenAll(t1, t2);
        Assert.Single(results, r => r.Code == TabletSayCode.Accepted);
        Assert.Single(results, r => r.Code == TabletSayCode.Busy);
    }

    [Fact]
    public async Task Say_endpoint_status_codes_and_public_host_state()
    {
        var freePort = FreePort();
        var content = new MinimalTabletContent();
        var fakeSpeech = new FakeSpeech();
        var manualTime = new ManualTime(DateTimeOffset.UtcNow);
        var tempDir = Path.Combine(Path.GetTempPath(), "localdm-say-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var app = TabletWebHost.Create(
                content,
                new EmptyPublicFeedSource(),
                ["--urls", $"http://127.0.0.1:{freePort}"],
                tempDir,
                fakeSpeech,
                manualTime);

            var ct = TestContext.Current.CancellationToken;
            await app.StartAsync(ct);

            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = cookies };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{freePort}") };

            // 1. 未认领状态 POST /tablet/say 返回 403 NO_ROLE
            // 直接 POST /tablet/say 会自动因为没有 session cookie 先被拒绝，或者若通过 claim 发 cookie
            var noCookieSay = await client.PostAsJsonAsync("/tablet/say", new { text = "你好" }, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, noCookieSay.StatusCode);
            var noCookieJson = await noCookieSay.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
            Assert.Equal("NO_SESSION_KEY", noCookieJson?["code"]?.ToString());

            // 让服务发 Cookie：POST /tablet/claim?roleId=role-1 会发 Cookie 并认领，或者认领前测试 403
            // 先尝试认领一个不存在的，看是否发了 cookie
            // 参照 TabletSessions.Issue(context) 在 / 会发 cookie
            // 但如果 GET / 没有静态文件可能报 404/500，我们也可以用 GET /tablet/public 或 claim
            // 试一下 GET / 拿 Cookie
            try
            {
                await client.GetAsync("/", ct);
            }
            catch
            {
                // 若 GET / 渲染失败，由 claim 处理
            }

            // 如果此时有 cookie 了，但没认领角色，测试 POST /tablet/say → 403 NO_ROLE
            if (cookies.GetCookies(new Uri($"http://127.0.0.1:{freePort}")).Count == 0)
            {
                // 通过让认领一个合法角色，然后 release，保留 cookie 但未认领角色
                var claimTemp = await client.PostAsync("/tablet/claim?roleId=role-1", null, ct);
                Assert.Equal(HttpStatusCode.OK, claimTemp.StatusCode);
                var releaseTemp = await client.PostAsync("/tablet/release", null, ct);
                Assert.Equal(HttpStatusCode.OK, releaseTemp.StatusCode);
            }

            var unclaimedSay = await client.PostAsJsonAsync("/tablet/say", new { text = "你好" }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, unclaimedSay.StatusCode);
            var unclaimedJson = await unclaimedSay.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
            Assert.Equal("NO_ROLE", unclaimedJson?["code"]?.ToString());

            // 2. 认领后发 {"text":"你好"} → 202
            var claimResponse = await client.PostAsync("/tablet/claim?roleId=role-1", null, ct);
            Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);

            var sayResponse = await client.PostAsJsonAsync("/tablet/say", new { text = "你好" }, ct);
            Assert.Equal(HttpStatusCode.Accepted, sayResponse.StatusCode);
            var sayJson = await sayResponse.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
            Assert.Equal(true, sayJson?["ok"]?.GetValue<bool>());
            Assert.Equal("ACCEPTED", sayJson?["code"]?.ToString());

            // 3. fakeSpeech.Busy = true 后 GET /tablet/public 的 host.busy == true
            fakeSpeech.Busy = true;
            var publicResponse = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>("/tablet/public", ct);
            Assert.NotNull(publicResponse);
            var hostObj = publicResponse?["host"];
            Assert.NotNull(hostObj);
            Assert.Equal(true, hostObj?["busy"]?.GetValue<bool>());

            // 4. POST /tablet/say/stop → 200
            var stopResponse = await client.PostAsync("/tablet/say/stop", null, ct);
            Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
            var stopJson = await stopResponse.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
            Assert.Equal(true, stopJson?["stopped"]?.GetValue<bool>());

            await app.StopAsync(ct);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>平板 App 不带 Cookie，只靠 <c>X-LocalDm-Device</c> 请求头发言（ADR 0012 决定 4 修订）。</summary>
    [Fact]
    public async Task App_device_header_can_say_after_claiming_and_gets_the_contract_fields()
    {
        var freePort = FreePort();
        var tempDir = Path.Combine(Path.GetTempPath(), "localdm-say-app-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var app = TabletWebHost.Create(
                new MinimalTabletContent(),
                new EmptyPublicFeedSource(),
                ["--urls", $"http://127.0.0.1:{freePort}"],
                tempDir,
                new FakeSpeech(),
                new ManualTime(DateTimeOffset.UtcNow));
            var ct = TestContext.Current.CancellationToken;
            await app.StartAsync(ct);

            using var handler = new HttpClientHandler { UseCookies = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{freePort}") };
            client.DefaultRequestHeaders.Add("X-LocalDm-Device", "device0123456789abcdef");

            var unclaimed = await client.PostAsJsonAsync("/tablet/say", new { text = "你好" }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, unclaimed.StatusCode);
            Assert.Equal("NO_ROLE", (await unclaimed.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct))?["code"]?.ToString());

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);
            var said = await client.PostAsJsonAsync("/tablet/say", new { text = "你好" }, ct);
            Assert.Equal(HttpStatusCode.Accepted, said.StatusCode);
            var json = await said.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
            // App 读这三个字段（apps/tablet/lib/data/app_state.dart 的 say）
            Assert.Equal(true, json?["ok"]?.GetValue<bool>());
            Assert.Equal("ACCEPTED", json?["code"]?.ToString());
            Assert.Equal("已发送", json?["message"]?.ToString());

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/tablet/say/stop", null, ct)).StatusCode);
            await app.StopAsync(ct);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Spike_speech_accepts_once_then_is_busy_until_the_deadline()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var speech = new SpikeTabletSpeech(time);

        var first = await speech.SubmitAsync("角色甲", "第一句");
        Assert.Equal(TabletSpeechOutcome.Accepted, first.Outcome);

        var second = await speech.SubmitAsync("角色甲", "第二句");
        Assert.Equal(TabletSpeechOutcome.Busy, second.Outcome);

        time.Advance(TimeSpan.FromSeconds(3));
        var third = await speech.SubmitAsync("角色甲", "第三句");
        Assert.Equal(TabletSpeechOutcome.Accepted, third.Outcome);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
