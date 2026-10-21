using System.IO;
using LocalDm.Core.Tablet;
using LocalDm.Tablet;
using Microsoft.AspNetCore.Http;
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
        SyntheticAssets.WritePage(Path.Combine(assetRoot, $"{role.RoleId}-p{page}.png"), $"{role.RoleId}-p{page}");
    }
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton(new TabletSpikeData { Roles = roles, Cards = cards, AssetRoot = assetRoot });
builder.Services.AddSingleton<TabletSpikeState>();
builder.Services.AddHttpContextAccessor();

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

// 释放角色：让别的会话可以认领；同时清掉本会话的阅读状态。
app.MapPost("/tablet/release", (TabletSpikeState state, HttpContext context) =>
{
    var key = TabletSessions.KeyFor(context);
    if (key is not null)
    {
        state.Release(key);
    }

    return Results.Json(new { released = true });
});

// 认领角色：同一角色被别的会话占用时返回 409。网页按钮与测试都走这一个入口，
// 保证「界面能点到」和「接口能被验证」是同一套逻辑。
app.MapPost("/tablet/claim", (string? roleId, TabletSpikeState state, HttpContext context) =>
{
    if (string.IsNullOrEmpty(roleId) || state.Roles.All(r => r.RoleId != roleId))
    {
        return Results.NotFound();
    }

    // 没有会话 Cookie 的请求先发一个：认领必须落到某个具体会话上，
    // 否则「谁认领的」无从谈起。
    var key = TabletSessions.KeyFor(context) ?? TabletSessions.Issue(context);
    return state.TryClaim(key, roleId)
        ? Results.Json(new { claimed = true, roleId })
        : Results.Conflict(new { claimed = false, reason = "TAKEN" });
});

// 图片走 HTTP 响应，不 Base64 进连接消息；按 ID 解析，不按名称拼路径。
// 同时给出「已被认领的角色」清单：真实设备上要能看到别人占了哪个角色。
app.MapGet("/tablet/catalog", (TabletSpikeData data, TabletSpikeState state) => Results.Json(new
{
    roles = data.Roles,
    cards = data.Cards,
    claimedRoles = state.ClaimedRoles,
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

app.MapGet("/tablet/role/pages/{pageNumber:int}", (
    int pageNumber,
    TabletSpikeState state,
    HttpContext context) =>
{
    // 从 Cookie 解析本人角色：客户端不能传别人的角色。
    // 没有会话 Cookie 直接拒绝，不能凭「不带 Cookie」读到任何角色页。
    var key = TabletSessions.KeyFor(context);
    if (key is null)
    {
        return Results.NotFound();
    }

    var session = state.Session(key);
    var role = session.RoleId is null
        ? null
        : state.Roles.FirstOrDefault(r => r.RoleId == session.RoleId);
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
/// <remarks>
/// 必须是浏览器能真正解码的 PNG：只写 9 字节的假签名会让 <c>&lt;img&gt;</c> 显示裂图，
/// 而「响应 200 + Content-Type image/png」照样成立，所以测试必须解码校验。
/// 每张图带编号条带与文字大小的标记，肉眼和程序都能区分不同卡片与页码。
/// </remarks>
internal static class SyntheticAssets
{
    public static void WriteCard(string path, string label) => Write(path, label, 300, 200, bands: 4);

    public static void WritePage(string path, string label) => Write(path, label, 400, 600, bands: 8);

    private static void Write(string path, string label, int width, int height, int bands)
    {
        var seed = StableHash(label);
        var pixels = new byte[width * height * 3];
        var bandHeight = height / bands;
        for (var y = 0; y < height; y++)
        {
            // 每带一个由种子推导的底色：不同卡片颜色不同，同卡不同页也不同。
            var band = Math.Min(bands - 1, y / Math.Max(1, bandHeight));
            var r = (byte)(((seed >> (band * 3)) & 0x3F) * 3 + 40);
            var g = (byte)(((seed >> (band * 3 + 1)) & 0x3F) * 2 + 30);
            var b = (byte)(((seed >> (band * 3 + 2)) & 0x3F) * 4 + 20);
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 3;
                pixels[index] = r;
                pixels[index + 1] = g;
                pixels[index + 2] = b;
                // 左上角一块固定黑条：断言图片内容时用来定位。
                if (x < 12 && y < 12)
                {
                    pixels[index] = 0;
                    pixels[index + 1] = 0;
                    pixels[index + 2] = 0;
                }
            }
        }

        File.WriteAllBytes(path, PngWriter.Encode(pixels, width, height));
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

/// <summary>最小 PNG 编码器：RGB8、无交错、zlib 压缩。合成图片用，不引第三方库。</summary>
internal static class PngWriter
{
    public static byte[] Encode(byte[] rgb, int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var target = y * (1 + width * 3);
            raw[target] = 0; // filter: none
            Array.Copy(rgb, y * width * 3, raw, target + 1, width * 3);
        }

        using var buffer = new MemoryStream();
        buffer.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(buffer, "IHDR", BuildIhdr(width, height));
        WriteChunk(buffer, "IDAT", Compress(raw));
        WriteChunk(buffer, "IEND", []);
        return buffer.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        var header = new byte[13];
        WriteUInt32(header, 0, (uint)width);
        WriteUInt32(header, 4, (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // color type: truecolor
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        return header;
    }

    private static byte[] Compress(byte[] raw)
    {
        // PNG 的 IDAT 装的是 zlib 流（2 字节头 + deflate + 4 字节 Adler-32 校验）。
        // 直接写裸 deflate 的话，Windows 的 WIC 会容错解出来，浏览器却解成全透明/全黑，
        // 于是「HTTP 200 + 自然尺寸正确 + 截图一片白」会同时成立。必须补 zlib 包装。
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        var deflated = output.ToArray();
        var zlib = new byte[2 + deflated.Length + 4];
        zlib[0] = 0x78; // CMF：deflate + 32K 窗口
        zlib[1] = 0x9C; // FLG：默认压缩级，无字典，校验通过
        Array.Copy(deflated, 0, zlib, 2, deflated.Length);
        var adler = Adler32(raw);
        zlib[^4] = (byte)(adler >> 24);
        zlib[^3] = (byte)(adler >> 16);
        zlib[^2] = (byte)(adler >> 8);
        zlib[^1] = (byte)adler;
        return zlib;
    }

    private static uint Adler32(byte[] data)
    {
        const uint modulo = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modulo;
            b = (b + a) % modulo;
        }

        return (b << 16) | a;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteUInt32(length, 0, (uint)data.Length);
        stream.Write(length);
        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }

        Array.Copy(data, 0, typed, 4, data.Length);
        stream.Write(typed);

        var crc = new byte[4];
        WriteUInt32(crc, 0, Crc32(typed));
        stream.Write(crc);
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var n = 0; n < 256; n++)
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
