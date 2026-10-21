using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace LocalDm.Tests;

/// <summary>
/// 多浏览器会话隔离的自动化验证。
///
/// 真实设备测试前必须证明：每台设备（每个 Cookie）各认各的角色，
/// 已认领的角色不能再被别人认领，角色页只返回本人角色。
/// 这条不成立，6 台设备的并发验收就没有意义。
/// </summary>
public sealed class TabletSessionIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-sess-" + Guid.NewGuid().ToString("N"));

    public TabletSessionIsolationTests() => Directory.CreateDirectory(_root);

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
    public async Task Each_browser_cookie_claims_its_own_role_and_pages_stay_isolated()
    {
        for (var i = 1; i <= 6; i++)
        {
            for (var page = 1; page <= 3; page++)
            {
                File.WriteAllBytes(Path.Combine(_root, $"role-{i}-p{page}.png"), [(byte)i, (byte)page, 0xCC]);
            }
        }

        using var service = new SessionTabletService(_root);
        var port = service.Start();

        var ct = TestContext.Current.CancellationToken;
        using var client = new HttpClient();

        // 6 个会话各自认领一个角色。
        var handlers = new HttpClient[6];
        for (var i = 0; i < 6; i++)
        {
            handlers[i] = new HttpClient();
            var response = await handlers[i].PostAsync(
                new Uri($"http://127.0.0.1:{port}/tablet/claim?roleId=role-{i + 1}"), null, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 各自取自己的 3 页：内容必须对应本人角色。
        for (var i = 0; i < 6; i++)
        {
            for (var page = 1; page <= 3; page++)
            {
                var bytes = await handlers[i].GetByteArrayAsync(
                    new Uri($"http://127.0.0.1:{port}/tablet/role/pages/{page}"), ct);
                Assert.Equal(new byte[] { (byte)(i + 1), (byte)page, 0xCC }, bytes);
            }
        }

        // 第 7 个会话不能再认领已被占用的 role-1：返回 409。
        using var late = new HttpClient();
        var conflict = await late.PostAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/claim?roleId=role-1"), null, ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // 没有 Cookie 的会话取角色页必须失败。
        using var anonymous = new HttpClient();
        var denied = await anonymous.GetAsync(
            new Uri($"http://127.0.0.1:{port}/tablet/role/pages/1"), ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        foreach (var handler in handlers)
        {
            handler.Dispose();
        }
    }

    /// <summary>带 Cookie 会话的最小服务：探针用，产品路由在 src/LocalDm.Tablet/。</summary>
    private sealed class SessionTabletService : IDisposable
    {
        private readonly string _root;
        private readonly Dictionary<string, string> _sessionRoles = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;

        public SessionTabletService(string root) => _root = root;

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
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var path = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/");
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (path == "/tablet/claim")
            {
                var roleId = context.Request.QueryString["roleId"] ?? string.Empty;
                var cookie = EnsureCookie(context);
                lock (_gate)
                {
                    var taken = _sessionRoles.Any(pair => string.Equals(pair.Value, roleId, StringComparison.Ordinal));
                    if (taken && !string.Equals(_sessionRoles.GetValueOrDefault(cookie), roleId, StringComparison.Ordinal))
                    {
                        Write(context, 409, "text/plain", "conflict");
                        return;
                    }

                    _sessionRoles[cookie] = roleId;
                }

                Write(context, 200, "application/json", "{\"claimed\":true}");
                return;
            }

            if (segments.Length == 4
                && segments[0] == "tablet"
                && segments[1] == "role"
                && segments[2] == "pages"
                && int.TryParse(segments[3], out var page))
            {
                string? roleId;
                lock (_gate)
                {
                    _sessionRoles.TryGetValue(EnsureCookie(context), out roleId);
                }

                if (roleId is null || page < 1 || page > 3)
                {
                    Write(context, 404, "text/plain", "not found");
                    return;
                }

                var file = Path.Combine(_root, $"{roleId}-p{page}.png");
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

        private static string EnsureCookie(HttpListenerContext context)
        {
            // HttpListener 的 Cookie 集合：直接按名取值。
            var existing = context.Request.Cookies["localdm-tablet-spike"];
            if (!string.IsNullOrEmpty(existing?.Value))
            {
                return existing.Value;
            }

            var fresh = Guid.NewGuid().ToString("N");
            context.Response.AppendHeader(
                "Set-Cookie",
                $"localdm-tablet-spike={fresh}; Path=/; HttpOnly; SameSite=Lax");
            return fresh;
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
