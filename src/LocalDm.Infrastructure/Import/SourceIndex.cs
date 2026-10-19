using System.Security.Cryptography;
using System.Text;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 源索引：把文件 hash、真实页块与 locator 归一化。
/// 图片各自 page=1 且顺序完全由 manifest 指定，不猜剧情排序。
/// </summary>
public static class SourceIndex
{
    /// <summary>
    /// 从实际文件构建 SourceFile：计算 SHA-256 与页数。
    /// PDF 页数用文件内页数；图片固定 1 页。
    /// </summary>
    public static SourceFile BuildSourceFile(
        string fileId,
        string absolutePath,
        DocumentKind? kind,
        int? pdfPageCount,
        SourceOrder order,
        string toolIdentity)
    {
        if (!File.Exists(absolutePath))
        {
            throw new DomainRuleException(DomainErrorCode.MISSING_SOURCE, "找不到源文件。");
        }

        var pageCount = kind == DocumentKind.Image ? 1 : pdfPageCount ?? 1;
        var file = new SourceFile
        {
            FileId = fileId,
            PrivatePath = absolutePath,
            ContentHash = ComputeFileHash(absolutePath),
            PageCount = pageCount,
            Origin = order,
            ToolIdentity = toolIdentity,
            ParseState = FileParseState.Pending,
        };
        file.Validate();
        return file;
    }

    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string ComputeTextHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// 把一次提取批次展开成页块。locator 形如 doc:ID/tier:TIER/page:N/block:N(/char:OFFSET)。
    /// 解析不出页号时抛源失配，绝不猜测。
    /// </summary>
    public static IReadOnlyList<SourceSpan> BuildSpans(SourceFile file, IEnumerable<ExtractionBatch> batches)
    {
        var spans = new List<SourceSpan>();
        var index = 0;
        foreach (var batch in batches)
        {
            var (page, block) = ParseLocator(batch.Locator);
            if (page < 1 || page > file.PageCount)
            {
                throw new DomainRuleException(
                    DomainErrorCode.SOURCE_MISMATCH,
                    "提取结果的页号超出源文件页数。");
            }

            var text = batch.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            spans.Add(new SourceSpan
            {
                SpanId = $"{file.FileId}-p{page}-b{block}-{index}",
                FileId = file.FileId,
                PageNumber = page,
                BlockNumber = block,
                ToolLocator = batch.Locator,
                TextHash = ComputeTextHash(text),
                OriginalText = text,
            });
            index++;
        }

        return spans;
    }

    /// <summary>
    /// 解析白名单 locator；不接受来自结果的任意字符串当命令行。
    /// </summary>
    public static (int Page, int Block) ParseLocator(string locator)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            locator,
            @"^doc:[A-Za-z0-9_\-]+/tier:[A-Za-z0-9_\-]+/page:(?<page>[0-9]+)(/block:(?<block>[0-9]+))?(/char:[0-9]+)?$");
        if (!match.Success)
        {
            throw new DomainRuleException(DomainErrorCode.SOURCE_MISMATCH, "定位符格式不在白名单内。");
        }

        var page = int.Parse(match.Groups["page"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var block = match.Groups["block"].Success
            ? int.Parse(match.Groups["block"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        return (page, block);
    }

    /// <summary>覆盖核对：每个文件每一页都必须有来源。</summary>
    public static IReadOnlyList<string> FindUncoveredPages(
        IReadOnlyList<SourceFile> files,
        IReadOnlyList<SourceSpan> spans)
    {
        var missing = new List<string>();
        foreach (var file in files)
        {
            var covered = spans
                .Where(s => s.FileId == file.FileId)
                .Select(s => s.PageNumber)
                .ToHashSet();
            for (var page = 1; page <= file.PageCount; page++)
            {
                if (!covered.Contains(page))
                {
                    missing.Add($"{file.FileId}#{page}");
                }
            }
        }

        return missing;
    }
}
