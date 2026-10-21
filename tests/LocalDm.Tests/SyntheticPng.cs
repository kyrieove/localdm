using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LocalDm.Tests;

/// <summary>解码后的合成图片：尺寸与关键像素，用于断言图片真实可读。</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>左上角标记块的像素（合成图固定为黑）。</summary>
    public (byte R, byte G, byte B) MarkerPixel => (Pixels[0], Pixels[1], Pixels[2]);
}

/// <summary>
/// 最小 PNG 解码器：只支持 8 位真彩色、无交错。
///
/// 存在的理由：探针早期写过 9 字节假 PNG，测试只断言「字节非空且不同」，
/// 于是裂图也算通过。现在测试必须真的解码，浏览器能不能显示才有依据。
/// </summary>
public static class SyntheticPng
{
    public static DecodedImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        Assert.True(
            png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }),
            "不是有效 PNG：签名不对。");

        return Decode(png, out _, out _);
    }

    /// <summary>解码并顺带返回位深与颜色类型，便于调用方区分 RGB/RGBA。</summary>
    public static DecodedImage Decode(byte[] png, out int colorType, out int bitDepth)
    {
        colorType = 0;
        bitDepth = 0;
        ArgumentNullException.ThrowIfNull(png);
        Assert.True(
            png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }),
            "不是有效 PNG：签名不对。");

        var position = 8;
        int? width = null;
        int? height = null;
        var idat = new MemoryStream();
        while (position + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position, 4));
            var type = Encoding.ASCII.GetString(png, position + 4, 4);
            var body = png.AsSpan(position + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + length, 4));
            Assert.Equal(Crc32(png.AsSpan(position + 4, 4 + length)), crc);

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..8]);
                bitDepth = body[8];
                colorType = body[9];
                Assert.Equal(8, bitDepth);
                Assert.Contains(colorType, new[] { 2, 6 }); // truecolor 或 RGBA
                Assert.Equal(0, body[12]);                      // 无交错
            }
            else if (type == "IDAT")
            {
                idat.Write(body);
            }
            else if (type == "IEND")
            {
                break;
            }

            position += 12 + length;
        }

        Assert.True(width is > 0 && height is > 0, "PNG 缺少 IHDR。");

        // IDAT 里是 zlib 流：前 2 字节是 zlib 头，末尾 4 字节是 Adler-32。
        // 跳过头部再交给 DeflateStream；WIC 容错、浏览器严格，所以必须按规范解。
        var idatBytes = idat.ToArray();
        Assert.True(idatBytes.Length > 6, "IDAT 太短，不是压缩流。");
        // IDAT 可能是 zlib 包装（2 字节头）或裸 deflate（无头）。
        // 两种都能被 DeflateStream 解：先试跳过 2 字节，失败再按裸 deflate 解。
        byte[] bytes;
        try
        {
            using var zlibStream = new MemoryStream(idatBytes, 2, idatBytes.Length - 2, writable: false);
            using var zlib = new DeflateStream(zlibStream, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            zlib.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (InvalidDataException)
        {
            using var rawStream = new MemoryStream(idatBytes, writable: false);
            using var raw = new DeflateStream(rawStream, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            raw.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        var w = width!.Value;
        var h = height!.Value;
        var channels = colorType == 6 ? 4 : 3;
        var stride = 1 + w * channels;
        Assert.Equal(h * stride, bytes.Length);

        var pixels = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            Assert.Equal(0, bytes[y * stride]); // filter: none
            // RGBA 时丢掉 alpha，只保留 RGB：合成图与原生渲染都能统一比较。
            for (var x = 0; x < w; x++)
            {
                var source = y * stride + 1 + x * channels;
                var target = (y * w + x) * 3;
                pixels[target] = bytes[source];
                pixels[target + 1] = bytes[source + 1];
                pixels[target + 2] = bytes[source + 2];
            }
        }

        return new DecodedImage(w, h, pixels);
    }

    private static readonly uint[] Table = BuildTable();

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

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
