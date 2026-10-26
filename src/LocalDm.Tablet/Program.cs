using System.IO;
using LocalDm.Core.Tablet;
using LocalDm.Tablet;

// 真实剧本包模式：给了包路径就服务那一本，没给就走合成探针。
// 给了路径却找不到文件必须报错退出：悄悄退回合成模式会让页面显示假卡，
// 看起来「能打开」，其实服务的是另一本剧本。
var packagePath = Environment.GetEnvironmentVariable("LOCALDM_TABLET_PACKAGE");
ITabletContent content;
if (!string.IsNullOrWhiteSpace(packagePath))
{
    if (!File.Exists(packagePath))
    {
        throw new FileNotFoundException($"找不到剧本包：{packagePath}", packagePath);
    }

    var package = System.Text.Json.JsonSerializer.Deserialize<LocalDm.Core.Domain.ScriptPackage>(
        File.ReadAllText(packagePath))
        ?? throw new InvalidOperationException("剧本包读不出来。");
    // 软件导入的包把清单放在 assets/<包标识>.tablet.json（规则同 Infrastructure 的 TabletMaterials；
    // 那边是 Windows 目标框架，这里引用不了，只读不生成）。
    var sidecar = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packagePath))!, "..", "assets", package.PackageId + ".tablet.json");
    var manifest = package.Tablet
        ?? (File.Exists(sidecar) ? System.Text.Json.JsonSerializer.Deserialize<TabletManifest>(File.ReadAllText(sidecar)) : null)
        ?? throw new InvalidOperationException("这个剧本包没有平板清单：先在软件里打开一次这本剧本，让它准备好。");
    if (manifest.ManifestVersion != TabletManifest.CurrentManifestVersion)
    {
        throw new InvalidOperationException("平板清单是旧版本：先在软件里打开一次这本剧本，让它重新准备。");
    }

    content = new PackageTabletContent(package with { Tablet = manifest }, TabletAssetRoot.Resolve(packagePath, manifest));
}
else
{
    content = new SpikeTabletContent(BuildSpikeData());
}

// 独立探针无桌面局对话：包模式用空来源，合成模式使用合成对话。
IPublicFeedSource source = content is SpikeTabletContent ? new SpikePublicFeedSource() : new EmptyPublicFeedSource();
var app = TabletWebHost.Create(
    content,
    source,
    args,
    Directory.GetCurrentDirectory(),
    content is SpikeTabletContent ? new SpikeTabletSpeech() : null,
    ui: content is SpikeTabletContent ? new SpikeTabletUiSource(source) : (ITabletUiSource?)null);
app.Run();
// 合成材料：6 个角色、6 张卡（含两张同名卡，用来验证同名不串图）。
static TabletSpikeData BuildSpikeData()
{
    var assetRoot = Path.Combine(Path.GetTempPath(), "localdm-tablet-blazor-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(assetRoot);
    var roles = Enumerable.Range(1, 6)
        .Select(i => new TabletRole($"role-{i}", $"合成角色 {i}"))
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

    // 角色本主阅读方式是文字排版；这里仍给每角色生成一页原稿图，
    // 用来验证「查看原稿」入口与角色页隔离。真实产品里原稿页来自导入资产。
    foreach (var role in roles)
    {
        for (var page = 1; page <= 3; page++)
        {
            SyntheticAssets.WritePage(Path.Combine(assetRoot, $"{role.RoleId}-source-{page}.png"), $"{role.RoleId}-source-{page}");
        }
    }

    const int handbookPages = 2;
    for (var page = 1; page <= handbookPages; page++)
    {
        SyntheticAssets.WritePage(Path.Combine(assetRoot, $"handbook-{page}.png"), $"handbook-{page}");
    }

    var book = SyntheticRoleBook.Create();
    var roleBooks = roles.ToDictionary(r => r.RoleId, r => book with
    {
        RoleId = r.RoleId,
        Chapters = book.Chapters.Select((chapter, index) => chapter with
        {
            Blocks = chapter.Blocks.Select(block => block with { SourcePageNumber = index + 1 }).ToArray(),
        }).ToArray(),
        SourcePageAssetIds = Enumerable.Range(1, 3).Select(page => $"{r.RoleId}-source-{page}").ToArray(),
        Confidence = book.Confidence with { HasSourcePages = true },
    }, StringComparer.Ordinal);

    return new TabletSpikeData
    {
        Roles = roles,
        Cards = cards,
        AssetRoot = assetRoot,
        StoryTitle = SyntheticRoleBook.Title,
        RoleBooks = roleBooks,
        HandbookPageCount = handbookPages,
    };
}

/// <summary>阅读进度请求体。偏移量 0 是有效值（第一页开头）。</summary>
public sealed record ReadingProgressRequest(
    long CharacterOffset,
    int FontSize,
    string LineSpacing,
    bool FocusMode)
{
    public bool? PreferText { get; init; }
    public int? SourcePageNumber { get; init; }
    public int? Columns { get; init; }
}

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
