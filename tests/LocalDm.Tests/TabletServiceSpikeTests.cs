using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalDm.Tests;

/// <summary>
/// 平板分发的进程内局域网服务探针（合成材料）。
/// 这里自己起一个最小 HTTP 监听，用来先证明「同进程启停 + HTTP 图片传输」这条路通不通，
/// 不引入 ASP.NET Core / Blazor 依赖，也不读 stories/、.appdata/、.local/。
/// </summary>
public sealed class TabletServiceSpikeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-tablet-spike-" + Guid.NewGuid().ToString("N"));

    public TabletServiceSpikeTests() => Directory.CreateDirectory(_root);

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
    public async Task Same_process_service_serves_single_card_image_by_id()
    {
        // 合成卡面：一张 1x1 PNG，字节内容带标记，用来断言「拿到的是这一张」。
        var cardBytes = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };
        File.WriteAllBytes(Path.Combine(_root, "card-1.png"), cardBytes);

        using var service = new SyntheticTabletService(_root);
        var port = service.Start();

        using var client = new HttpClient();
        var image = await client.GetByteArrayAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/cards/card-1/front"),
            TestContext.Current.CancellationToken);

        Assert.Equal(cardBytes, image);

        // 目录只给公开字段，不给图片字节。
        var catalog = await client.GetStringAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/catalog"),
            TestContext.Current.CancellationToken);
        Assert.Contains("card-1", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("PNG", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_card_and_path_traversal_are_rejected()
    {
        File.WriteAllBytes(Path.Combine(_root, "card-1.png"), new byte[] { 1, 2, 3 });

        using var service = new SyntheticTabletService(_root);
        var port = service.Start();

        using var client = new HttpClient();

        var missing = await client.GetAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/cards/nope/front"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var traversal = await client.GetAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/cards/..%2F..%2Fcard-1/front"),
            TestContext.Current.CancellationToken);
        Assert.True(
            traversal.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"路径穿越必须失败，实际 {traversal.StatusCode}");
    }

    [Fact]
    public async Task Stopping_the_service_stops_answering()
    {
        using var service = new SyntheticTabletService(_root);
        var port = service.Start();
        service.Stop();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetStringAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/catalog"),
            TestContext.Current.CancellationToken));
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Six_sessions_each_get_their_own_role_page()
    {
        for (var i = 1; i <= 6; i++)
        {
            File.WriteAllBytes(
                Path.Combine(_root, $"role-{i}.png"),
                [(byte)i, 0xAA, 0xBB]);
        }

        using var service = new SyntheticTabletService(_root);
        var port = service.Start();

        using var client = new HttpClient();
        for (var i = 1; i <= 6; i++)
        {
            var page = await client.GetByteArrayAsync(
                new Uri($"http://127.0.0.1:{port}/tablet/role/pages/{i}"),
                TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { (byte)i, 0xAA, 0xBB }, page);
        }
    }

    /// <summary>
    /// 最小合成服务：同进程 HttpListener，按 ID 取图。
    /// 这是探针，不是产品实现；产品路由在 src/LocalDm.Tablet/。
    /// 与 TabletConcurrencySpikeTests 各自持有独立实例，互不影响。
    /// </summary>
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
                    // 探针：单个请求失败不影响服务继续跑。
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
                // 探针同样要挡住穿越：只允许纯 ID，不允许任何分隔符。
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

            if (segments.Length == 3
                && segments[0] == "tablet"
                && segments[1] == "role"
                && segments[2] == "pages")
            {
                // 页码来自路径最后一段；缺段或非数字都算不存在。
                Write(context, 404, "text/plain", "not found");
                return;
            }

            if (segments.Length == 4
                && segments[0] == "tablet"
                && segments[1] == "role"
                && segments[2] == "pages"
                && int.TryParse(segments[3], out var page))
            {
                var file = Path.Combine(_root, $"role-{page}.png");
                if (page < 1 || page > 6 || !File.Exists(file))
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
