using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LocalDm.Tests;

/// <summary>
/// 测试侧的最小 PNG 编码器：把原生渲染取回的像素编成 PNG，
/// 再交给 <see cref="SyntheticPng"/> 解码校验。
/// 与探针应用里的编码器是两处：应用那边要生成资产，这边只做校验用的往返转换。
/// </summary>
internal static class TestPngWriter
{
    public static byte[] Encode(byte[] rgb, int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var target = y * (1 + width * 3);
            raw[target] = 0;
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
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 8), height);
        header[8] = 8;
        header[9] = 2;
        return header;
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, (int)data.Length);
        stream.Write(length);
        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }

        Array.Copy(data, 0, typed, 4, data.Length);
        stream.Write(typed);

        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        stream.Write(crc);
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

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
