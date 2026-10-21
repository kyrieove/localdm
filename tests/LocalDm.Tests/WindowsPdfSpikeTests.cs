using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalDm.Tests;

/// <summary>
/// Windows 原生 PDF 路线探针：页数读取与逐页渲染。
///
/// 只用合成 PDF（自己写对象头），不读 stories/、.appdata/、.local/。
/// 目标是判断 Windows.Data.Pdf 能否在本机跑通、发布物要不要额外依赖。
/// 现有 <c>ScriptImportService.CountPdfPages</c> 是字符串计数，
/// 按方案要求不能把它当作最终依据，这里给出原生路线的事实。
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
    public void String_page_counting_used_today_misses_compressed_pdfs()
    {
        // 现有实现的已知边界：只认未压缩的 /Type /Page 对象头。
        // 用对象流压缩的同一份 3 页 PDF，它数不出来 —— 这正是原生路线要解决的问题。
        var compressed = Path.Combine(_root, "compressed.pdf");
        WriteCompressedPdf(compressed, pages: 3);

        var stringCount = LocalDm.Infrastructure.Import.ScriptImportService.CountPdfPages(compressed);
        Assert.Equal(1, stringCount);
    }

    [Fact]
    public void Native_pdf_page_count_is_not_wired_yet_and_reports_the_reason()
    {
        var path = Path.Combine(_root, "3pages.pdf");
        WriteSyntheticPdf(path, pages: 3);

        var probe = new WindowsPdfProbe();
        var result = probe.CountPages(path);

        // 路线尚未接入：如实记录不可用与原因，不伪造页数、不静默退回字符串计数。
        Assert.False(result.Available, "投影已接入，请把断言改成页数校验。");
        Assert.Contains("未接入", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_pdf_render_is_not_wired_yet_and_reports_the_reason()
    {
        var path = Path.Combine(_root, "2pages.pdf");
        WriteSyntheticPdf(path, pages: 2);

        var probe = new WindowsPdfProbe();
        var count = probe.CountPages(path);
        Assert.False(count.Available);

        var rendered = probe.RenderPage(path, 0);
        Assert.False(rendered.Available);
        Assert.Contains("未接入", rendered.Reason, StringComparison.Ordinal);
    }

    /// <summary>写一份只有页对象的合成 PDF：足够验证页数读取与渲染路径。</summary>
    private static void WriteSyntheticPdf(string path, int pages)
    {
        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");
        var offsets = new List<long> { 0, builder.Length };
        builder.Append("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i * 2} 0 R"));
        offsets.Add(builder.Length);
        builder.Append($"2 0 obj<</Type/Pages/Count {pages}/Kids[{kids}]>>endobj\n");
        for (var i = 0; i < pages; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{3 + i * 2} 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 300]>>endobj\n");
            offsets.Add(builder.Length);
            builder.Append($"{4 + i * 2} 0 obj<</Length 0>>stream\nendstream endobj\n");
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
        File.WriteAllText(path, builder.ToString(), Encoding.Latin1);
    }

    /// <summary>
    /// 把页对象塞进对象流（ObjStm）并压缩：交叉引用流压缩过的 PDF 形态。
    /// 页对象头不再以明文出现，字符串计数数不到。
    /// </summary>
    private static void WriteCompressedPdf(string path, int pages)
    {
        var objects = new StringBuilder();
        var offsets = new List<(int Number, int Offset)>();
        var cursor = 1;
        for (var i = 0; i < pages; i++)
        {
            offsets.Add((cursor, objects.Length));
            objects.Append($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >> ");
            cursor += 2;
        }

        offsets.Add((cursor, objects.Length));
        objects.Append($"<< /Type /Pages /Count {pages} /Kids [0] >> ");
        var first = offsets[0].Number;
        var header = $"{string.Join(" ", offsets.Select(o => $"{o.Number} {o.Offset}"))} ";

        var payload = Encoding.Latin1.GetBytes(header + objects);
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(payload, 0, payload.Length);
        }

        var compressedBytes = compressed.ToArray();
        var builder = new StringBuilder();
        builder.Append("%PDF-1.5\n");
        builder.Append("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        builder.Append("2 0 obj<</Type/ObjStm/N ").Append(offsets.Count)
            .Append("/First ").Append(header.Length).Append("/Length ").Append(compressedBytes.Length)
            .Append("/Filter/FlateDecode>>stream\n");
        var prefix = Encoding.Latin1.GetBytes(builder.ToString());
        using var file = new FileStream(path, FileMode.Create);
        file.Write(prefix, 0, prefix.Length);
        file.Write(compressedBytes, 0, compressedBytes.Length);
        var suffix = Encoding.Latin1.GetBytes("\nendstream endobj\n%%EOF");
        file.Write(suffix, 0, suffix.Length);
    }
}

/// <summary>Windows.Data.Pdf 的调用结果：可用性、页数与脱敏原因。</summary>
public sealed record PdfCountResult(bool Available, int PageCount, string? Reason);

/// <summary>单页渲染结果：可用性与产出字节数。</summary>
public sealed record PdfRenderResult(bool Available, int ByteLength, string? Reason);

/// <summary>
/// 原生 PDF 探针。Windows.Data.Pdf 是 WinRT API，需要 Windows SDK 目标与 CsWinRT 投影；
/// 本类把「能不能调」与「调出来什么」分开报告，不静默退回字符串计数。
/// </summary>
public sealed class WindowsPdfProbe
{
    public PdfCountResult CountPages(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new PdfCountResult(false, 0, "不是 Windows 平台");
        }

        try
        {
            var count = NativePdf.PageCount(path);
            return new PdfCountResult(true, count, null);
        }
        catch (Exception ex)
        {
            return new PdfCountResult(false, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public PdfRenderResult RenderPage(string path, int pageIndex)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new PdfRenderResult(false, 0, "不是 Windows 平台");
        }

        try
        {
            var bytes = NativePdf.RenderPage(path, pageIndex);
            return new PdfRenderResult(true, bytes, null);
        }
        catch (Exception ex)
        {
            return new PdfRenderResult(false, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }
}

/// <summary>
/// WinRT 调用点。Windows.Data.Pdf 在本机 SDK 下需要 Windows SDK 目标与 CsWinRT，
/// 这里用一个明确的失败点代替假成功：路线未落地前不伪造页数。
/// </summary>
internal static class NativePdf
{
    public static int PageCount(string path) =>
        throw new NotSupportedException("Windows.Data.Pdf 投影未接入本工程，当前为待落地路线。");

    public static int RenderPage(string path, int pageIndex) =>
        throw new NotSupportedException("Windows.Data.Pdf 投影未接入本工程，当前为待落地路线。");
}
