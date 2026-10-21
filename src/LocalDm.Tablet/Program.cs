using System.IO;
using LocalDm.Core.Tablet;
using LocalDm.Tablet;
using Microsoft.Extensions.FileProviders;

// 命令行参数优先：测试与真实探针用 --urls 指定端口，没有参数时才退回固定口。
var builder = WebApplication.CreateBuilder(args);
if (args.Contains("--urls") is false && builder.Configuration["urls"] is not { Length: > 0 })
{
    builder.WebHost.UseUrls("http://127.0.0.1:5099");
}

// 合成材料：6 个角色、6 张卡（含两张同名卡，用来验证同名不串图）。
var assetRoot = Path.Combine(Path.GetTempPath(), "localdm-tablet-blazor-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(assetRoot);
var roles = Enumerable.Range(1, 6)
    .Select(i => new TabletSpikeRole($"role-{i}", $"合成角色 {i}", 3))
    .ToArray();
var cards = new[]
{
    new TabletSpikeCard("card-1", "书房", "地点", 1),
    new TabletSpikeCard("card-2", "书房", "地点", 2),
    new TabletSpikeCard("card-3", "走廊", "地点", 3),
    new TabletSpikeCard("card-4", "信箱", "地点", 4),
    new TabletSpikeCard("card-5", "旧照片", "回忆", 5),
    new TabletSpikeCard("card-6", "钥匙", "回忆", 6),
};
foreach (var card in cards)
{
    // 同名卡必须内容不同：用 cardId 而不是显示名做内容种子。
    SyntheticAssets.WriteCard(Path.Combine(assetRoot, card.CardId + ".png"), card.CardId);
}

foreach (var role in roles)
{
    for (var page = 1; page <= role.PageCount; page++)
    {
        SyntheticAssets.WritePage(
            Path.Combine(assetRoot, $"{role.RoleId}-p{page}.png"),
            $"{role.RoleId}-{page}",
            page);
    }
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton(new TabletSpikeData { Roles = roles, Cards = cards, AssetRoot = assetRoot });
builder.Services.AddSingleton<TabletSpikeState>();

var app = builder.Build();
app.UseAntiforgery();

// Debug 下 wwwroot 留在源目录；内容根指到输出目录时把它作为静态文件源。
// blazor.web.js 在 Debug 输出里不存在（只在 publish 产物里），
// 所以测试与真实探针都用 publish 产物运行，见 TabletBlazorHost。
var sharedFrameworkRoot = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "src", "LocalDm.Tablet", "wwwroot"));
if (Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "wwwroot")))
{
    app.UseStaticFiles();
}
else if (Directory.Exists(sharedFrameworkRoot))
{
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(sharedFrameworkRoot) });
}

// 图片走 HTTP 响应，不 Base64 进连接消息；按 ID 解析，不按名称拼路径。
app.MapGet("/tablet/catalog", (TabletSpikeData data) => Results.Json(new
{
    roles = data.Roles,
    cards = data.Cards,
}));

app.MapGet("/tablet/cards/{cardId}/front", (string cardId, TabletSpikeData data) =>
{
    if (cardId.Contains('/') || cardId.Contains('\\') || cardId.Contains(".."))
    {
        return Results.NotFound();
    }

    if (!data.Cards.Any(c => c.CardId == cardId))
    {
        return Results.NotFound();
    }

    var path = Path.Combine(data.AssetRoot, cardId + ".png");
    return File.Exists(path) ? Results.File(path, "image/png") : Results.NotFound();
});

app.MapGet("/tablet/role/pages/{pageNumber:int}", (int pageNumber, TabletSpikeState state) =>
{
    var role = state.Role;
    if (role is null || pageNumber < 1 || pageNumber > role.PageCount)
    {
        return Results.NotFound();
    }

    var path = Path.Combine(state.Data.AssetRoot, $"{role.RoleId}-p{pageNumber}.png");
    return File.Exists(path) ? Results.File(path, "image/png") : Results.NotFound();
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>合成图片写入：探针自己造内容，不读任何真实材料。</summary>
internal static class SyntheticAssets
{
    public static void WriteCard(string path, string label) => Write(path, label, 300, 200);

    public static void WritePage(string path, string label, int page) => Write(path, $"{label}-{page}", 400, 600);

    private static void Write(string path, string label, int width, int height)
    {
        // 一张内容可区分的最小 PNG：按标签哈希取色，保证不同卡片内容不同。
        var seed = StableHash(label);
        var r = (byte)(seed & 0xFF);
        var g = (byte)((seed >> 8) & 0xFF);
        var b = (byte)((seed >> 16) & 0xFF);
        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G', r, g, b, (byte)width, (byte)height]);
    }

    /// <summary>跨进程稳定的哈希：string.GetHashCode 每次进程都不同，会让两张卡恰好同色。</summary>
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in text)
            {
                hash = (hash * 31) + c;
            }

            return hash;
        }
    }
}
