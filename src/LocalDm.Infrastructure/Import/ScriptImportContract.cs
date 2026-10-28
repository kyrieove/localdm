using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 一页的识别入口。真实实现走 MinerU（带隐私预检与授权），测试实现给固定文本。
///
/// 单独抽出来，是因为命令行与界面共用同一份导入服务，而两边的测试都不该真的启动 OCR。
/// </summary>
public interface IPageExtractor
{
    /// <summary>识别工具身份；隐私预检与来源标注都要用它。</summary>
    Task<ToolIdentity> GetIdentityAsync(CancellationToken cancellationToken);

    /// <summary>识别一页。失败或未获授权时返回对应状态，不抛异常（取消除外）。</summary>
    Task<ImportFileResult> ExtractAsync(
        string fileId,
        string absolutePath,
        DataConsent consent,
        CancellationToken cancellationToken);
}

/// <summary>导入服务的装配参数。数据根与工具路径都从这里来，服务本身不读设置文件。</summary>
public sealed record ScriptImportOptions
{
    /// <summary>运行数据根：包文件、存档库与识别缓存都放这里。</summary>
    public required string DataRoot { get; init; }

    /// <summary>MinerU 可执行文件；为空时用默认路径。</summary>
    public string? MineruExecutable { get; init; }

    /// <summary>备用 OCR 的 Python 解释器；为空时退回 <c>LOCALDM_OCR_PYTHON</c>，再退回 <c>python</c>。</summary>
    public string? OcrPythonPath { get; init; }

    /// <summary>应用专属 MINERU_HOME；为空时用 <c>&lt;DataRoot&gt;/mineru-home</c>。</summary>
    public string? MineruHome { get; init; }

    /// <summary>测试注入的识别实现；为空时用真实的 MinerU 管线。</summary>
    public IPageExtractor? Extractor { get; init; }
}

/// <summary>识别进度：界面只显示第几页、共几页、失败几页，不显示任何识别文字。</summary>
public sealed record ImportProgress
{
    public required int Completed { get; init; }

    public required int Total { get; init; }

    /// <summary>刚处理完的页标签（文件名，不含正文）。</summary>
    public required string PageLabel { get; init; }

    public int FailedCount { get; init; }

    /// <summary>按已用时间估的剩余时间；还没估出来时为 null。</summary>
    public TimeSpan? EstimatedRemaining { get; init; }
}

/// <summary>
/// 一次识别的结果：页、来源文件与失败页。
///
/// 这里只放已经过隐私预检与授权的数据；被拦住时 <see cref="BlockedReason"/> 非空，
/// 其余字段为空，界面据此停在第三步。
/// </summary>
public sealed record ScriptExtraction
{
    public required IReadOnlyList<RealScriptPackageBuilder.ExtractedPage> Pages { get; init; }

    public required IReadOnlyList<SourceFile> Sources { get; init; }

    /// <summary>识别工具报错的文件（分组-文件名）：这些文件的内容没进包，不能建包。</summary>
    public IReadOnlyList<string> FailedPages { get; init; } = [];

    /// <summary>工具正常跑完但没识别出文字的文件（分组-文件名）：多半是插图或空白页，要人确认后才放行。</summary>
    public IReadOnlyList<string> BlankFiles { get; init; } = [];

    /// <summary>隐私预检或授权未通过时的脱敏原因；非空表示这次识别被挡住。</summary>
    public string? BlockedReason { get; init; }

    /// <summary>预检给出的错误码，界面只显示类别。</summary>
    public string? BlockedCode { get; init; }

    public bool IsBlocked => !string.IsNullOrEmpty(BlockedReason);

    public static ScriptExtraction Blocked(string code, string reason) => new()
    {
        Pages = [],
        Sources = [],
        BlockedCode = code,
        BlockedReason = reason,
    };
}

/// <summary>建包结果：包文件与流程文件的位置。</summary>
public sealed record ScriptImportResult
{
    public required RealScriptPackageBuilder.BuildResult Build { get; init; }

    public required string PackagePath { get; init; }

    /// <summary>流程声明文件的路径；以后「重新整理流程」从这里读。</summary>
    public required string FlowPath { get; init; }
}
