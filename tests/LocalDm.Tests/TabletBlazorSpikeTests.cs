using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace LocalDm.Tests;

/// <summary>
/// Blazor Interactive Server 探针的自动化部分：真的启动服务、真的发请求。
///
/// 这里验的是「这条路通不通」，不是产品行为：
/// - Razor 组件能渲染出合成角色与卡片目录；
/// - 图片走 HTTP 响应，按 cardId 取到对应字节；
/// - blazor.web.js 与 _framework 资源可达（连接恢复的前提）。
///
/// 全部材料在临时目录现造，不读 stories/、.appdata/、.local/。
/// </summary>
public sealed class TabletBlazorSpikeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-blazor-" + Guid.NewGuid().ToString("N"));

    public TabletBlazorSpikeTests() => Directory.CreateDirectory(_root);

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
    public async Task Blazor_server_renders_synthetic_catalog_and_serves_card_image()
    {
        // 探针服务是 src/LocalDm.Tablet 的合成最小程序，端口用随机空闲口。
        var project = Path.Combine(
            RepoRoot(),
            "src",
            "LocalDm.Tablet",
            "LocalDm.Tablet.csproj");
        Assert.True(File.Exists(project), "找不到 LocalDm.Tablet 工程。");

        var port = FreePort();
        using var host = TabletBlazorHost.Start(project, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };

        var ct = TestContext.Current.CancellationToken;

        // 1) 首页能渲染（Blazor Interactive Server 的静态外壳）。
        //    组件内容由 SignalR circuit 推送，HTTP 首屏只有外壳，所以这里只断言外壳可达。
        var html = await client.GetStringAsync("/", ct);
        Assert.Contains("blazor.web.js", html, StringComparison.Ordinal);

        // 2) Blazor 的脚本与框架资源可达，否则真实设备上根本连不上。
        var script = await client.GetAsync("/_framework/blazor.web.js", ct);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);

        // 3) 目录接口给出全部名称（含两张同名「书房」），这是玩家点选的入口。
        var catalog = await client.GetStringAsync("/tablet/catalog", ct);
        Assert.Contains("合成角色", catalog, StringComparison.Ordinal);
        Assert.Contains("书房", catalog, StringComparison.Ordinal);

        // 3) 图片按 cardId 走 HTTP，且不同卡内容不同（同名也不串图）。
        var first = await client.GetByteArrayAsync("/tablet/cards/card-1/front", ct);
        var second = await client.GetByteArrayAsync("/tablet/cards/card-2/front", ct);
        Assert.NotEqual(first, second);
        Assert.True(first.Length > 0);

        // 4) 目录只给公开字段，不带图片字节。
        var catalogJson = await client.GetStringAsync("/tablet/catalog", ct);
        Assert.Contains("card-1", catalogJson, StringComparison.Ordinal);
        Assert.DoesNotContain("iVBOR", catalogJson, StringComparison.Ordinal);

        // 5) 未知卡与路径穿越必须失败。
        var missing = await client.GetAsync("/tablet/cards/nope/front", ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var traversal = await client.GetAsync("/tablet/cards/..%2F..%2Fcard-1/front", ct);
        Assert.True(
            traversal.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"路径穿越必须失败，实际 {traversal.StatusCode}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LocalDm.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录。");
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
