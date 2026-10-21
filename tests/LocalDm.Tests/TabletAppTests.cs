using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace LocalDm.Tests;

/// <summary>
/// 打真实探针应用（src/LocalDm.Tablet）的功能测试。
///
/// 这里全部走真实路由与会话实现，不是测试内另写的假服务：
/// - 用独立 Cookie 容器模拟多台设备，各自认领角色、访问相同页号；
/// - 断言返回的是真实可解码 PNG，且内容与尺寸对应本人角色与页码；
/// - 断言会话失效（无 Cookie）时取不到角色页。
/// </summary>
public sealed class TabletAppTests : IDisposable
{
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Independent_sessions_claim_different_roles_and_get_their_own_pages()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var ct = TestContext.Current.CancellationToken;

        var clients = new HttpClient[6];
        for (var i = 0; i < 6; i++)
        {
            clients[i] = NewClient(baseAddress);
            var claim = await clients[i].PostAsync($"/tablet/claim?roleId=role-{i + 1}", null, ct);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }

        // 每个会话取同一个页号：内容必须各不相同，且都是可解码 PNG。
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < 6; i++)
        {
            for (var page = 1; page <= 3; page++)
            {
                var bytes = await clients[i].GetByteArrayAsync($"/tablet/role/pages/{page}", ct);
                var image = SyntheticPng.Decode(bytes);
                Assert.Equal(400, image.Width);
                Assert.Equal(600, image.Height);

                var key = Convert.ToHexString(bytes);
                seen[key] = seen.GetValueOrDefault(key) + 1;
            }
        }

        // 18 张图应当 18 个不同内容：任何人拿到别人的角色页都算串台。
        Assert.Equal(18, seen.Count);
    }

    [Fact]
    public async Task Claiming_a_taken_role_returns_conflict_and_keeps_the_first_owner()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var ct = TestContext.Current.CancellationToken;

        var first = NewClient(baseAddress);
        Assert.Equal(
            HttpStatusCode.OK,
            (await first.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);

        var second = NewClient(baseAddress);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await second.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);

        // 先到者仍持有该角色，页面照常可取。
        var page = await first.GetByteArrayAsync("/tablet/role/pages/1", ct);
        Assert.Equal(400, SyntheticPng.Decode(page).Width);

        // 没认领到角色的会话什么都取不到。
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await second.GetAsync("/tablet/role/pages/1", ct)).StatusCode);

        // 完全不带 Cookie 的请求也拿不到：不能凭「没有会话」绕过认领。
        using var anonymous = new HttpClient();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync(
                new Uri($"http://127.0.0.1:{PortOf(host)}/tablet/role/pages/1"), ct)).StatusCode);

        // 不带 Cookie 的认领会现场发一个 Cookie，之后仍按这个新会话隔离。
        var fresh = NewClient(baseAddress);
        Assert.Equal(
            HttpStatusCode.OK,
            (await fresh.PostAsync("/tablet/claim?roleId=role-6", null, ct)).StatusCode);
        var freshPage = await fresh.GetByteArrayAsync("/tablet/role/pages/1", ct);
        Assert.Equal(400, SyntheticPng.Decode(freshPage).Width);
    }

    [Fact]
    public async Task Card_images_are_real_pngs_and_same_name_cards_differ()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var ct = TestContext.Current.CancellationToken;
        var client = NewClient(baseAddress);

        var first = await client.GetByteArrayAsync("/tablet/cards/card-1/front", ct);
        var second = await client.GetByteArrayAsync("/tablet/cards/card-2/front", ct);

        // 两张都叫「书房」：必须都能解码，且内容不同。
        Assert.Equal(300, SyntheticPng.Decode(first).Width);
        Assert.Equal(200, SyntheticPng.Decode(first).Height);
        Assert.Equal(300, SyntheticPng.Decode(second).Width);
        Assert.NotEqual(first, second);

        // 未知卡与路径穿越必须失败。
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/tablet/cards/nope/front", ct)).StatusCode);
        Assert.True(
            (await client.GetAsync("/tablet/cards/..%2F..%2Fcard-1/front", ct)).StatusCode
                is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Interactive_page_and_blazor_script_are_reachable()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var ct = TestContext.Current.CancellationToken;
        var client = NewClient(baseAddress);

        var html = await client.GetStringAsync("/", ct);
        Assert.Contains("blazor.web.js", html, StringComparison.Ordinal);
        Assert.Contains("blazor.web.js", html, StringComparison.Ordinal);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/_framework/blazor.web.js", ct)).StatusCode);
    }

    [Fact]
    public async Task Stopping_the_host_stops_answering()
    {
        var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var client = NewClient(baseAddress);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/tablet/catalog", TestContext.Current.CancellationToken)).StatusCode);

        host.Dispose();

        using var stopped = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => stopped.GetStringAsync(
            new Uri(baseAddress, "/tablet/catalog"), TestContext.Current.CancellationToken));
    }

    private HttpClient NewClient(Uri baseAddress)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = true })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _clients.Add(client);
        return client;
    }

    private static int PortOf(TabletBlazorHost host) => host.Port;

    private static string RepoRoot(string projectFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LocalDm.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录。");
        var project = Directory.GetFiles(Path.Combine(root, "src"), projectFileName, SearchOption.AllDirectories)
            .Single();
        return project;
    }
}
