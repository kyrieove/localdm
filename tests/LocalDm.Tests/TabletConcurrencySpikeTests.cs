using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace LocalDm.Tests;

/// <summary>
/// 6 会话并发、刷新与断线恢复的自动化部分。
///
/// 这些用例验的是「服务端能同时支撑多个浏览器会话且互不串数据」：
/// - 6 个并发客户端各自取图，内容按请求的 ID 对应，不互相污染；
/// - 反复请求（相当于刷新后重建页面）结果稳定；
/// - 服务停止后所有请求立即失败，不残留旧局资产。
///
/// 真实设备上的双指缩放、横竖屏、锁屏与切后台恢复必须由平板实测，
/// 桌面自动化不能替代，见 docs/evidence/tablet/browser-spike.md。
/// </summary>
public sealed class TabletConcurrencySpikeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-conc-" + Guid.NewGuid().ToString("N"));

    public TabletConcurrencySpikeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Six_concurrent_sessions_each_get_their_own_images()
    {
        for (var i = 1; i <= 6; i++)
        {
            File.WriteAllBytes(Path.Combine(_root, $"card-{i}.png"), [(byte)i, 0xAA, 0xBB]);
        }

        using var service = new SyntheticTabletService(_root);
        var port = service.Start();
        using var client = new HttpClient();

        var ct = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(1, 6)
            .Select(i => client.GetByteArrayAsync(
                new Uri($"http://127.0.0.1:{port}/tablet/cards/card-{i}/front"), ct))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(new byte[] { (byte)(i + 1), 0xAA, 0xBB }, results[i]);
        }
    }

    [Fact]
    public async Task Repeated_requests_keep_returning_the_same_image()
    {
        File.WriteAllBytes(Path.Combine(_root, "card-1.png"), [1, 2, 3]);

        using var service = new SyntheticTabletService(_root);
        var port = service.Start();
        using var client = new HttpClient();

        var ct = TestContext.Current.CancellationToken;
        byte[]? previous = null;
        for (var round = 0; round < 5; round++)
        {
            var current = await client.GetByteArrayAsync(
                new Uri($"http://127.0.0.1:{port}/tablet/cards/card-1/front"), ct);
            if (previous is not null)
            {
                Assert.Equal(previous, current);
            }

            previous = current;
        }
    }

    [Fact]
    public async Task After_stopping_the_service_every_request_fails()
    {
        File.WriteAllBytes(Path.Combine(_root, "card-1.png"), [1, 2, 3]);

        using var service = new SyntheticTabletService(_root);
        var port = service.Start();
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/cards/card-1/front"),
            TestContext.Current.CancellationToken)).StatusCode);

        service.Stop();

        using var stopped = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => stopped.GetStringAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/catalog"),
            TestContext.Current.CancellationToken));
    }

    /// <summary>同进程最小服务：探针用，产品路由在 src/LocalDm.Tablet/。</summary>
    private sealed class SyntheticTabletService : IDisposable
    {
        private readonly string _root;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;

        public SyntheticTabletService(string root) => _root = root;

        public int Start()
        {
            var port = FreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = LoopAsync(_listener, _cts.Token);
            return port;
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch (HttpListenerException)
            {
            }
        }

        private async Task LoopAsync(HttpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                try
                {
                    Handle(context);
                }
                catch
                {
                    // 单个请求失败不影响其他会话。
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var path = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/");
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (path == "/tablet/catalog")
            {
                var names = Directory.GetFiles(_root, "*.png")
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(n => n, StringComparer.Ordinal);
                Write(context, 200, "application/json",
                    "{\"cards\":[" + string.Join(",", names.Select(n => $"\"{n}\"")) + "]}");
                return;
            }

            if (segments.Length == 4
                && segments[0] == "tablet"
                && segments[1] == "cards"
                && segments[3] == "front")
            {
                var id = segments[2];
                if (id.Contains('/') || id.Contains('\\') || id.Contains(".."))
                {
                    Write(context, 404, "text/plain", "not found");
                    return;
                }

                var file = Path.Combine(_root, id + ".png");
                if (!File.Exists(file))
                {
                    Write(context, 404, "text/plain", "not found");
                    return;
                }

                var bytes = File.ReadAllBytes(file);
                context.Response.ContentType = "image/png";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
                return;
            }

            Write(context, 404, "text/plain", "not found");
        }

        private static void Write(HttpListenerContext context, int status, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose() => Stop();
    }
}
