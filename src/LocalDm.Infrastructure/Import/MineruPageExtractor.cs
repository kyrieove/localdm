using System.Runtime.Versioning;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Documents;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 真实的识别实现：MinerU 轻量档 + 备用 OCR 兜底。
///
/// 隐私预检、数据授权与续读都在 <see cref="ImportPipeline"/> 里，这一层只负责
/// 「把哪个文件、按哪种文档类型、带哪份授权交给它」。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MineruPageExtractor : IPageExtractor, IDisposable
{
    private readonly MineruRunner _runner;
    private readonly ImportPipeline _pipeline;
    private ToolIdentity? _identity;

    public MineruPageExtractor(string? executable, string appOwnedHome, string? ocrPythonPath)
    {
        _runner = new MineruRunner(executable ?? DefaultMineruExecutable(), appOwnedHome);
        var fallback = new RapidOcrFallback(ResolveOcrPython(ocrPythonPath), RapidOcrFallback.DefaultScriptPath());
        _pipeline = new ImportPipeline(_runner, new MineruPrivacyPreflight(appOwnedHome), fallback);
    }

    public async Task<ToolIdentity> GetIdentityAsync(CancellationToken cancellationToken) =>
        _identity ??= await _runner.GetIdentityAsync(cancellationToken);

    public Task<ImportFileResult> ExtractAsync(
        string fileId,
        string absolutePath,
        DataConsent consent,
        CancellationToken cancellationToken)
    {
        var isPdf = string.Equals(Path.GetExtension(absolutePath), ".pdf", StringComparison.OrdinalIgnoreCase);
        return _pipeline.ImportFileAsync(
            fileId,
            Path.GetFullPath(absolutePath),
            isPdf ? DocumentKind.ScanPdf : DocumentKind.Image,
            pdfPageCount: isPdf ? ScriptImportService.CountPdfPages(absolutePath) : null,
            consent,
            cancellationToken,
            manifestHash: consent.SourceManifestHash,
            tier: ParseTier.Flash);
    }

    public void Dispose() => _runner.Dispose();

    /// <summary>MinerU 的默认位置；与命令行原来的默认值一致。</summary>
    private static string DefaultMineruExecutable() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "mineru.exe");

    /// <summary>
    /// 备用 OCR 的解释器：设置里的路径 → <c>LOCALDM_OCR_PYTHON</c> → <c>python</c>。
    /// 前两者只在文件真的存在时采用，免得把一条已失效的旧路径一直带下去。
    /// </summary>
    private static string ResolveOcrPython(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("LOCALDM_OCR_PYTHON");
        return !string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment)
            ? fromEnvironment
            : "python";
    }
}
