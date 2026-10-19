using System.Text.Json;

namespace LocalDm.Core.Import;

/// <summary>
/// 应用自己的 formatVersion=1 提取包（document-package.md）。
/// 只接受 formatVersion=1、源 hash 匹配、Complete、页覆盖完整且关键事实有来源的包。
/// </summary>
public static class PreparedPackageReader
{
    public const int SupportedFormatVersion = 1;

    /// <summary>
    /// 读取并校验已准备包。返回校验结论；不通过时给出脱敏原因。
    /// </summary>
    public static PreparedPackageReadResult Read(string json)
    {
        PreparedPackageDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PreparedPackageDto>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return PreparedPackageReadResult.Reject("PACKAGE_INVALID_JSON", "应用包不是合法 JSON。");
        }

        if (dto is null)
        {
            return PreparedPackageReadResult.Reject("PACKAGE_INVALID_JSON", "应用包为空。");
        }

        if (dto.FormatVersion != SupportedFormatVersion)
        {
            return PreparedPackageReadResult.Reject(
                "PACKAGE_VERSION_UNSUPPORTED",
                $"只接受 formatVersion={SupportedFormatVersion} 的应用包。");
        }

        if (dto.Completion != "Complete")
        {
            return PreparedPackageReadResult.Reject(
                "PACKAGE_INCOMPLETE",
                "应用包标记为未完成：仍有未处理的输入或未完成的续读请求。");
        }

        if (dto.SourceFiles.Count == 0 || dto.Spans.Count == 0)
        {
            return PreparedPackageReadResult.Reject(
                "PACKAGE_MISSING_SOURCE",
                "应用包缺少源文件或页块来源。");
        }

        // 页覆盖：每个源文件都必须有记录，且已处理页数等于页数
        foreach (var file in dto.SourceFiles)
        {
            if (file.PageCount <= 0)
            {
                return PreparedPackageReadResult.Reject("PACKAGE_COVERAGE_INVALID", "源文件页数不合法。");
            }

            var processed = dto.Spans
                .Where(s => s.FileId == file.FileId)
                .Select(s => s.PageNumber)
                .Distinct()
                .Count();
            if (processed != file.PageCount)
            {
                return PreparedPackageReadResult.Reject(
                    "PACKAGE_COVERAGE_INCOMPLETE",
                    "源文件的页覆盖不完整。");
            }
        }

        // 页块必须回指存在的文件与页
        foreach (var span in dto.Spans)
        {
            var file = dto.SourceFiles.FirstOrDefault(f => f.FileId == span.FileId);
            if (file is null || span.PageNumber < 1 || span.PageNumber > file.PageCount)
            {
                return PreparedPackageReadResult.Reject(
                    "PACKAGE_SOURCE_MISMATCH",
                    "页块来源引用了不存在的文件或页。");
            }
        }

        return PreparedPackageReadResult.Accept(dto);
    }

    /// <summary>
    /// 只接受应用包，不接受 Markdown 或 MinerU 私有缓存冒充。
    /// </summary>
    public static PreparedPackageReadResult ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return PreparedPackageReadResult.Reject("PACKAGE_MISSING", "找不到应用包文件。");
        }

        var extension = Path.GetExtension(path);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return PreparedPackageReadResult.Reject(
                "PACKAGE_FORMAT_REJECTED",
                "只接受 .json 应用包；Markdown 或其他格式不能冒充完整包。");
        }

        return Read(File.ReadAllText(path));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public sealed class PreparedPackageDto
    {
        public int FormatVersion { get; set; } = -1;

        public string ToolIdentity { get; set; } = string.Empty;

        public string Completion { get; set; } = string.Empty;

        public List<SourceFileDto> SourceFiles { get; set; } = [];

        public List<SourceSpanDto> Spans { get; set; } = [];

        public List<BatchDto> Batches { get; set; } = [];
    }

    public sealed class SourceFileDto
    {
        public string FileId { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public int PageCount { get; set; }

        public string? PrivatePath { get; set; }
    }

    public sealed class SourceSpanDto
    {
        public string SpanId { get; set; } = string.Empty;

        public string FileId { get; set; } = string.Empty;

        public int PageNumber { get; set; }

        public int BlockNumber { get; set; }

        public string ToolLocator { get; set; } = string.Empty;

        public string OriginalText { get; set; } = string.Empty;
    }

    public sealed class BatchDto
    {
        public string Locator { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public int Start { get; set; }

        public int End { get; set; }

        public string PageRange { get; set; } = string.Empty;
    }
}

public sealed record PreparedPackageReadResult(
    bool Accepted,
    string? ErrorCode,
    string? PublicReason,
    PreparedPackageReader.PreparedPackageDto? Package)
{
    public static PreparedPackageReadResult Accept(PreparedPackageReader.PreparedPackageDto dto) =>
        new(true, null, null, dto);

    public static PreparedPackageReadResult Reject(string code, string reason) => new(false, code, reason, null);
}
