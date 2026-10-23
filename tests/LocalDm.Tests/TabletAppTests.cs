using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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

    [Fact]
    public async Task Css_and_javascript_assets_are_revalidated_after_changes()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{host.Port}"),
        };

        foreach (var path in new[] { "/reader.css", "/reader-pagination.js" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Independent_sessions_claim_different_roles_and_get_their_own_source_pages()
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

        // 每个会话取自己的原稿页：内容必须各不相同（串台就会相同）。
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < 6; i++)
        {
            var bytes = await clients[i].GetByteArrayAsync("/tablet/role/source", ct);
            var image = SyntheticPng.Decode(bytes);
            Assert.Equal(400, image.Width);
            Assert.Equal(600, image.Height);

            var key = Convert.ToHexString(bytes);
            seen[key] = seen.GetValueOrDefault(key) + 1;
        }

        // 6 张图应当 6 个不同内容：任何人拿到别人的原稿页都算串台。
        Assert.Equal(6, seen.Count);

        // 6 个角色全被占用，第 7 个会话再认任何一个都应冲突。
        using var late = new HttpClient();
        var conflict = await late.PostAsync(
            new Uri($"http://127.0.0.1:{PortOf(host)}/tablet/claim?roleId=role-1"), null, ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // 没有 Cookie 的会话取原稿页必须失败。
        using var anonymous = new HttpClient();
        var denied = await anonymous.GetAsync(
            new Uri($"http://127.0.0.1:{PortOf(host)}/tablet/role/source"), ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        // 有人退出后，空出的角色可以再被认领。
        var released = await clients[5].PostAsync("/tablet/release", null, ct);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await late.PostAsync(
                new Uri($"http://127.0.0.1:{PortOf(host)}/tablet/claim?roleId=role-6"), null, ct)).StatusCode);
        var lateSource = await late.GetByteArrayAsync(
            new Uri($"http://127.0.0.1:{PortOf(host)}/tablet/role/source"), ct);
        Assert.Equal(600, SyntheticPng.Decode(lateSource).Height);
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

        // 先到者仍持有该角色，原稿页照常可取。
        var page = await first.GetByteArrayAsync("/tablet/role/source", ct);
        Assert.Equal(400, SyntheticPng.Decode(page).Width);

        // 没认领到角色的会话什么都取不到。
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await second.GetAsync("/tablet/role/source", ct)).StatusCode);

        // 完全不带 Cookie 的请求也拿不到：不能凭「没有会话」绕过认领。
        using var anonymous = new HttpClient();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync(
                new Uri($"http://127.0.0.1:{PortOf(host)}/tablet/role/source"), ct)).StatusCode);

        // 先到者退出后角色空出，另一个会话可以认领并读到原稿页。
        Assert.Equal(
            HttpStatusCode.OK,
            (await first.PostAsync("/tablet/release", null, ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await second.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);
        var secondSource = await second.GetByteArrayAsync("/tablet/role/source", ct);
        Assert.Equal(600, SyntheticPng.Decode(secondSource).Height);
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

    [Fact]
    public async Task Role_book_text_comes_only_for_the_claimed_role()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var ct = TestContext.Current.CancellationToken;

        var first = NewClient(baseAddress);
        var second = NewClient(baseAddress);
        Assert.Equal(
            HttpStatusCode.OK,
            (await first.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await second.PostAsync("/tablet/claim?roleId=role-2", null, ct)).StatusCode);

        // 两边都拿到自己的角色本，且章节结构一致（合成样本相同，但 roleId 不同）。
        var bookA = await first.GetStringAsync("/tablet/role/book", ct);
        var bookB = await second.GetStringAsync("/tablet/role/book", ct);
        Assert.Contains("\"roleId\":\"role-1\"", bookA, StringComparison.Ordinal);
        Assert.Contains("\"roleId\":\"role-2\"", bookB, StringComparison.Ordinal);

        // 章题与正文块都要给：阅读器靠它们分页。
        foreach (var book in new[] { bookA, bookB })
        {
            Assert.Contains("ChapterTitle", book, StringComparison.Ordinal);
            Assert.Contains("Paragraph", book, StringComparison.Ordinal);
            Assert.Contains("雨停之前", book, StringComparison.Ordinal);
        }

        // 没有认领的会话什么都拿不到。
        using var anonymous = new HttpClient();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync(new Uri(baseAddress, "/tablet/role/book"), ct)).StatusCode);
    }

    [Fact]
    public async Task Reading_progress_round_trips_and_position_zero_is_valid()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var ct = TestContext.Current.CancellationToken;
        var client = NewClient(baseAddress);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);

        // 起始进度是位置 0：第一页开头，不是「没有进度」。
        var initial = await client.GetStringAsync("/tablet/role/progress", ct);
        Assert.Contains("\"characterOffset\":0", initial, StringComparison.Ordinal);
        Assert.Contains("\"preferText\":false", initial, StringComparison.Ordinal);
        Assert.Contains("\"sourcePageNumber\":1", initial, StringComparison.Ordinal);
        Assert.Contains("\"columns\":2", initial, StringComparison.Ordinal);

        // 读了一段之后保存：偏移量与设置都要原样回来。
        var saved = await client.PostAsync(
            "/tablet/role/progress",
            JsonContent.Create(new { characterOffset = 480L, fontSize = 22, lineSpacing = "2.2", focusMode = true, preferText = true, sourcePageNumber = 2, columns = 1 }),
            ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var reloaded = await client.GetStringAsync("/tablet/role/progress", ct);
        Assert.Contains("\"preferText\":true", reloaded, StringComparison.Ordinal);
        Assert.Contains("\"sourcePageNumber\":2", reloaded, StringComparison.Ordinal);
        Assert.Contains("\"columns\":1", reloaded, StringComparison.Ordinal);
        Assert.Contains("\"characterOffset\":480", reloaded, StringComparison.Ordinal);
        Assert.Contains("\"fontSize\":22", reloaded, StringComparison.Ordinal);
        Assert.Contains("\"lineSpacing\":\"2.2\"", reloaded, StringComparison.Ordinal);
        Assert.Contains("\"focusMode\":true", reloaded, StringComparison.Ordinal);

        // 越界设置被钳制：不信任客户端传来的任意值。
        await client.PostAsync(
            "/tablet/role/progress",
            JsonContent.Create(new { characterOffset = 10L, fontSize = 99, lineSpacing = "9", focusMode = false }),
            ct);
        var clamped = await client.GetStringAsync("/tablet/role/progress", ct);
        Assert.Contains("\"fontSize\":26", clamped, StringComparison.Ordinal);
        Assert.Contains("\"lineSpacing\":\"1.95\"", clamped, StringComparison.Ordinal);

        // 会话之间互不影响：另一个会话仍是位置 0。
        var other = NewClient(baseAddress);
        Assert.Equal(
            HttpStatusCode.OK,
            (await other.PostAsync("/tablet/claim?roleId=role-2", null, ct)).StatusCode);
        var otherProgress = await other.GetStringAsync("/tablet/role/progress", ct);
        Assert.Contains("\"characterOffset\":0", otherProgress, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_page_is_available_only_for_the_claimed_role()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var ct = TestContext.Current.CancellationToken;
        var client = NewClient(baseAddress);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);

        var source = await client.GetByteArrayAsync("/tablet/role/source", ct);
        var image = SyntheticPng.Decode(source);
        Assert.Equal(400, image.Width);
        Assert.Equal(600, image.Height);

        using var anonymous = new HttpClient();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync(new Uri(baseAddress, "/tablet/role/source"), ct)).StatusCode);
    }

    [Fact]
    public async Task Handbook_pages_are_available_only_to_sessions_with_a_claimed_role()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var baseAddress = new Uri($"http://127.0.0.1:{PortOf(host)}");
        var ct = TestContext.Current.CancellationToken;

        using var anonymous = new HttpClient();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync(new Uri(baseAddress, "/tablet/handbook/source/1"), ct)).StatusCode);

        var client = NewClient(baseAddress);
        await client.GetAsync("/", ct); // 拿到会话 Cookie，但还没认领角色
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/tablet/handbook/source/1", ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);
        var first = await client.GetByteArrayAsync("/tablet/handbook/source/1", ct);
        var second = await client.GetByteArrayAsync("/tablet/handbook/source/2", ct);
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/tablet/handbook/source/3", ct)).StatusCode);
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

    [Fact]
    public async Task Changing_roles_clears_reading_position_and_source_pages_remain_scoped()
    {
        using var host = TabletBlazorHost.Start(RepoRoot("LocalDm.Tablet.csproj"));
        var client = NewClient(new Uri($"http://127.0.0.1:{host.Port}"));
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/tablet/claim?roleId=role-1", null, ct)).StatusCode);
        await client.PostAsync("/tablet/role/progress", JsonContent.Create(new
        {
            characterOffset = 480, fontSize = 22, lineSpacing = "2.2", focusMode = false,
        }), ct);
        var firstSource = await client.GetByteArrayAsync("/tablet/role/source/1", ct);
        using var sourceResponse = await client.GetAsync("/tablet/role/source/1", ct);
        Assert.True(sourceResponse.Headers.CacheControl?.NoStore);
        var secondSource = await client.GetByteArrayAsync("/tablet/role/source/2", ct);
        Assert.NotEqual(firstSource, secondSource);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/tablet/role/source/4", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/tablet/claim?roleId=role-2", null, ct)).StatusCode);
        var progress = await client.GetStringAsync("/tablet/role/progress", ct);
        Assert.Contains("\"roleId\":\"role-2\"", progress, StringComparison.Ordinal);
        Assert.Contains("\"characterOffset\":0", progress, StringComparison.Ordinal);
        Assert.NotEqual(firstSource, await client.GetByteArrayAsync("/tablet/role/source/1", ct));
        await client.PostAsync("/tablet/release", null, ct);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/tablet/role/book", ct)).StatusCode);
        using var anonymous = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync(new Uri(client.BaseAddress!, "/tablet/role/source/2"), ct)).StatusCode);
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
