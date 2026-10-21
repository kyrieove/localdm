using System.IO;
using System.Net;
using System.Net.Http;

namespace LocalDm.Tests;

/// <summary>
/// 探针应用的外壳与目录契约：真的启动 src/LocalDm.Tablet 再发请求。
///
/// 关注点只有三个：Blazor 脚本可达（真实设备连得上的前提）、
/// 目录只给公开名称不夹带图片字节、未认领角色取不到角色页。
/// 多会话与图片内容的完整验证在 <see cref="TabletAppTests"/>。
/// </summary>
public sealed class TabletBlazorSpikeTests
{
    [Fact]
    public async Task Blazor_shell_script_and_public_catalog_are_reachable()
    {
        using var host = TabletBlazorHost.Start(Project("LocalDm.Tablet.csproj"));
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{host.Port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var ct = TestContext.Current.CancellationToken;

        // 1) 首页外壳可达，并引用 blazor.web.js。
        var html = await client.GetStringAsync("/", ct);
        Assert.Contains("blazor.web.js", html, StringComparison.Ordinal);

        // 2) 脚本本身可达：这一项不成立，真实设备连不上 circuit。
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_framework/blazor.web.js", ct)).StatusCode);

        // 3) 目录给出全部名称（含两张同名「书房」），这是玩家点选的入口。
        var catalog = await client.GetStringAsync("/tablet/catalog", ct);
        Assert.Contains("合成角色", catalog, StringComparison.Ordinal);
        Assert.Contains("书房", catalog, StringComparison.Ordinal);
        Assert.Contains("claimedRoles", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("iVBOR", catalog, StringComparison.Ordinal);

        // 4) 未认领角色时取角色页必须失败：客户端不能绕过认领直接读。
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/tablet/role/pages/1", ct)).StatusCode);
    }

    private static string Project(string projectFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LocalDm.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录。");
        return Directory.GetFiles(Path.Combine(root, "src"), projectFileName, SearchOption.AllDirectories).Single();
    }
}
