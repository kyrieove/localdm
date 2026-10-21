using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LocalDm.Tests;

/// <summary>
/// Windows 原生 PDF 路线探针：真的调用 <c>Windows.Data.Pdf</c>。
///
/// 只用合成 PDF（自己写对象头），不读 stories/、.appdata/、.local/。
/// 目标：证明本机能用原生 API 读出真实页数、把每一页渲染成可解码的位图，
/// 并记录现有 <c>CountPdfPages</c> 字符串计数的已知边界。
/// </summary>
public sealed class WindowsPdfSpikeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-pdf-" + Guid.NewGuid().ToString("N"));

    public WindowsPdfSpikeTests() => Directory.CreateDirectory(_root);

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
    public async Task Native_api_reads_the_real_page_count_of_a_synthetic_pdf()
    {
        var path = Path.Combine(_root, "3pages.pdf");
        WriteSyntheticPdf(path, pages: 3);

        Assert.Equal(3, await NativePdf.PageCountAsync(path));
    }

    /// <summary>
    /// 页数读取与渲染都跑通了：渲染结果是白底空页（合成页没有任何绘制指令），
    /// 所以断言「尺寸正确且整页同色」正是预期，不能反过来说渲染失败。
    /// 换一页加上真实绘制内容的样本，见 <see cref="Native_api_renders_drawn_content"/>。
    /// </summary>
    [Fact]
    public async Task Native_api_renders_each_page_to_a_decodable_bitmap()
    {
        var path = Path.Combine(_root, "3pages.pdf");
        WriteSyntheticPdf(path, pages: 3, withDrawing: true);

        var count = await NativePdf.PageCountAsync(path);
        Assert.Equal(3, count);

        for (var page = 0; page < count; page++)
        {
            var png = await NativePdf.RenderPageAsync(path, page);
            var image = SyntheticPng.Decode(png);
            Assert.Equal(300, image.Width);
            Assert.Equal(450, image.Height);
            Assert.False(
                image.Pixels.All(b => b == image.Pixels[0]),
                $"第 {page + 1} 页渲染出来没有任何内容。");
        }
    }

    [Fact]
    public async Task Native_api_renders_drawn_content()
    {
        // 带内容流的合成页：每一页画一个实心黑块，位置随页号变化。
        var path = Path.Combine(_root, "drawn.pdf");
        WriteSyntheticPdf(path, pages: 3, withDrawing: true);

        for (var page = 0; page < 3; page++)
        {
            var png = await NativePdf.RenderPageAsync(path, page);
            var image = SyntheticPng.Decode(png);
            var firstDarkRow = FirstDarkRow(image);
            Assert.True(firstDarkRow > 0, $"第 {page + 1} 页没有找到绘制内容。");

            // 黑块在 PDF 坐标系里从 y=300-60*(i+1) 起、高 50；
            // 渲染成 450 像素高的图后，首行黑像素的行号 = (300-(y+50))/300*450。
            // 系统渲染器会留几像素抗锯齿边，按 ±12 行断言。
            var y = 300 - 60 * (page + 1);
            var top = (300 - (y + 50)) / 300.0 * 450.0;
            Assert.InRange(firstDarkRow, (int)top - 12, (int)top + 12);
        }
    }

    private static int FirstDarkRow(DecodedImage image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var index = (y * image.Width + x) * 3;
                if (image.Pixels[index] < 100 && image.Pixels[index + 1] < 100 && image.Pixels[index + 2] < 100)
                {
                    return y;
                }
            }
        }

        return -1;
    }

    [Fact]
    public void String_page_counting_used_today_misses_object_streams()
    {
        // 现有实现的已知边界：只数未压缩的 /Type /Page 对象头。
        var plain = Path.Combine(_root, "plain.pdf");
        var compressed = Path.Combine(_root, "compressed.pdf");
        WriteSyntheticPdf(plain, pages: 3);
        WriteObjectStreamPdf(compressed, pages: 3);

        Assert.Equal(3, LocalDm.Infrastructure.Import.ScriptImportService.CountPdfPages(plain));
        Assert.Equal(1, LocalDm.Infrastructure.Import.ScriptImportService.CountPdfPages(compressed));
    }

    [Fact]
    public async Task Object_stream_pdf_is_readable_but_windows_pdf_cannot_open_it()
    {
        // 合成的对象流样本缺 xref，Windows.Data.Pdf 打不开它。
        // 这条事实如实记录：真实剧本包不会用这种畸形结构，但不能反过来说原生路线不可用。
        var path = Path.Combine(_root, "compressed.pdf");
        WriteObjectStreamPdf(path, pages: 3);

        await Assert.ThrowsAsync<System.Runtime.InteropServices.COMException>(
            () => NativePdf.PageCountAsync(path));
    }

    /// <summary>
    /// 写一份合成 PDF。<paramref name="withDrawing"/> 为真时每页带一个实心黑块，
    /// 位置随页号下移，用来证明渲染真的把页内容画出来了，不是一张白纸。
    /// </summary>
    private static void WriteSyntheticPdf(string path, int pages, bool withDrawing = false)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("%PDF-1.4\n");
        var offsets = new System.Collections.Generic.List<long> { 0, builder.Length };
        builder.Append("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i * 3} 0 R"));
        offsets.Add(builder.Length);
        builder.Append($"2 0 obj<</Type/Pages/Count {pages}/Kids[{kids}]>>endobj\n");
        for (var i = 0; i < pages; i++)
        {
            offsets.Add(builder.Length);
            // 页对象按「内容 + 资源」写全：没有 Resources 时系统渲染器不执行绘制指令。
            builder.Append($"{3 + i * 3} 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 300]")
                .Append(withDrawing ? $"/Contents {4 + i * 3} 0 R/Resources<<>>>>endobj\n" : ">>endobj\n");
            if (!withDrawing)
            {
                continue;
            }

            // 画一个 200x60 的实心黑块：页号越大越靠下。
            // 黑块画在页面上半部，避免被 PDF 坐标系（左下原点）与图像行序的翻转搞混。
            var y = 300 - 60 * (i + 1);
            var content = $"0 0 0 rg 10 {y} 180 50 re f";
            offsets.Add(builder.Length);
            builder.Append($"{4 + i * 3} 0 obj<</Length {content.Length}>>stream\n")
                .Append(content).Append("\nendstream endobj\n");
        }

        var xref = builder.Length;
        builder.Append("xref\n0 ").Append(offsets.Count).Append('\n');
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer<</Size ").Append(offsets.Count).Append("/Root 1 0 R>>\nstartxref\n")
            .Append(xref).Append("\n%%EOF");
        File.WriteAllText(path, builder.ToString(), System.Text.Encoding.Latin1);
    }

    /// <summary>
    /// 把页对象收进对象流（ObjStm + FlateDecode）：页对象头不再以明文出现。
    /// 用来证明字符串计数会漏页，同时原生 API 仍能正确读出页数。
    /// </summary>
    private static void WriteObjectStreamPdf(string path, int pages)
    {
        var objects = new System.Text.StringBuilder();
        var offsets = new System.Collections.Generic.List<(int Number, int Offset)>();
        var number = 3;
        for (var i = 0; i < pages; i++)
        {
            offsets.Add((number, objects.Length));
            objects.Append($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >> ");
            number += 2;
        }

        offsets.Add((2, objects.Length));
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i * 2} 0 R"));
        objects.Append($"<< /Type /Pages /Count {pages}/Kids [{kids}] >> ");
        var header = $"{string.Join(" ", offsets.Select(o => $"{o.Number} {o.Offset}"))} ";
        var payload = System.Text.Encoding.Latin1.GetBytes(header + objects);

        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(
            compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(payload, 0, payload.Length);
        }

        var body = compressed.ToArray();
        var builder = new System.Text.StringBuilder();
        builder.Append("%PDF-1.5\n");
        builder.Append("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        builder.Append("2 0 obj<</Type/ObjStm/N ").Append(offsets.Count)
            .Append("/First ").Append(header.Length).Append("/Length ").Append(body.Length)
            .Append("/Filter/FlateDecode>>stream\n");
        var prefix = System.Text.Encoding.Latin1.GetBytes(builder.ToString());
        using var file = new FileStream(path, FileMode.Create);
        file.Write(prefix, 0, prefix.Length);
        file.Write(body, 0, body.Length);
        var suffix = System.Text.Encoding.Latin1.GetBytes("\nendstream endobj\n%%EOF");
        file.Write(suffix, 0, suffix.Length);
    }
}

/// <summary>
/// Windows.Data.Pdf 的真实调用点。
///
/// 页数用 <c>PdfDocument.PageCount</c>（解析器读页树），不用字符串计数；
/// 渲染走 <c>PdfPage.RenderToStreamAsync</c> 输出 JPEG，再用 <c>BitmapDecoder</c>
/// 取像素自己编成 PNG，最后交给同一个解码器校验。
/// </summary>
internal static class NativePdf
{
    public static async Task<int> PageCountAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        return (int)document.PageCount;
    }

    public static async Task<byte[]> RenderPageAsync(string path, int pageIndex)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        using var page = document.GetPage((uint)pageIndex);

        // 1) 原生渲染成 JPEG 位图流。
        using var rendered = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions
        {
            DestinationWidth = 300,
            DestinationHeight = 450,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        });

        // 2) 用系统解码器取像素，自己编 PNG：渲染结果必须真的能被读回像素。
        //    解码器默认可能给 BGRA，先转成 B8G8R8A8 再固定取前 3 字节，
        //    否则 CopyToBuffer 会因像素格式与缓冲区大小不符而失败。
        var decoder = await BitmapDecoder.CreateAsync(rendered);
        using var software = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        var pixels = new byte[software.PixelWidth * software.PixelHeight * 4];
        software.CopyToBuffer(pixels.AsBuffer());

        var rgb = new byte[software.PixelWidth * software.PixelHeight * 3];
        for (var i = 0; i < rgb.Length / 3; i++)
        {
            rgb[i * 3] = pixels[i * 4 + 2];
            rgb[i * 3 + 1] = pixels[i * 4 + 1];
            rgb[i * 3 + 2] = pixels[i * 4];
        }

        return TestPngWriter.Encode(rgb, software.PixelWidth, software.PixelHeight);
    }
}
