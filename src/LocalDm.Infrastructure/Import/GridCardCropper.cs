using System.IO;
using System.Threading;
using System.Security.Cryptography;
using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 通用网格裁切：按行数、列数、有效区域与有效槽位，把一整版图切成若干张单卡。
///
/// 支持 JPEG/PNG/BMP/TIFF：Windows 自带的 WIC 解码，不引第三方图片库。
/// 每条边都按整图位置独立取整（见 <see cref="GridGeometry"/>）；
/// 有效区域超出原图时直接报错，不猜。
/// </summary>
public static class GridCardCropper
{
    /// <summary>
    /// 把整版图切成每格一张。返回顺序与 <see cref="GridLayout.EffectiveSlots"/> 一致。
    /// </summary>
    public static IReadOnlyList<CroppedCard> Crop(string imagePath, GridLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(layout);

        var sheet = RasterImage.Load(imagePath);
        var crops = new List<CroppedCard>(layout.EffectiveSlots().Count);
        foreach (var slot in layout.EffectiveSlots())
        {
            var rect = GridGeometry.SlotRect(layout, slot);
            if (rect is not { } region || !GridGeometry.Fits(region, sheet.Width, sheet.Height))
            {
                throw new DomainRuleException(
                    DomainErrorCode.INVALID_INPUT,
                    $"第 {slot.Row + 1} 行第 {slot.Column + 1} 列的格子超出了原图（{sheet.Width}×{sheet.Height}），请检查切片参数。");
            }

            var pixels = layout.MirrorHorizontally
                ? MirrorHorizontally(sheet.Crop(region))
                : sheet.Crop(region);

            crops.Add(new CroppedCard
            {
                Slot = slot,
                Region = region,
                Image = pixels,
                Png = PngCodec.Encode(pixels),
            });
        }

        return crops;
    }

    private static RasterImage MirrorHorizontally(RasterImage image)
    {
        var mirrored = new RasterImage(image.Width, image.Height);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                mirrored.Set(x, y, image.Get(image.Width - 1 - x, y));
            }
        }

        return mirrored;
    }
}

/// <summary>切出来的一张卡：槽位、在整图上的位置、像素与可直接落盘的 PNG。</summary>
public sealed record CroppedCard
{
    public required GridSlot Slot { get; init; }

    public required PixelRegion Region { get; init; }

    public required RasterImage Image { get; init; }

    /// <summary>PNG 字节：与 <see cref="Image"/> 是同一份像素，测试解码它来核对。</summary>
    public required byte[] Png { get; init; }

    public int Width => Image.Width;

    public int Height => Image.Height;

    public int X => Region.X;

    public int Y => Region.Y;
}

/// <summary>
/// 解码后或待写出的 RGB8 位图（行优先，每像素 3 字节）。
/// 只在导入层与测试里用，不进脚本包。
/// </summary>
public sealed class RasterImage
{
    public RasterImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "图片尺寸必须大于 0。");
        }

        Width = width;
        Height = height;
        Pixels = new byte[width * height * 3];
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public (byte R, byte G, byte B) Get(int x, int y)
    {
        var index = (y * Width + x) * 3;
        return (Pixels[index], Pixels[index + 1], Pixels[index + 2]);
    }

    public void Set(int x, int y, (byte R, byte G, byte B) color)
    {
        var index = (y * Width + x) * 3;
        Pixels[index] = color.R;
        Pixels[index + 1] = color.G;
        Pixels[index + 2] = color.B;
    }

    public RasterImage Crop(PixelRegion region)
    {
        var target = new RasterImage(region.Width, region.Height);
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                target.Set(x, y, Get(region.X + x, region.Y + y));
            }
        }

        return target;
    }

    /// <summary>用 WIC 解码图片文件。不依赖任何第三方图片库。</summary>
    public static RasterImage Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new DomainRuleException(DomainErrorCode.MISSING_SOURCE, "找不到要切的图片文件。");
        }

        using var stream = File.OpenRead(path);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            frame,
            System.Windows.Media.PixelFormats.Rgb24,
            null,
            0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 3;
        var buffer = new byte[stride * height];
        converted.CopyPixels(buffer, stride, 0);

        var image = new RasterImage(width, height);
        Buffer.BlockCopy(buffer, 0, image.Pixels, 0, buffer.Length);
        return image;
    }
}

/// <summary>最小 PNG 编码器：RGB8、无交错、zlib 压缩。合成与切图产物都用它。</summary>
public static class PngCodec
{
    public static byte[] Encode(RasterImage image) => Encode(image.Pixels, image.Width, image.Height);

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
        WriteChunk(buffer, "IDAT", ZlibCompress(raw));
        WriteChunk(buffer, "IEND", []);
        return buffer.ToArray();
    }

    /// <summary>
    /// 内容 hash：SHA-256 小写十六进制，与 <see cref="SourceFile.ContentHash"/> 同一算法。
    /// 算的是字节流，不是解码后的像素，换一次编码也会换 hash。
    /// </summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

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

    /// <summary>
    /// PNG 的 IDAT 装的是 zlib 流（2 字节头 + deflate + 4 字节 Adler-32）。
    /// 只写裸 deflate 时 WIC 容错、浏览器解成全黑，所以必须补 zlib 包装。
    /// </summary>
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(
            output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        var deflated = output.ToArray();
        var zlib = new byte[2 + deflated.Length + 4];
        zlib[0] = 0x78; // CMF：deflate + 32K 窗口
        zlib[1] = 0x9C; // FLG：默认压缩级，无字典
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
